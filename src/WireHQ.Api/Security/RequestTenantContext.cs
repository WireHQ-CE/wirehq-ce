using WireHQ.Application.Abstractions;
using WireHQ.Shared.Observability;

namespace WireHQ.Api.Security;

/// <summary>
/// Mutable, request-scoped tenant context. Populated by <c>TenantResolutionMiddleware</c> from the
/// validated <c>org</c> claim, then consumed by the persistence layer (query filters + RLS GUC).
/// </summary>
public sealed class RequestTenantContext : ITenantContext, ISettableTenantContext
{
    public Guid? OrganizationId { get; private set; }

    public string? OrganizationSlug { get; private set; }

    public bool IsPlatformScope { get; private set; }

    public bool BypassTenantIsolation { get; private set; }

    public void SetTenant(Guid organizationId, string? slug = null)
    {
        OrganizationId = organizationId;
        OrganizationSlug = slug;
        // Scoping to a concrete org always re-enables RLS for the rest of this unit of work — e.g. the
        // dispatcher claims a job cross-tenant (bypass) then SetTenant(job.org) to run it org-scoped.
        BypassTenantIsolation = false;
    }

    public void SetPlatformScope() => IsPlatformScope = true;

    public void SetBypass() => BypassTenantIsolation = true;
}

/// <summary>Ambient request metadata for audit/observability.</summary>
public sealed class RequestContext(IHttpContextAccessor accessor) : IRequestContext
{
    public string? IpAddress => accessor.HttpContext?.Connection.RemoteIpAddress?.ToString();

    public string? UserAgent => accessor.HttpContext?.Request.Headers.UserAgent.ToString();

    // The W3C trace id so audit rows reconcile with traces, logs, and the X-Correlation-Id header
    // (ADR-030); falls back to the connection trace identifier outside a recording Activity.
    public string? RequestId => CorrelationId.Current() ?? accessor.HttpContext?.TraceIdentifier;
}
