using Microsoft.EntityFrameworkCore;
using WireHQ.Application.Abstractions.Persistence;
using WireHQ.Application.Common.Messaging;
using WireHQ.Modules.WireGuard.Authorization;
using WireHQ.Modules.WireGuard.Domain;
using WireHQ.Shared.Results;

namespace WireHQ.Modules.WireGuard.Application.Config;

/// <summary>Lists a target's config-version history (metadata only — never the encrypted content).</summary>
public sealed record ListConfigVersionsQuery(ConfigTargetType TargetType, Guid TargetId)
    : IQuery<IReadOnlyList<ConfigVersionListItem>>, IAuthorizedRequest
{
    // Read permission tracks the target kind: peers vs instances.
    public IReadOnlyCollection<string> RequiredPermissions =>
        TargetType == ConfigTargetType.Peer ? [WireGuardPermissions.Peers.Read] : [WireGuardPermissions.Instances.Read];
}

public sealed record ConfigVersionListItem(
    int Version,
    string Format,
    string Checksum,
    DateTimeOffset CreatedAtUtc,
    Guid? CreatedBy,
    string? Note);

public sealed class ListConfigVersionsQueryHandler(IApplicationDbContext dbContext)
    : IQueryHandler<ListConfigVersionsQuery, IReadOnlyList<ConfigVersionListItem>>
{
    public async Task<Result<IReadOnlyList<ConfigVersionListItem>>> Handle(ListConfigVersionsQuery query, CancellationToken cancellationToken)
    {
        // Tenant scoping is automatic (ConfigVersion is ITenantOwned → global query filter).
        IReadOnlyList<ConfigVersionListItem> items = await dbContext.Set<ConfigVersion>()
            .Where(c => c.TargetType == query.TargetType && c.TargetId == query.TargetId)
            .OrderByDescending(c => c.Version)
            .Select(c => new ConfigVersionListItem(c.Version, c.Format, c.Checksum, c.CreatedAtUtc, c.CreatedBy, c.Note))
            .ToListAsync(cancellationToken);

        return Result.Success(items);
    }
}
