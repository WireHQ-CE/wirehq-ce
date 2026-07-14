using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using WireHQ.Domain.Auditing;
using WireHQ.Infrastructure.Auditing;

namespace WireHQ.Infrastructure.Persistence.Interceptors;

/// <summary>
/// Links every new audit entry into its tenant's tamper-evident hash chain (ADR-031, docs/15 §5).
///
/// On save it groups the inserted <see cref="AuditLog"/> rows by tenant and, per tenant, takes a Postgres
/// advisory lock, reads the current chain head, and stamps each new row's <c>prev_hash</c>/<c>entry_hash</c>
/// in (occurred-at, id) order. The lock serialises concurrent writers to the same chain so two transactions
/// can never read the same head and fork it.
///
/// Why a <i>session</i> lock (acquired here, released in <see cref="SavedChangesAsync"/>/
/// <see cref="SaveChangesFailedAsync"/>) rather than <c>pg_advisory_xact_lock</c>: EF raises
/// <see cref="SavingChangesAsync"/> <b>before</b> it opens the save transaction, so a transaction-scoped lock
/// taken here would auto-commit and release immediately. Releasing the session lock only after the save has
/// committed (or failed) gives identical fork-prevention without wrapping the save in an explicit transaction —
/// which would otherwise move the post-commit domain-event dispatch ahead of commit.
/// </summary>
public sealed class AuditChainInterceptor : SaveChangesInterceptor
{
    // All SQL below is compile-time constant; the org id and lock key are always bound parameters
    // (@org/@key), never interpolated — no injection surface.
    private const string AcquireLockSql = "SELECT pg_advisory_lock(@key)";
    private const string ReleaseLockSql = "SELECT pg_advisory_unlock(@key)";
    private const string ReadHeadByOrgSql =
        "SELECT entry_hash FROM audit.audit_logs WHERE organization_id = @org ORDER BY occurred_at_utc DESC, id DESC LIMIT 1";
    // Branch on null (rather than IS NOT DISTINCT FROM) so the btree index on (organization_id, occurred_at) is usable.
    private const string ReadHeadPlatformSql =
        "SELECT entry_hash FROM audit.audit_logs WHERE organization_id IS NULL ORDER BY occurred_at_utc DESC, id DESC LIMIT 1";

    // Held advisory-lock keys for the in-flight save. The interceptor is scoped (one per DbContext), and a
    // context's saves are sequential, so per-instance state is safe; we clear it on release.
    private readonly List<long> _heldKeys = [];

    // Whether we opened the connection ourselves (EF hasn't opened it yet when SavingChanges fires). If so we
    // must close it after the save so EF's ref-count balances and the connection returns to the pool.
    private bool _openedConnection;

    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        if (eventData.Context is not null)
        {
            await ChainAsync(eventData.Context, cancellationToken);
        }

        return await base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    public override async ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        // Release only after the rows are committed, so a waiting writer reads a head that includes them.
        if (eventData.Context is not null)
        {
            await ReleaseAsync(eventData.Context, cancellationToken);
        }

        return await base.SavedChangesAsync(eventData, result, cancellationToken);
    }

    public override async Task SaveChangesFailedAsync(
        DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        if (eventData.Context is not null)
        {
            await ReleaseAsync(eventData.Context, cancellationToken);
        }

        await base.SaveChangesFailedAsync(eventData, cancellationToken);
    }

    private async Task ChainAsync(DbContext context, CancellationToken cancellationToken)
    {
        var added = context.ChangeTracker.Entries<AuditLog>()
            .Where(e => e.State == EntityState.Added)
            .Select(e => e.Entity)
            .ToList();

        if (added.Count == 0)
        {
            return;
        }

        // EF opens the connection lazily during command execution — i.e. after this interceptor. Open it now
        // (ref-counted, so EF reuses the same physical connection for the inserts) so the session advisory lock
        // is held on exactly the connection that writes the rows. Closed again post-commit in ReleaseAsync.
        if (context.Database.GetDbConnection().State != ConnectionState.Open)
        {
            await context.Database.OpenConnectionAsync(cancellationToken);
            _openedConnection = true;
        }

        var connection = context.Database.GetDbConnection();
        var transaction = context.Database.CurrentTransaction?.GetDbTransaction();

        // Lock the tenants' chains in a stable order so two concurrent multi-tenant saves can't deadlock.
        var groups = added
            .GroupBy(e => e.OrganizationId)
            .Select(g => (Key: AuditChain.LockKey(g.Key), OrganizationId: g.Key, Entries: g.ToList()))
            .OrderBy(g => g.Key)
            .ToList();

        try
        {
            foreach (var group in groups)
            {
                await AcquireLockAsync(connection, transaction, group.Key, cancellationToken);
                _heldKeys.Add(group.Key);

                var prev = await ReadChainHeadAsync(connection, transaction, group.OrganizationId, cancellationToken);

                foreach (var entry in group.Entries.OrderBy(e => e.OccurredAtUtc).ThenBy(e => e.Id))
                {
                    var entryHash = AuditChain.ComputeEntryHash(prev, entry);
                    entry.ApplyChain(prev, entryHash);
                    prev = entryHash;
                }
            }
        }
        catch
        {
            // Don't strand locks if chaining throws before EF's save runs (SaveChangesFailed won't fire then).
            await ReleaseAsync(context, cancellationToken);
            throw;
        }
    }

    private async Task ReleaseAsync(DbContext context, CancellationToken cancellationToken)
    {
        if (_heldKeys.Count > 0)
        {
            var connection = context.Database.GetDbConnection();
            var transaction = context.Database.CurrentTransaction?.GetDbTransaction();

            // Release each lock so a writer waiting on this tenant's chain can proceed (it now reads a head
            // that includes our just-committed rows). Releasing before closing also prompt-frees the lock.
            // Swallow release errors: this also runs on the failure path, where re-throwing would mask the
            // original exception — and the pool reset on close frees any lock left behind anyway.
            if (connection.State == ConnectionState.Open)
            {
                try
                {
                    foreach (var key in _heldKeys)
                    {
                        await using var command = CreateCommand(connection, transaction, ReleaseLockSql);
                        AddKey(command, key);
                        await command.ExecuteNonQueryAsync(cancellationToken);
                    }
                }
                catch
                {
                    // Best-effort: the connection's pool reset (DISCARD ALL) releases the locks regardless.
                }
            }

            _heldKeys.Clear();
        }

        // Balance the ref-counted open above; the connection returns to the pool (whose reset also frees any
        // advisory lock, a backstop if release was skipped because the connection had already dropped).
        if (_openedConnection)
        {
            _openedConnection = false;
            try
            {
                await context.Database.CloseConnectionAsync();
            }
            catch
            {
                // On the failure path a broken connection can throw on close — never let that mask the real error.
            }
        }
    }

    private static async Task AcquireLockAsync(
        DbConnection connection, DbTransaction? transaction, long key, CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, transaction, AcquireLockSql);
        AddKey(command, key);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<byte[]?> ReadChainHeadAsync(
        DbConnection connection, DbTransaction? transaction, Guid? organizationId, CancellationToken cancellationToken)
    {
        // The chain head is the most recent entry for the tenant, in the same (occurred-at, id) order the
        // chain is built and verified in.
        var sql = organizationId is null ? ReadHeadPlatformSql : ReadHeadByOrgSql;

        await using var command = CreateCommand(connection, transaction, sql);
        if (organizationId is { } org)
        {
            var p = command.CreateParameter();
            p.ParameterName = "org";
            p.Value = org;
            command.Parameters.Add(p);
        }

        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is byte[] hash ? hash : null;
    }

    private static DbCommand CreateCommand(DbConnection connection, DbTransaction? transaction, string sql)
    {
        var command = connection.CreateCommand();
        // sql is one of this class's compile-time-constant fields; all caller-supplied values (org id, lock
        // key) are bound parameters, never interpolated into the text. No injection surface.
        // nosemgrep: csharp.lang.security.sqli.csharp-sqli.csharp-sqli
        command.CommandText = sql;
        command.CommandType = CommandType.Text;
        if (transaction is not null)
        {
            command.Transaction = transaction;
        }

        return command;
    }

    private static void AddKey(DbCommand command, long key)
    {
        var p = command.CreateParameter();
        p.ParameterName = "key";
        p.Value = key;
        command.Parameters.Add(p);
    }
}
