using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;
using WireHQ.Application.Abstractions;
using WireHQ.Application.Abstractions.Persistence;
using WireHQ.Infrastructure.Auditing;
using WireHQ.Infrastructure.Persistence.Seeding;

namespace WireHQ.Infrastructure.Persistence;

/// <summary>
/// Applies migrations + the RLS policies, then seeds reference data. In production this is invoked by a
/// dedicated migrations job that must complete before the API starts (see docs/10-deployment.md).
///
/// Two roles (ADR-027): schema DDL — migrations, the RLS policy script, and the <c>wirehq_app</c> password
/// sync — run on the privileged <c>Admin</c> connection (owner); everything the request pipeline does
/// (incl. seeding) runs on the runtime <c>Default</c> connection as the non-privileged <c>wirehq_app</c>,
/// which is subject to RLS. The seeders therefore run with the tenant bypass set (see Program.cs).
/// </summary>
public sealed class ApplicationDbContextInitializer(
    IEnumerable<IDataSeeder> seeders,
    ITenantContext tenant,
    IEnumerable<IModelConfigurationContributor> modelContributors,
    IConfiguration configuration,
    ILogger<ApplicationDbContextInitializer> logger)
{
    private const string AppRole = "wirehq_app";

    public async Task InitialiseAsync(CancellationToken cancellationToken = default)
    {
        // DDL (migrate + RLS) runs on the owner/admin connection — the runtime wirehq_app role is a
        // non-owner and can neither create the schema nor define policies.
        await using var admin = CreateAdminContext();

        await WaitForDatabaseAsync(admin, cancellationToken);

        // Once migrations are generated (`dotnet ef migrations add InitialCreate`) this applies them.
        // Until then — e.g. a brand-new clone — fall back to EnsureCreated so the app runs immediately in
        // development. The two are mutually exclusive; production always migrates.
        if (admin.Database.GetMigrations().Any())
        {
            logger.LogInformation("Applying database migrations...");
            await admin.Database.MigrateAsync(cancellationToken);
            logger.LogInformation("Migrations applied.");
        }
        else
        {
            logger.LogWarning(
                "No EF migrations found — creating schema via EnsureCreated (development only). " +
                "Generate migrations with: dotnet ef migrations add InitialCreate -p src/WireHQ.Infrastructure -s src/WireHQ.Api");
            await admin.Database.EnsureCreatedAsync(cancellationToken);
        }

        await ApplyRowLevelSecurityAsync(admin, cancellationToken);
        await SyncAppRolePasswordAsync(admin, cancellationToken);
        await BackfillAuditChainAsync(admin, cancellationToken);

        // Keep the audit_logs partitions ahead of "now" so an insert always finds its month (the migration
        // seeds them at migrate time; this covers a boot that happens months later). Runs as the owner, after
        // RLS so new partitions inherit the append-only default privileges. (ADR-031, docs/15 §5)
        // Skipped when audit_logs is a plain table: the partition DDL is raw SQL invisible to the EF model,
        // so an EnsureCreated baseline or the Community Edition's squashed InitialCreate has none (docs/17 §7).
        if (await AuditPartitions.IsPartitionedAsync(admin, cancellationToken))
        {
            await AuditPartitions.EnsureMonthlyPartitionsAsync(admin, DateTime.UtcNow, monthsAhead: 3, cancellationToken);
        }
        else
        {
            logger.LogInformation(
                "audit.audit_logs is not partitioned (EnsureCreated or a squashed baseline) — skipping partition management.");
        }
    }

    /// <summary>
    /// Links any audit rows written before the hash-chain migration into their tenant's chain (ADR-031),
    /// so tamper-evidence covers the full history — not just rows written after the upgrade. Runs on the
    /// owner connection (the append-only grant blocks the app role from UPDATE) and reuses the runtime
    /// canonical hashing, so backfilled and live rows verify identically. Idempotent: once every row has an
    /// <c>entry_hash</c> this is a no-op, so it's safe on every boot. (docs/15 §5)
    /// </summary>
    private async Task BackfillAuditChainAsync(ApplicationDbContext admin, CancellationToken cancellationToken)
    {
        if (!await admin.AuditLogs.AnyAsync(a => a.EntryHash == null, cancellationToken))
        {
            return;
        }

        logger.LogInformation("Backfilling the audit hash chain for pre-existing rows...");

        // Ordered by tenant then chain order so GroupBy yields each chain's entries in (occurred-at, id) order.
        var rows = await admin.AuditLogs
            .OrderBy(a => a.OrganizationId)
            .ThenBy(a => a.OccurredAtUtc)
            .ThenBy(a => a.Id)
            .ToListAsync(cancellationToken);

        foreach (var chain in rows.GroupBy(a => a.OrganizationId))
        {
            byte[]? prev = null;
            foreach (var entry in chain)
            {
                if (entry.EntryHash is not null)
                {
                    prev = entry.EntryHash; // Already chained (a resumed/partial backfill) — keep walking.
                    continue;
                }

                var entryHash = AuditChain.ComputeEntryHash(prev, entry);
                entry.ApplyChain(prev, entryHash);
                prev = entryHash;
            }
        }

        var chained = await admin.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Audit hash chain backfill complete ({Count} rows chained).", chained);
    }

    /// <summary>
    /// Applies the Row-Level Security policies (Layer 2 tenant isolation, ADR-027) after the schema is
    /// current. Data-driven + idempotent, so it covers any tenant table the latest migration added (and
    /// re-grants <c>wirehq_app</c> on the latest tables) — safe to run on every boot. Runs as the owner.
    /// (docs/03-multi-tenancy.md)
    /// </summary>
    private async Task ApplyRowLevelSecurityAsync(ApplicationDbContext admin, CancellationToken cancellationToken)
    {
        logger.LogInformation("Applying Row-Level Security policies...");
        await admin.Database.ExecuteSqlRawAsync(ReadEmbeddedRlsScript(), cancellationToken);
        logger.LogInformation("Row-Level Security policies applied.");
    }

    /// <summary>
    /// Sets the <c>wirehq_app</c> role password to match the runtime <c>Default</c> connection string, so
    /// the single source of truth for the app credential is configuration (not the committed RLS script).
    /// No-op unless the app is actually configured to connect as <c>wirehq_app</c>.
    /// </summary>
    private async Task SyncAppRolePasswordAsync(ApplicationDbContext admin, CancellationToken cancellationToken)
    {
        var defaultConnectionString = configuration.GetConnectionString("Default");
        if (string.IsNullOrWhiteSpace(defaultConnectionString))
        {
            return;
        }

        var builder = new NpgsqlConnectionStringBuilder(defaultConnectionString);
        if (!string.Equals(builder.Username, AppRole, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrEmpty(builder.Password))
        {
            return; // The app isn't connecting as wirehq_app (e.g. legacy single-role config) — nothing to sync.
        }

        // ALTER ROLE can't be parameterized (it's DDL). builder.Password is our own configuration value,
        // not user input; escape the literal defensively and build the statement without interpolation
        // (so it isn't a templated ExecuteSqlRaw that EF1002 would — rightly — flag for general use).
        var escaped = builder.Password.Replace("'", "''");
        var alterRole = "ALTER ROLE " + AppRole + " WITH LOGIN PASSWORD '" + escaped + "';";
        await admin.Database.ExecuteSqlRawAsync(alterRole, cancellationToken);
        logger.LogInformation("Synced the {Role} role password from the Default connection string.", AppRole);
    }

    private ApplicationDbContext CreateAdminContext()
    {
        var adminConnectionString =
            configuration.GetConnectionString("Admin")
            ?? configuration.GetConnectionString("Default")
            ?? throw new InvalidOperationException("No 'Admin' or 'Default' connection string is configured.");

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(adminConnectionString, npgsql =>
            {
                npgsql.MigrationsAssembly(typeof(ApplicationDbContext).Assembly.FullName);
                npgsql.MigrationsHistoryTable("__ef_migrations_history", "core");
            })
            .UseSnakeCaseNamingConvention()
            .Options;

        // No tenant interceptor here: this context only does owner DDL, never tenant-scoped DML.
        return new ApplicationDbContext(options, tenant, modelContributors);
    }

    private static string ReadEmbeddedRlsScript()
    {
        var assembly = typeof(ApplicationDbContextInitializer).Assembly;
        var resourceName = Array.Find(assembly.GetManifestResourceNames(), n => n.EndsWith("rls.sql", StringComparison.Ordinal))
            ?? throw new InvalidOperationException("Embedded RLS script (rls.sql) was not found in the Infrastructure assembly.");

        using var stream = assembly.GetManifestResourceStream(resourceName)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// Waits (with backoff) for the database to accept connections before migrating, so a slow or
    /// briefly-unreachable database during startup doesn't crash the API. depends_on(healthy) covers
    /// the normal case; this covers transient races and DNS readiness.
    /// </summary>
    private async Task WaitForDatabaseAsync(ApplicationDbContext context, CancellationToken cancellationToken)
    {
        const int maxAttempts = 12;
        var delay = TimeSpan.FromSeconds(3);
        Exception? lastError = null;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                // Open a REAL connection rather than CanConnectAsync — the latter swallows the exception and returns a
                // bare false, which is exactly why a wrong password used to surface as a useless "not reachable". A
                // successful open+close proves the DB is up AND the credentials are accepted.
                await context.Database.OpenConnectionAsync(cancellationToken);
                await context.Database.CloseConnectionAsync();
                return;
            }
            catch (Exception ex)
            {
                lastError = ex;
                logger.LogWarning("Waiting for database (attempt {Attempt}/{Max}): {Error}", attempt, maxAttempts, ex.Message);
            }

            await Task.Delay(delay, cancellationToken);
        }

        // Persistent failure. If the server rejected our credentials, say so plainly and point at the usual cause: a
        // pre-existing data volume from an earlier install. Postgres ignores POSTGRES_PASSWORD once its data directory
        // exists, so a freshly-generated deploy/.env won't match the stored password. This turns a baffling 36-second
        // timeout into a one-line diagnosis.
        var message = IsAuthenticationFailure(lastError)
            ? "Database authentication failed — the server rejected the configured credentials. This almost always means "
              + "the Postgres data VOLUME was initialised by an EARLIER install with a different password (Postgres ignores "
              + "POSTGRES_PASSWORD once its data directory exists, so freshly-generated secrets in deploy/.env won't match). "
              + "Reset the volume and re-run — e.g. `docker compose -f deploy/docker-compose.yml down -v` then "
              + "`./deploy/setup.sh` (this DELETES the local database). Underlying error: " + (lastError?.Message ?? "unknown")
            : $"Database was not reachable after {maxAttempts} attempts. Last error: {lastError?.Message ?? "unknown"}.";

        throw new InvalidOperationException(message, lastError);
    }

    /// <summary>A Postgres authentication/authorisation failure (SqlState 28P01 invalid_password / 28000
    /// invalid_authorization_specification) anywhere in the exception chain — as distinct from a transient
    /// connection-refused while the server is still coming up.</summary>
    private static bool IsAuthenticationFailure(Exception? ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e is Npgsql.PostgresException { SqlState: "28P01" or "28000" })
            {
                return true;
            }
        }

        return false;
    }

    public async Task SeedAsync(CancellationToken cancellationToken = default)
    {
        // The initializer depends only on the IDataSeeder seam: each registered seeder runs in
        // ascending Order, is idempotent, and gates itself (see IDataSeeder). Editions add/remove
        // seeders purely by DI registration — the Community Edition strip drops a SaaS seeder's
        // file + registration line without touching this class (docs/17 §5).
        foreach (var seeder in seeders.OrderBy(s => s.Order))
        {
            await seeder.SeedAsync(cancellationToken);
        }
    }
}
