using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;

namespace WireHQ.Infrastructure.Auditing;

/// <summary>
/// Monthly partition management for <c>audit.audit_logs</c> (ADR-031, docs/15 §5). Range-partitioning by
/// <c>occurred_at_utc</c> lets reads prune to the relevant months and turns physical retention into a partition
/// drop. New rows need their month's partition to already exist, so this is called on boot (and, later, by the
/// retention sweeper) on the owner connection to carve the current month plus a buffer of upcoming months.
/// </summary>
public static class AuditPartitions
{
    /// <summary>The partition table name for a given UTC month, e.g. <c>audit_logs_2026_06</c>.</summary>
    public static string PartitionName(DateTime monthUtc) =>
        "audit_logs_" + monthUtc.ToString("yyyy_MM", CultureInfo.InvariantCulture);

    /// <summary>
    /// Whether <c>audit.audit_logs</c> is actually a partitioned table. The partition DDL lives in a raw-SQL
    /// migration that is invisible to the EF model, so a baseline created WITHOUT it — the <c>EnsureCreated</c>
    /// dev fallback, or the Community Edition's squashed <c>InitialCreate</c> (docs/17 §7) — yields a plain
    /// table. Callers must check this and skip partition management on plain tables (hash-chaining and the
    /// append-only grants are unaffected; only partition-pruning/partition-drop retention doesn't apply).
    /// The scalar is aliased "Value" for EF's <c>SqlQueryRaw&lt;bool&gt;</c>.
    /// </summary>
    public const string IsPartitionedSql =
        """
        SELECT EXISTS (
            SELECT 1
            FROM pg_partitioned_table pt
            JOIN pg_class c ON c.oid = pt.partrelid
            JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = 'audit' AND c.relname = 'audit_logs') AS "Value"
        """;

    /// <summary>Runs <see cref="IsPartitionedSql"/> via the EF (owner) context.</summary>
    public static async Task<bool> IsPartitionedAsync(DbContext owner, CancellationToken cancellationToken) =>
        await owner.Database.SqlQueryRaw<bool>(IsPartitionedSql).SingleAsync(cancellationToken);

    /// <summary>
    /// Ensures a partition exists for the current UTC month and each of the next <paramref name="monthsAhead"/>
    /// months, via the EF context. Idempotent (<c>CREATE TABLE IF NOT EXISTS</c>), safe on every boot. Must run
    /// as the owner. Partitions inherit the append-only default privileges granted to the app role by rls.sql.
    /// </summary>
    public static Task EnsureMonthlyPartitionsAsync(
        DbContext owner, DateTime utcNow, int monthsAhead, CancellationToken cancellationToken) =>
        owner.Database.ExecuteSqlRawAsync(BuildEnsurePartitionsSql(utcNow, monthsAhead), cancellationToken);

    /// <summary>
    /// The idempotent <c>CREATE TABLE IF NOT EXISTS … PARTITION OF</c> statements for the current UTC month and
    /// the next <paramref name="monthsAhead"/> months. Pure text so it can run via either an EF context (boot)
    /// or a raw owner connection (the retention sweeper). The partition name and range bounds derive only from
    /// the clock — never user input — and DDL can't take bound parameters for identifiers or bounds, so building
    /// the statement text is the only option; the bounds carry an explicit +00 offset so they're timezone-safe.
    /// </summary>
    public static string BuildEnsurePartitionsSql(DateTime utcNow, int monthsAhead)
    {
        var month = new DateTime(utcNow.Year, utcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc);

        var sql = new StringBuilder();
        for (var i = 0; i <= monthsAhead; i++)
        {
            var from = month.AddMonths(i);
            var to = from.AddMonths(1);
            sql.Append("CREATE TABLE IF NOT EXISTS audit.\"").Append(PartitionName(from))
               .Append("\" PARTITION OF audit.audit_logs FOR VALUES FROM ('")
               .Append(from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).Append(" 00:00:00+00') TO ('")
               .Append(to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).Append(" 00:00:00+00');\n");
        }

        return sql.ToString();
    }
}
