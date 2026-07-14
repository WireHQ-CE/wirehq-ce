namespace WireHQ.Domain.Common;

/// <summary>
/// Implemented by every entity that belongs to a tenant. The persistence layer keys the
/// global query filter and the insert-time stamp off this interface, so isolation is applied
/// uniformly and developers never hand-write a tenant predicate. See docs/03-multi-tenancy.md.
/// </summary>
public interface ITenantOwned
{
    Guid OrganizationId { get; }
}

/// <summary>
/// Standard audit columns. Populated centrally by an EF Core SaveChanges interceptor, never
/// by hand, so they are always consistent and never forgotten.
/// </summary>
public interface IAuditable
{
    DateTimeOffset CreatedAtUtc { get; }

    Guid? CreatedBy { get; }

    DateTimeOffset? UpdatedAtUtc { get; }

    Guid? UpdatedBy { get; }
}

/// <summary>
/// Marks an entity as soft-deletable. A global query filter hides soft-deleted rows by
/// default; hard deletion is an explicit, audited purge operation only.
/// </summary>
public interface ISoftDeletable
{
    bool IsDeleted { get; }

    DateTimeOffset? DeletedAtUtc { get; }

    Guid? DeletedBy { get; }
}
