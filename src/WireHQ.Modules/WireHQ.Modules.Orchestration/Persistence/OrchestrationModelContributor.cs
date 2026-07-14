using Microsoft.EntityFrameworkCore;
using WireHQ.Application.Abstractions.Persistence;

namespace WireHQ.Modules.Orchestration.Persistence;

/// <summary>
/// Applies the module's entity configurations (schema <c>orch</c>) into the shared
/// <c>ApplicationDbContext</c> model, so the deployment-job tables join the model while tenancy/audit
/// filters are applied generically by the context. (docs/12-remote-orchestration.md §7)
/// </summary>
public sealed class OrchestrationModelContributor : IModelConfigurationContributor
{
    public void Configure(ModelBuilder modelBuilder) =>
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(OrchestrationModelContributor).Assembly);
}
