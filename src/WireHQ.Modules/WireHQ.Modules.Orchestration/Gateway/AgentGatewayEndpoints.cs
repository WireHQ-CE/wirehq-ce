using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using WireHQ.Modules.WireGuard.Endpoints;

namespace WireHQ.Modules.Orchestration.Gateway;

/// <summary>
/// The agent data-plane surface (<c>/agent/v1/*</c>) served on the dedicated mTLS listener. <c>enroll</c> is
/// the one pre-cert route (token-authenticated, anonymous); <c>cert/rotate</c> + <c>heartbeat</c> require a
/// valid agent client certificate (the <c>AgentCertificate</c> scheme). A port guard 404s these routes unless
/// the request arrived on the agent listener, so they are never reachable on the JWT (:8080) listener. (ADR-028)
/// </summary>
public static class AgentGatewayEndpoints
{
    public static IEndpointRouteBuilder MapAgentGatewayEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints
            .MapGroup("/agent/v1")
            .WithTags("Agent Gateway")
            .AddEndpointFilter(EnsureAgentListenerAsync);

        // Pre-cert: a token + CSR in, a signed client cert out. Anonymous (the token is the credential).
        group.MapPost("/enroll", async (EnrollAgentRequest request, AgentGatewayService gateway, CancellationToken ct) =>
                (await gateway.EnrollAsync(request, ct)).ToHttpResult())
            .AllowAnonymous();

        // Cert-authenticated: re-key before expiry, and report liveness.
        group.MapPost("/cert/rotate", async (RotateCertRequest request, HttpContext context, AgentGatewayService gateway, CancellationToken ct) =>
            {
                if (AgentIdentity(context) is not { } identity)
                {
                    return Results.Unauthorized();
                }

                return (await gateway.RotateAsync(identity.AgentId, identity.OrganizationId, request, ct)).ToHttpResult();
            })
            .RequireAuthorization(RequireAgentCertificate);

        group.MapPost("/heartbeat", async (HeartbeatRequest request, HttpContext context, AgentGatewayService gateway, CancellationToken ct) =>
            {
                if (AgentIdentity(context) is not { } identity)
                {
                    return Results.Unauthorized();
                }

                return (await gateway.HeartbeatAsync(identity.AgentId, request, ct)).ToHttpResult();
            })
            .RequireAuthorization(RequireAgentCertificate);

        // Job delivery: the agent drains signed bundles for its bound instances, then reports each result.
        group.MapGet("/jobs", async (HttpContext context, AgentGatewayService gateway, CancellationToken ct) =>
            {
                if (AgentIdentity(context) is not { } identity)
                {
                    return Results.Unauthorized();
                }

                return (await gateway.GetJobsAsync(identity.AgentId, ct)).ToHttpResult();
            })
            .RequireAuthorization(RequireAgentCertificate);

        group.MapPost("/jobs/{id:guid}/result", async (Guid id, AgentJobResultRequest request, HttpContext context, AgentGatewayService gateway, CancellationToken ct) =>
            {
                if (AgentIdentity(context) is not { } identity)
                {
                    return Results.Unauthorized();
                }

                return (await gateway.PostJobResultAsync(identity.AgentId, id, request, ct)).ToHttpResult();
            })
            .RequireAuthorization(RequireAgentCertificate);

        // The generic non-WireGuard task channel (ADR-040): the agent drains opaque tasks (LDAP directory sync
        // is the first kind) and posts each result back. Idle in the CE (no task providers registered).
        group.MapGet("/tasks", async (HttpContext context, AgentGatewayService gateway, CancellationToken ct) =>
            {
                if (AgentIdentity(context) is not { } identity)
                {
                    return Results.Unauthorized();
                }

                return (await gateway.GetTasksAsync(identity.AgentId, identity.OrganizationId, ct)).ToHttpResult();
            })
            .RequireAuthorization(RequireAgentCertificate);

        group.MapPost("/tasks/{id:guid}/result", async (Guid id, AgentTaskResultRequest request, HttpContext context, AgentGatewayService gateway, CancellationToken ct) =>
            {
                if (AgentIdentity(context) is not { } identity)
                {
                    return Results.Unauthorized();
                }

                return (await gateway.PostTaskResultAsync(identity.AgentId, identity.OrganizationId, id, request.Result.GetRawText(), ct)).ToHttpResult();
            })
            .RequireAuthorization(RequireAgentCertificate);

        group.MapPost("/telemetry", async (AgentTelemetryRequest request, HttpContext context, AgentGatewayService gateway, CancellationToken ct) =>
            {
                if (AgentIdentity(context) is not { } identity)
                {
                    return Results.Unauthorized();
                }

                return (await gateway.PostTelemetryAsync(identity.AgentId, request, ct)).ToHttpResult();
            })
            .RequireAuthorization(RequireAgentCertificate);

        // The agent's observed runtime status (incl. agent-computed config drift) for its managed instances.
        group.MapPost("/status", async (AgentStatusRequest request, HttpContext context, AgentGatewayService gateway, CancellationToken ct) =>
            {
                if (AgentIdentity(context) is not { } identity)
                {
                    return Results.Unauthorized();
                }

                return (await gateway.PostStatusAsync(identity.AgentId, request, ct)).ToHttpResult();
            })
            .RequireAuthorization(RequireAgentCertificate);

        // The agent's structured step telemetry (spans + logs) for the telemetry plane (docs/15 §9). Re-emitted
        // by the gateway as tenant/agent-tagged OTel signals, parented to the deploy trace. (Phase 5)
        group.MapPost("/diagnostics", async (AgentDiagnosticsRequest request, HttpContext context, AgentGatewayService gateway, CancellationToken ct) =>
            {
                if (AgentIdentity(context) is not { } identity)
                {
                    return Results.Unauthorized();
                }

                return (await gateway.PostDiagnosticsAsync(identity.AgentId, identity.OrganizationId, request, ct)).ToHttpResult();
            })
            .RequireAuthorization(RequireAgentCertificate);

        return endpoints;
    }

    private static void RequireAgentCertificate(Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder policy) =>
        policy.AddAuthenticationSchemes(AgentCertificateAuthenticationHandler.SchemeName).RequireAuthenticatedUser();

    /// <summary>404s the agent routes unless the request arrived on the configured agent listener port.</summary>
    private static async ValueTask<object?> EnsureAgentListenerAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var options = context.HttpContext.RequestServices.GetRequiredService<IOptions<AgentGatewayOptions>>().Value;
        if (!options.Enabled || context.HttpContext.Connection.LocalPort != options.Port)
        {
            return Results.NotFound();
        }

        return await next(context);
    }

    private static (Guid AgentId, Guid OrganizationId)? AgentIdentity(HttpContext context)
    {
        var agentClaim = context.User.FindFirst(AgentGatewayClaims.AgentId)?.Value;
        var orgClaim = context.User.FindFirst(AgentGatewayClaims.OrganizationId)?.Value;
        return Guid.TryParse(agentClaim, out var agentId) && Guid.TryParse(orgClaim, out var organizationId)
            ? (agentId, organizationId)
            : null;
    }
}
