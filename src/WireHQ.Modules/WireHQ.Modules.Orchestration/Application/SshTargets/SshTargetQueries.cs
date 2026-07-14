using Microsoft.EntityFrameworkCore;
using WireHQ.Application.Abstractions.Persistence;
using WireHQ.Application.Common.Messaging;
using WireHQ.Modules.Orchestration.Authorization;
using WireHQ.Modules.Orchestration.Domain;
using WireHQ.Shared.Results;

namespace WireHQ.Modules.Orchestration.Application.SshTargets;

/// <summary>An SSH target's non-secret details. The credential ciphertext is never projected.</summary>
public sealed record SshTargetItem(
    Guid Id,
    string Name,
    string Host,
    int Port,
    string Username,
    string AuthKind,
    string? HostKeyFingerprint,
    DateTimeOffset CreatedAtUtc);

public sealed record ListSshTargetsQuery : IQuery<IReadOnlyList<SshTargetItem>>, IAuthorizedRequest
{
    public IReadOnlyCollection<string> RequiredPermissions => [OrchestrationPermissions.Targets.Read];
}

public sealed class ListSshTargetsQueryHandler(IApplicationDbContext dbContext)
    : IQueryHandler<ListSshTargetsQuery, IReadOnlyList<SshTargetItem>>
{
    public async Task<Result<IReadOnlyList<SshTargetItem>>> Handle(ListSshTargetsQuery query, CancellationToken cancellationToken)
    {
        var rows = await dbContext.Set<SshTarget>()
            .OrderBy(t => t.Name)
            .Select(t => new { t.Id, t.Name, t.Host, t.Port, t.Username, t.AuthKind, t.HostKeyFingerprint, t.CreatedAtUtc })
            .ToListAsync(cancellationToken);

        IReadOnlyList<SshTargetItem> items = rows
            .Select(t => new SshTargetItem(t.Id, t.Name, t.Host, t.Port, t.Username, t.AuthKind.ToString(), t.HostKeyFingerprint, t.CreatedAtUtc))
            .ToList();

        return Result.Success(items);
    }
}

public sealed record GetSshTargetQuery(Guid Id) : IQuery<SshTargetItem>, IAuthorizedRequest
{
    public IReadOnlyCollection<string> RequiredPermissions => [OrchestrationPermissions.Targets.Read];
}

public sealed class GetSshTargetQueryHandler(IApplicationDbContext dbContext)
    : IQueryHandler<GetSshTargetQuery, SshTargetItem>
{
    public async Task<Result<SshTargetItem>> Handle(GetSshTargetQuery query, CancellationToken cancellationToken)
    {
        var t = await dbContext.Set<SshTarget>().FirstOrDefaultAsync(x => x.Id == query.Id, cancellationToken);
        return t is null
            ? OrchestrationErrors.SshTarget.NotFound
            : new SshTargetItem(t.Id, t.Name, t.Host, t.Port, t.Username, t.AuthKind.ToString(), t.HostKeyFingerprint, t.CreatedAtUtc);
    }
}
