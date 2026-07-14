using WireHQ.Application.Authorization;
using WireHQ.Application.Common.Messaging;
using WireHQ.Shared.Results;

namespace WireHQ.Application.Updates;

// The install's update situation, for the operator's in-app banner/modal (docs/30 U-7). Gated on
// org.settings.update: on a self-hosted CE the org Owner/Admin IS the operator (there is no platform Super Admin
// tier), so regular members don't see an "update the server" prompt they cannot act on. Kept-core — SaaS binds
// the no-op provider, so the query simply reports "up to date" there.

public sealed record GetUpdateStatusQuery(string CurrentVersion) : IQuery<UpdateStatus>, IAuthorizedRequest
{
    public IReadOnlyCollection<string> RequiredPermissions => [Permissions.Organization.Update];
}

public sealed class GetUpdateStatusQueryHandler(IUpdateStatusProvider provider)
    : IQueryHandler<GetUpdateStatusQuery, UpdateStatus>
{
    public Task<Result<UpdateStatus>> Handle(GetUpdateStatusQuery query, CancellationToken cancellationToken) =>
        Task.FromResult(Result.Success(provider.Current(query.CurrentVersion)));
}
