namespace WireHQ.Application.Abstractions;

/// <summary>
/// Captures the entity mutations pending in the current unit of work as a structured before/after diff, and
/// resolves the principal target, for the declarative <c>AuditBehavior</c>. Implemented over the EF
/// <c>ChangeTracker</c>, so it must be called after the handler has mutated entities and BEFORE
/// <c>SaveChanges</c> (while original values and the Added/Modified/Deleted states are still available).
/// (docs/15 §5, ADR-031)
/// </summary>
public interface IAuditChangeCapture
{
    /// <summary>
    /// Snapshots the pending changes. When exactly one (non-owned, non-audit) entity changed, its type and
    /// id become the audit target; otherwise the target is left for the caller to supply (the diff still
    /// lists every changed entity). Secret-named property values are redacted in the diff.
    /// </summary>
    AuditChangeSet Capture();
}

/// <summary>The resolved target (when unambiguous) plus the structured diff for a declaratively-audited command.</summary>
/// <param name="TargetType">The CLR type name of the single changed entity, or null when zero/many changed.</param>
/// <param name="TargetId">The primary key of that single changed entity, or null.</param>
/// <param name="Changes">A JSON-serialisable list of per-entity changes, or null when nothing was tracked.</param>
public sealed record AuditChangeSet(string? TargetType, string? TargetId, object? Changes);
