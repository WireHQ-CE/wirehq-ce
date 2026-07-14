using FluentValidation;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using WireHQ.Application.Abstractions.Persistence;
using WireHQ.Application.Authorization;
using WireHQ.Modules.Abstractions;
using WireHQ.Modules.Orchestration.Authorization;
using WireHQ.Modules.Orchestration.Certificates;
using WireHQ.Modules.Orchestration.Endpoints;
using WireHQ.Modules.Orchestration.Gateway;
using WireHQ.Modules.Orchestration.Persistence;
using WireHQ.Modules.Orchestration.Providers.Agent;
using WireHQ.Modules.Orchestration.Providers.Ssh;
using WireHQ.Modules.Orchestration.Services;
using WireHQ.Modules.WireGuard.Providers;

namespace WireHQ.Modules.Orchestration;

/// <summary>
/// The Remote Orchestration module — the deployment-job engine that drives WireGuard config onto
/// local/remote targets through the WireGuard module's <c>IWireGuardProvider</c> seam. Discovered,
/// gated, and wired by the host via <see cref="IModule"/>. Registers its model (schema <c>orch</c>),
/// the background dispatcher, CQRS handlers, and HTTP endpoints. (docs/12-remote-orchestration.md)
/// </summary>
public sealed class OrchestrationModule : IModule
{
    public string Name => "orchestration";

    public string Version => "1.0.0";

    public bool IsEnabled(IConfiguration configuration)
    {
        // Enabled by default; toggle with Modules:Orchestration:Enabled = false.
        var value = configuration["Modules:Orchestration:Enabled"];
        return string.IsNullOrWhiteSpace(value) || (bool.TryParse(value, out var enabled) && enabled);
    }

    public void RegisterServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<IPermissionContributor, OrchestrationPermissionContributor>();
        services.AddSingleton<IModelConfigurationContributor, OrchestrationModelContributor>();

        // Renders an instance's desired server config (shared by deploy + drift detection).
        services.AddScoped<IServerConfigRenderer, ServerConfigRenderer>();

        // The deployment-job engine: a scoped dispatcher driven by a background polling loop.
        services.AddScoped<IJobDispatcher, JobDispatcher>();
        services.AddHostedService<JobDispatcherHostedService>();

        // Live status + telemetry: a shared per-instance sync (on-demand query + reconciler) and the
        // periodic reconciler that drains it across SSH-bound instances. (docs/12 §10)
        services.AddScoped<IInstanceStatusSync, InstanceStatusSync>();
        services.AddScoped<IStatusReconciler, StatusReconciler>();
        services.AddHostedService<StatusReconcilerHostedService>();

        // Opt-in drift remediation: re-enqueue a deploy when a target drifts. (docs/12 §13 Phase 3, gap #4)
        services.AddScoped<IAutoReconverger, AutoReconverger>();

        // The SSH (Push) remote data plane. Registered as an IWireGuardProvider so the WireGuard
        // module's factory resolves it by type with no change there.
        services.AddSingleton<ISshConnectionFactory, SshConnectionFactory>();
        services.AddSingleton<IWireGuardProvider, SshWireGuardProvider>();

        // The agent (Pull) data plane. Registered as an IWireGuardProvider so the factory resolves it by
        // type; it never acts inline — the gateway delivers signed bundles on the agent's poll. (ADR-028)
        services.AddSingleton<IWireGuardProvider, AgentWireGuardProvider>();

        // The agent trust plane (Phase 2): the per-org CA that issues short-lived client certs the mTLS
        // gateway authenticates + signs job bundles. One scoped instance, both seams. (ADR-028)
        services.AddScoped<CaService>();
        services.AddScoped<ICertificateAuthority>(sp => sp.GetRequiredService<CaService>());
        services.AddScoped<IBundleSigner>(sp => sp.GetRequiredService<CaService>());

        // The agent mTLS gateway: enrolment + cert rotation + heartbeat behind the AgentCertificate scheme.
        // Options bind here (the host reads them too, for the Kestrel listener); the listener + scheme are
        // wired in the Api host (the composition root). (ADR-028)
        services.Configure<AgentGatewayOptions>(configuration.GetSection(AgentGatewayOptions.SectionName));
        services.AddScoped<AgentGatewayService>();

        var assembly = typeof(OrchestrationModule).Assembly;
        services.AddMediatR(cfg => cfg.RegisterServicesFromAssembly(assembly));
        services.AddValidatorsFromAssembly(assembly, includeInternalTypes: true);
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapOrchestrationEndpoints();
        endpoints.MapAgentGatewayEndpoints();
    }
}
