using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using WireHQ.Application.Abstractions;
using WireHQ.Domain.Common;

namespace WireHQ.Infrastructure.Persistence.Interceptors;

/// <summary>
/// Centralizes three cross-cutting persistence concerns so they can never be forgotten:
/// (1) stamps created/updated audit columns, (2) turns hard deletes of soft-deletable entities
/// into soft deletes, and (3) stamps the tenant id on inserts as a safety net. Values are set via
/// EF metadata, so the entities keep private setters. (docs/05-database.md)
/// </summary>
public sealed class AuditableEntityInterceptor(
    ICurrentUser currentUser,
    ITenantContext tenant,
    IDateTimeProvider clock)
    : SaveChangesInterceptor
{
    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Apply(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Apply(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private void Apply(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        var now = clock.UtcNow;
        var userId = currentUser.UserId;

        foreach (var entry in context.ChangeTracker.Entries())
        {
            // Convert hard delete → soft delete.
            if (entry is { State: EntityState.Deleted, Entity: ISoftDeletable })
            {
                entry.State = EntityState.Modified;
                entry.Property(nameof(ISoftDeletable.IsDeleted)).CurrentValue = true;
                entry.Property(nameof(ISoftDeletable.DeletedAtUtc)).CurrentValue = now;
                entry.Property(nameof(ISoftDeletable.DeletedBy)).CurrentValue = userId;

                // Remove() cascades Deleted to owned sub-entities (e.g. the instance's Slug, stored in
                // the same row). The owner row survives as an UPDATE, so reverting the owner to Modified
                // would leave EF nulling those owned columns → NOT NULL violation. Keep them Unchanged.
                foreach (var reference in entry.References)
                {
                    if (reference.TargetEntry is { State: EntityState.Deleted } owned && owned.Metadata.IsOwned())
                    {
                        owned.State = EntityState.Unchanged;
                    }
                }
            }

            if (entry.Entity is IAuditable)
            {
                switch (entry.State)
                {
                    case EntityState.Added:
                        entry.Property(nameof(IAuditable.CreatedAtUtc)).CurrentValue = now;
                        entry.Property(nameof(IAuditable.CreatedBy)).CurrentValue = userId;
                        break;
                    case EntityState.Modified:
                        entry.Property(nameof(IAuditable.UpdatedAtUtc)).CurrentValue = now;
                        entry.Property(nameof(IAuditable.UpdatedBy)).CurrentValue = userId;
                        break;
                }
            }

            // Tenant-stamp safety net: never leave a tenant-owned insert without an org id.
            if (entry is { State: EntityState.Added, Entity: ITenantOwned })
            {
                var prop = entry.Property(nameof(ITenantOwned.OrganizationId));
                if (prop.CurrentValue is Guid existing && existing == Guid.Empty && tenant.OrganizationId is { } orgId)
                {
                    prop.CurrentValue = orgId;
                }
            }
        }
    }
}
