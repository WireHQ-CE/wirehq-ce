using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using WireHQ.Modules.Orchestration.Application.Agents;
using WireHQ.Modules.Orchestration.Application.Deployments;
using WireHQ.Modules.Orchestration.Application.Entitlements;
using WireHQ.Modules.Orchestration.Application.Fleet;
using WireHQ.Modules.Orchestration.Application.SshTargets;
using WireHQ.Modules.Orchestration.Application.Status;
using WireHQ.Modules.Orchestration.Application.Targets;
using WireHQ.Modules.WireGuard.Endpoints;

namespace WireHQ.Modules.Orchestration.Endpoints;

/// <summary>
/// The orchestration HTTP surface, mapped under <c>/api/v1/wireguard</c> alongside the WireGuard
/// module's routes. Thin: dispatch a command/query and map the Result to HTTP, reusing the WireGuard
/// module's Result→ProblemDetails mapping so the error contract is identical. (docs/12 §8)
/// </summary>
public static class OrchestrationEndpoints
{
    public static IEndpointRouteBuilder MapOrchestrationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints
            .MapGroup("/api/v1/wireguard")
            .RequireAuthorization()
            .WithTags("WireGuard Orchestration");

        // Enqueue a deployment of the instance's current desired state (202 Accepted — async).
        group.MapPost("/instances/{id:guid}/deploy", async (Guid id, ISender sender, CancellationToken ct) =>
            (await sender.Send(new RequestDeploymentCommand(id), ct)).ToHttpResult(StatusCodes.Status202Accepted));

        group.MapGet("/instances/{id:guid}/deployments", async (Guid id, ISender sender, CancellationToken ct) =>
            (await sender.Send(new ListDeploymentsQuery(id), ct)).ToHttpResult());

        group.MapGet("/deployments/{jobId:guid}", async (Guid jobId, ISender sender, CancellationToken ct) =>
            (await sender.Send(new GetDeploymentQuery(jobId), ct)).ToHttpResult());

        // Pull live status now (parses wg show over SSH) and persist the telemetry. POST: it writes.
        group.MapPost("/instances/{id:guid}/status", async (Guid id, ISender sender, CancellationToken ct) =>
            (await sender.Send(new RefreshInstanceStatusCommand(id), ct)).ToHttpResult());

        // Fleet overview: every instance with its target, status, drift, and peer connectivity + a summary.
        group.MapGet("/fleet", async (ISender sender, CancellationToken ct) =>
            (await sender.Send(new GetFleetQuery(), ct)).ToHttpResult());

        // ---- SSH deployment targets ----
        group.MapGet("/ssh-targets", async (ISender sender, CancellationToken ct) =>
            (await sender.Send(new ListSshTargetsQuery(), ct)).ToHttpResult());

        group.MapPost("/ssh-targets", async (CreateSshTargetRequest request, ISender sender, CancellationToken ct) =>
            (await sender.Send(new CreateSshTargetCommand(
                request.Name, request.Host, request.Port, request.Username, request.AuthKind, request.Credential, request.HostKeyFingerprint), ct))
                .ToHttpResult(StatusCodes.Status201Created));

        group.MapGet("/ssh-targets/{id:guid}", async (Guid id, ISender sender, CancellationToken ct) =>
            (await sender.Send(new GetSshTargetQuery(id), ct)).ToHttpResult());

        group.MapPatch("/ssh-targets/{id:guid}", async (Guid id, UpdateSshTargetRequest request, ISender sender, CancellationToken ct) =>
            (await sender.Send(new UpdateSshTargetCommand(
                id, request.Name, request.Host, request.Port, request.Username, request.HostKeyFingerprint, request.AuthKind, request.Credential), ct))
                .ToHttpResult());

        group.MapDelete("/ssh-targets/{id:guid}", async (Guid id, ISender sender, CancellationToken ct) =>
            (await sender.Send(new DeleteSshTargetCommand(id), ct)).ToHttpResult());

        // Probe a target: connect, report reachability + wg presence + the host-key fingerprint.
        group.MapPost("/ssh-targets/{id:guid}/test", async (Guid id, ISender sender, CancellationToken ct) =>
            (await sender.Send(new TestSshTargetCommand(id), ct)).ToHttpResult());

        // ---- Instance deployment binding ----
        group.MapGet("/instances/{id:guid}/target", async (Guid id, ISender sender, CancellationToken ct) =>
            (await sender.Send(new GetInstanceTargetQuery(id), ct)).ToHttpResult());

        group.MapPut("/instances/{id:guid}/target", async (Guid id, BindInstanceTargetRequest request, ISender sender, CancellationToken ct) =>
            (await sender.Send(new BindInstanceTargetCommand(id, request.Kind, request.SshTargetId, request.AgentId, request.KeyCustody, request.InterfaceName, request.AutoReconverge), ct)).ToHttpResult());

        // Current plan usage (counts) for the active org — pairs with the plan limits from /me.
        endpoints.MapGet("/api/v1/entitlements/usage", async (ISender sender, CancellationToken ct) =>
                (await sender.Send(new GetPlanUsageQuery(), ct)).ToHttpResult())
            .RequireAuthorization()
            .WithTags("Entitlements");

        MapAgentEndpoints(endpoints);
        return endpoints;
    }

    /// <summary>
    /// The operator surface for agent enrolment + lifecycle (JWT pipeline, gated on <c>orch.agents.*</c>).
    /// The agent's own data-plane surface (mTLS, <c>/agent/v1/*</c>) is a separate listener + auth scheme.
    /// </summary>
    private static void MapAgentEndpoints(IEndpointRouteBuilder endpoints)
    {
        var agents = endpoints
            .MapGroup("/api/v1/agents")
            .RequireAuthorization()
            .WithTags("WireGuard Agents");

        // Mint a single-use enrolment token (the CLEAR token is returned exactly once).
        agents.MapPost("/enroll-tokens", async (MintEnrollTokenRequest? request, ISender sender, CancellationToken ct) =>
            (await sender.Send(new MintEnrollTokenCommand(request?.TtlHours), ct)).ToHttpResult(StatusCodes.Status201Created));

        agents.MapGet("/", async (ISender sender, CancellationToken ct) =>
            (await sender.Send(new ListAgentsQuery(), ct)).ToHttpResult());

        agents.MapGet("/{id:guid}", async (Guid id, ISender sender, CancellationToken ct) =>
            (await sender.Send(new GetAgentQuery(id), ct)).ToHttpResult());

        agents.MapPost("/{id:guid}/disable", async (Guid id, ISender sender, CancellationToken ct) =>
            (await sender.Send(new DisableAgentCommand(id), ct)).ToHttpResult());

        agents.MapPost("/{id:guid}/reactivate", async (Guid id, ISender sender, CancellationToken ct) =>
            (await sender.Send(new ReactivateAgentCommand(id), ct)).ToHttpResult());

        agents.MapPost("/{id:guid}/revoke", async (Guid id, ISender sender, CancellationToken ct) =>
            (await sender.Send(new RevokeAgentCommand(id), ct)).ToHttpResult());
    }
}

public sealed record BindInstanceTargetRequest(string Kind, Guid? SshTargetId, Guid? AgentId, string? KeyCustody, string? InterfaceName, bool? AutoReconverge);

public sealed record MintEnrollTokenRequest(int? TtlHours);

public sealed record CreateSshTargetRequest(
    string Name, string Host, int? Port, string Username, string AuthKind, string Credential, string? HostKeyFingerprint);

public sealed record UpdateSshTargetRequest(
    string? Name, string? Host, int? Port, string? Username, string? HostKeyFingerprint, string? AuthKind, string? Credential);
