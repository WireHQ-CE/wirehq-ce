using Microsoft.EntityFrameworkCore;
using WireHQ.Application.Abstractions.Persistence;
using WireHQ.Application.Common.Messaging;
using WireHQ.Domain.Memberships;
using WireHQ.Modules.Orchestration.Domain;
using WireHQ.Modules.WireGuard.Domain;
using WireHQ.Shared.Results;

namespace WireHQ.Modules.Orchestration.Application.Entitlements;

/// <summary>
/// Current usage of the plan-capped resources for the active org — drives the usage meters on the Plan page.
/// Authenticated + tenant-scoped (every counted table is <c>ITenantOwned</c>); no extra permission so any
/// member can see where their org stands against its plan. Pairs with the limits from <c>/me</c>.
/// (docs/commercial.md §6)
/// </summary>
public sealed record GetPlanUsageQuery : IQuery<PlanUsageDto>;

public sealed record PlanUsageDto(int Instances, int Peers, int Gateways, int Networks, int Seats);

public sealed class GetPlanUsageQueryHandler(IApplicationDbContext dbContext)
    : IQueryHandler<GetPlanUsageQuery, PlanUsageDto>
{
    public async Task<Result<PlanUsageDto>> Handle(GetPlanUsageQuery query, CancellationToken cancellationToken)
    {
        var instances = await dbContext.Set<WireGuardInstance>().CountAsync(cancellationToken);
        var peers = await dbContext.Set<Peer>().CountAsync(cancellationToken);
        var gateways = await dbContext.Set<DeploymentTarget>().CountAsync(t => t.Kind != DeploymentTargetKind.Local, cancellationToken);
        var networks = await dbContext.Set<WireGuardNetwork>().CountAsync(cancellationToken);
        var seats = await dbContext.Set<Membership>().CountAsync(m => !m.IsDeleted, cancellationToken);

        return new PlanUsageDto(instances, peers, gateways, networks, seats);
    }
}
