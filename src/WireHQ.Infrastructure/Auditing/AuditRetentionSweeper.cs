using System.Data.Common;
using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;
using WireHQ.Application.Abstractions;

namespace WireHQ.Infrastructure.Auditing;

/// <summary>
/// Physical retention for <c>audit.audit_logs</c> (ADR-031, docs/15 §5). Each pass, on the owner connection
/// (the app role is append-only and can't drop partitions):
///   1. keeps a buffer of upcoming month partitions carved out so inserts never miss a partition;
///   2. drops every monthly partition that lies entirely older than the ceiling
///      (<c>Audit:RetentionCeilingMonths</c>, default 13) — instant, no bulk-delete bloat;
///   3. in the same transaction, records a re-anchor per tenant whose oldest surviving row now dangles, so
///      the hash chain still verifies across the retention boundary instead of looking tampered.
/// Idempotent: with nothing old enough it drops nothing and inserts no anchors.
/// </summary>
public sealed class AuditRetentionSweeper(
    IConfiguration configuration,
    ILogger<AuditRetentionSweeper> logger)
    : IAuditRetentionService
{
    private const int MonthsAhead = 3;

    // Partitions of audit_logs whose name encodes a month, e.g. audit_logs_2026_06 (excludes the DEFAULT).
    private const string MonthlyPartitionsSql =
        """
        SELECT c.relname
        FROM pg_inherits i
        JOIN pg_class c ON c.oid = i.inhrelid
        JOIN pg_class p ON p.oid = i.inhparent
        JOIN pg_namespace n ON n.oid = p.relnamespace
        WHERE n.nspname = 'audit' AND p.relname = 'audit_logs'
          AND c.relname ~ '^audit_logs_[0-9]{4}_[0-9]{2}$'
        """;

    // For each tenant whose oldest surviving row carries a non-null prev_hash (its predecessor was dropped),
    // record that boundary hash as a legitimate cut — unless an anchor for it already exists. Idempotent.
    private const string RecordAnchorsSql =
        """
        INSERT INTO audit.audit_chain_anchors (id, organization_id, boundary_prev_hash, created_at_utc)
        SELECT gen_random_uuid(), oldest.organization_id, oldest.prev_hash, now()
        FROM (
            SELECT DISTINCT ON (organization_id) organization_id, prev_hash
            FROM audit.audit_logs
            ORDER BY organization_id, occurred_at_utc, id
        ) oldest
        WHERE oldest.prev_hash IS NOT NULL
          AND NOT EXISTS (
              SELECT 1 FROM audit.audit_chain_anchors a
              WHERE a.organization_id IS NOT DISTINCT FROM oldest.organization_id
                AND a.boundary_prev_hash = oldest.prev_hash
          )
        """;

    public async Task<int> SweepAsync(CancellationToken cancellationToken)
    {
        var ownerConnectionString =
            configuration.GetConnectionString("Admin") ?? configuration.GetConnectionString("Default");
        if (string.IsNullOrWhiteSpace(ownerConnectionString))
        {
            logger.LogWarning("Audit retention sweep skipped: no Admin/Default connection string configured.");
            return 0;
        }

        var ceilingMonths = ResolveCeilingMonths();
        var now = DateTime.UtcNow;
        // A monthly partition [M, M+1) is entirely older than the window when M < the first kept month.
        var cutoff = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(-ceilingMonths);

        await using var connection = new NpgsqlConnection(ownerConnectionString);
        await connection.OpenAsync(cancellationToken);

        // Partition-based retention only applies to a partitioned audit_logs. A plain table (the
        // EnsureCreated dev fallback, or the Community Edition's squashed baseline — docs/17 §7) has
        // nothing to carve or drop; skip the pass entirely.
        if (!await IsPartitionedAsync(connection, cancellationToken))
        {
            logger.LogDebug("Audit retention sweep skipped: audit.audit_logs is not partitioned.");
            return 0;
        }

        // 1. Keep upcoming partitions carved (idempotent), independent of the retention transaction.
        await ExecuteAsync(connection, null, AuditPartitions.BuildEnsurePartitionsSql(now, MonthsAhead), cancellationToken);

        // 2. Determine which partitions are entirely older than the cutoff.
        var toDrop = await ListPartitionsToDropAsync(connection, cutoff, cancellationToken);

        // 3. Drop them and re-anchor atomically: a drop with no matching anchor would otherwise read as tampering.
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        foreach (var partition in toDrop)
        {
            // partition is a catalog name validated by the MonthlyPartitionsSql regex, not user input.
            await ExecuteAsync(connection, transaction, $"DROP TABLE audit.\"{partition}\";", cancellationToken);
        }

        await ExecuteAsync(connection, transaction, RecordAnchorsSql, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        if (toDrop.Count > 0)
        {
            logger.LogInformation(
                "Audit retention dropped {Count} partition(s) older than {Months} months (cutoff {Cutoff:yyyy-MM}).",
                toDrop.Count, ceilingMonths, cutoff);
        }

        return toDrop.Count;
    }

    private static async Task<List<string>> ListPartitionsToDropAsync(
        DbConnection connection, DateTime cutoff, CancellationToken cancellationToken)
    {
        var names = new List<string>();
        await using (var command = connection.CreateCommand())
        {
            // A compile-time constant query with no parameters or interpolation.
            // nosemgrep: csharp.lang.security.sqli.csharp-sqli.csharp-sqli
            command.CommandText = MonthlyPartitionsSql;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                names.Add(reader.GetString(0));
            }
        }

        // Name is audit_logs_YYYY_MM; the trailing 7 chars are the partition's month.
        return names
            .Where(n => DateTime.ParseExact(n[^7..], "yyyy_MM", CultureInfo.InvariantCulture,
                            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal) < cutoff)
            .ToList();
    }

    private static async Task<bool> IsPartitionedAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        // A class-constant catalog query — never user input.
        // nosemgrep: csharp.lang.security.sqli.csharp-sqli.csharp-sqli
        command.CommandText = AuditPartitions.IsPartitionedSql;
        return await command.ExecuteScalarAsync(cancellationToken) is true;
    }

    private static async Task ExecuteAsync(
        DbConnection connection, DbTransaction? transaction, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        // sql is either a class constant or a DROP built from a regex-validated catalog partition name —
        // never user input; DDL identifiers can't be bound parameters anyway.
        // nosemgrep: csharp.lang.security.sqli.csharp-sqli.csharp-sqli
        command.CommandText = sql;
        if (transaction is not null)
        {
            command.Transaction = transaction;
        }

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private int ResolveCeilingMonths()
    {
        // Indexer + int.TryParse (not the Binder's GetValue<T> extension — G-06).
        var raw = configuration["Audit:RetentionCeilingMonths"];
        var months = int.TryParse(raw, out var value) ? value : 13;
        return Math.Clamp(months, 1, 600);
    }
}
