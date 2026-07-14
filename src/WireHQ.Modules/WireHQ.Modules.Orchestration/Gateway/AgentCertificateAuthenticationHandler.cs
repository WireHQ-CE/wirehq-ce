using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WireHQ.Application.Abstractions;
using WireHQ.Application.Abstractions.Persistence;
using WireHQ.Modules.Orchestration.Domain;

namespace WireHQ.Modules.Orchestration.Gateway;

public sealed class AgentCertificateAuthenticationOptions : AuthenticationSchemeOptions;

/// <summary>
/// Authenticates <c>/agent/v1/*</c> requests by the agent's mTLS client certificate. Trust is a
/// <b>leaf-fingerprint pin</b>: the SHA-256 of the presented cert must match an <see cref="AgentStatus.Active"/>
/// agent — that fingerprint is the exact cert WireHQ issued, so this is stronger than a chain check (and
/// disabled/revoked agents are rejected here, the no-CRL kill switch). The cross-tenant fingerprint lookup
/// runs in a <b>throwaway DI scope under RLS bypass</b>, so the request scope never enters bypass — even a
/// rejected cert leaves it fail-closed. On success the tenant is established for the rest of the request via
/// <see cref="ISettableTenantContext"/>, and the principal carries the agent + org ids. (ADR-028)
/// </summary>
public sealed class AgentCertificateAuthenticationHandler(
    IOptionsMonitor<AgentCertificateAuthenticationOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IServiceScopeFactory scopeFactory)
    : AuthenticationHandler<AgentCertificateAuthenticationOptions>(options, logger, encoder)
{
    public const string SchemeName = "AgentCertificate";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var clientCertificate = Context.Connection.ClientCertificate;
        if (clientCertificate is null)
        {
            // No client cert presented (e.g. a request on the JWT listener) — let other schemes / 401 decide.
            return AuthenticateResult.NoResult();
        }

        var nowUtc = DateTime.UtcNow;
        if (nowUtc < clientCertificate.NotBefore.ToUniversalTime() || nowUtc > clientCertificate.NotAfter.ToUniversalTime())
        {
            return AuthenticateResult.Fail("The agent certificate is outside its validity window.");
        }

        var fingerprint = clientCertificate.GetCertHashString(HashAlgorithmName.SHA256);

        // Resolve the fingerprint → agent cross-tenant, isolated under bypass so the request scope is never
        // tainted (fail-closed). The agents table is RLS-protected; bypass is the legitimate edge lookup.
        Agent? agent;
        using (var lookupScope = scopeFactory.CreateScope())
        {
            lookupScope.ServiceProvider.GetRequiredService<ISettableTenantContext>().SetBypass();
            var lookupDb = lookupScope.ServiceProvider.GetRequiredService<IApplicationDbContext>();
            agent = await lookupDb.Set<Agent>()
                .IgnoreQueryFilters()
                .AsNoTracking()
                .FirstOrDefaultAsync(a => a.CertificateFingerprint == fingerprint);
        }

        if (agent is null || !agent.IsActive)
        {
            return AuthenticateResult.Fail("Unknown, disabled, or revoked agent certificate.");
        }

        // Scope the rest of the request to the agent's org (RLS + audit). This runs during authorization,
        // i.e. AFTER TenantResolutionMiddleware, so nothing overwrites it.
        Context.RequestServices.GetRequiredService<ISettableTenantContext>().SetTenant(agent.OrganizationId);

        var identity = new ClaimsIdentity(
            [
                new Claim(AgentGatewayClaims.AgentId, agent.Id.ToString()),
                new Claim(AgentGatewayClaims.OrganizationId, agent.OrganizationId.ToString()),
            ],
            Scheme.Name);

        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name));
    }
}
