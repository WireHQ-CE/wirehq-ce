using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using WireHQ.Application.Abstractions;
using WireHQ.Domain.Auditing;
using WireHQ.Domain.Common;
using WireHQ.Infrastructure.Persistence;
using WireHQ.Shared.Observability;

namespace WireHQ.Infrastructure.Auditing;

/// <summary>
/// Builds the structured before/after diff for a declaratively-audited command from the EF
/// <c>ChangeTracker</c> (docs/15 §5, ADR-031). Walks the pending Added/Modified/Deleted entities, records
/// each as an operation + a per-property old/new diff (changed properties only for updates), and resolves an
/// unambiguous target when a single entity changed. Secret-named values are masked — the audit plane is
/// durable storage, so the same "never persist a secret" net the telemetry redaction applies (shared
/// <see cref="SensitiveData"/> denylist) applies here too.
/// </summary>
public sealed class EfAuditChangeCapture(ApplicationDbContext dbContext) : IAuditChangeCapture
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    // Framework-managed columns carry no business meaning in a diff and are stamped by the auditable-entity
    // interceptor AFTER this capture runs. The primary key + any concurrency token are excluded per entity.
    private static readonly HashSet<string> ExcludedProperties = new(StringComparer.Ordinal)
    {
        nameof(ITenantOwned.OrganizationId),
        nameof(IAuditable.CreatedAtUtc), nameof(IAuditable.CreatedBy),
        nameof(IAuditable.UpdatedAtUtc), nameof(IAuditable.UpdatedBy),
        nameof(ISoftDeletable.IsDeleted), nameof(ISoftDeletable.DeletedAtUtc), nameof(ISoftDeletable.DeletedBy),
    };

    public AuditChangeSet Capture()
    {
        var entries = dbContext.ChangeTracker.Entries()
            .Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            .Where(e => e.Entity is not AuditLog)   // never record the audit row describing itself
            .Where(e => !e.Metadata.IsOwned())      // owned values fold into their owner, not standalone targets
            .ToList();

        if (entries.Count == 0)
        {
            return new AuditChangeSet(null, null, null);
        }

        var changes = entries.Select(BuildEntityChange).ToList();

        // A single changed entity is an unambiguous target; with zero or many, the action key + the diff
        // describe what happened and the target is left null (the AuditBehavior records the action regardless).
        string? targetType = null;
        string? targetId = null;
        if (entries.Count == 1)
        {
            targetType = entries[0].Metadata.ClrType.Name;
            targetId = KeyOf(entries[0]);
        }

        return new AuditChangeSet(targetType, targetId, changes);
    }

    private static EntityChange BuildEntityChange(EntityEntry entry)
    {
        var keyNames = entry.Metadata.FindPrimaryKey()?.Properties.Select(p => p.Name).ToHashSet(StringComparer.Ordinal)
                       ?? new HashSet<string>(StringComparer.Ordinal);

        var diff = new Dictionary<string, ValueDiff>(StringComparer.Ordinal);
        foreach (var property in entry.Properties)
        {
            var name = property.Metadata.Name;
            if (keyNames.Contains(name) || ExcludedProperties.Contains(name) || property.Metadata.IsConcurrencyToken)
            {
                continue;
            }

            // Updates record only what changed; inserts/deletes record the whole business snapshot.
            if (entry.State == EntityState.Modified && !property.IsModified)
            {
                continue;
            }

            var redact = SensitiveData.IsSensitiveName(name);
            var (oldValue, newValue) = entry.State switch
            {
                EntityState.Added => (null, Project(property.CurrentValue, redact)),
                EntityState.Deleted => (Project(property.CurrentValue, redact), null),
                _ => (Project(property.OriginalValue, redact), Project(property.CurrentValue, redact)),
            };

            diff[name] = new ValueDiff(oldValue, newValue);
        }

        return new EntityChange(entry.Metadata.ClrType.Name, KeyOf(entry), entry.State.ToString(), diff);
    }

    private static string? KeyOf(EntityEntry entry)
    {
        var key = entry.Metadata.FindPrimaryKey();
        if (key is null)
        {
            return null;
        }

        var values = key.Properties.Select(p => entry.Property(p.Name).CurrentValue?.ToString());
        return string.Join(":", values);
    }

    private static object? Project(object? value, bool redact) =>
        value is null ? null : redact ? SensitiveData.Mask : SafeValue(value);

    private static object? SafeValue(object value) => value switch
    {
        string s => s,
        bool or int or long or short or byte or sbyte or uint or ulong or ushort or float or double or decimal => value,
        Guid g => g.ToString(),
        Enum e => e.ToString(),
        DateTime or DateTimeOffset or DateOnly or TimeOnly => value,
        // Value objects + collections (e.g. the DNS list): structured JSON when possible, else a readable string.
        _ => SafeSerialize(value),
    };

    private static object? SafeSerialize(object value)
    {
        try
        {
            return JsonSerializer.SerializeToElement(value, JsonOptions);
        }
        catch
        {
            return value.ToString();
        }
    }

    private sealed record EntityChange(string Entity, string? Id, string Operation, IReadOnlyDictionary<string, ValueDiff> Changes);

    private sealed record ValueDiff(object? Old, object? New);
}
