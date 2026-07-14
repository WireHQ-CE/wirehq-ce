using System.Security.Claims;
using WireHQ.Api.Security;
using WireHQ.Application.Abstractions;
using WireHQ.Identity.Jwt;

namespace WireHQ.Api.Middleware;

/// <summary>
/// Resolves the active tenant for the request from the validated <c>org</c> claim and stores it
/// in the request-scoped <see cref="ITenantContext"/>. Runs after authentication so the claim is
/// trustworthy. The persistence layer then scopes every query to this org. (docs/03-multi-tenancy.md)
/// </summary>
public sealed class TenantResolutionMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, ITenantContext tenantContext)
    {
        if (tenantContext is RequestTenantContext mutable && context.User.Identity?.IsAuthenticated == true)
        {
            var orgClaim = context.User.FindFirstValue(WireHqClaims.OrganizationId);
            if (Guid.TryParse(orgClaim, out var organizationId))
            {
                mutable.SetTenant(organizationId);
            }
            else if (context.User.FindFirstValue(WireHqClaims.PlatformRole) is not null)
            {
                // A platform operator with no active org runs in platform scope (cross-tenant reads
                // are explicit via IgnoreQueryFilters in platform handlers).
                mutable.SetPlatformScope();
            }
        }

        await next(context);
    }
}
