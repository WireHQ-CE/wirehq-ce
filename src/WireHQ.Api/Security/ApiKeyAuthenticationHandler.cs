using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using WireHQ.Application.Abstractions;
using WireHQ.Application.Abstractions.Persistence;
using WireHQ.Application.Entitlements;
using WireHQ.Domain.ApiKeys;
using WireHQ.Identity.Jwt;

namespace WireHQ.Api.Security;

public sealed class ApiKeyAuthenticationOptions : AuthenticationSchemeOptions;

/// <summary>
/// Authenticates a request from a WireHQ <b>API key</b> (docs/26-api-keys-webhooks.md §5, ADR-043) — a scoped,
/// revocable bearer secret presented in <c>X-Api-Key</c> or an <c>Authorization: Bearer whq_…</c> header. The key
/// is SHA-256-hashed and looked up cross-tenant in a <b>throwaway DI scope under RLS bypass</b> (the agent-cert
/// pattern), so the request scope is never tainted and a bad key stays fail-closed. On success the tenant is
/// established for the rest of the request and the principal carries <c>org</c> + one <c>perm</c> claim per
/// granted scope (so the whole authorization pipeline works unchanged) + <c>akid</c>. It deliberately has NO
/// <c>sub</c>/<c>mbr</c>/<c>sid</c> — a key is not a human session, so it can't act as its creator on endpoints
/// gated on the user identity (session management, MFA, profile); audit attributes a key's actions via <c>akid</c>
/// → the <c>api_key</c> actor type. <c>last_used</c> is touched throttled (at most once/minute) to avoid a write on
/// every call. A request that carries no API-key credential returns <see cref="AuthenticateResult.NoResult"/> so the
/// JWT scheme (or a 401) handles it.
/// </summary>
public sealed class ApiKeyAuthenticationHandler(
    IOptionsMonitor<ApiKeyAuthenticationOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IServiceScopeFactory scopeFactory,
    IDateTimeProvider clock)
    : AuthenticationHandler<ApiKeyAuthenticationOptions>(options, logger, encoder)
{
    public const string SchemeName = "ApiKey";

    private static readonly TimeSpan LastUsedThrottle = TimeSpan.FromMinutes(1);

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var presented = ExtractKey(Context.Request);
        if (presented is null)
        {
            return AuthenticateResult.NoResult();
        }

        var hash = ApiKeyToken.Hash(presented);

        ApiKey? key;
        var entitled = false;
        using (var lookupScope = scopeFactory.CreateScope())
        {
            lookupScope.ServiceProvider.GetRequiredService<ISettableTenantContext>().SetBypass();
            var db = lookupScope.ServiceProvider.GetRequiredService<IApplicationDbContext>();
            key = await db.ApiKeys
                .IgnoreQueryFilters()
                .Include(k => k.Scopes)
                .AsNoTracking()
                .FirstOrDefaultAsync(k => k.KeyHash == hash);

            if (key is not null)
            {
                // The plan is authoritative at the auth layer too: if the org has downgraded off the api.keys
                // entitlement, its keys stop authenticating — so a downgrade can't leave a live, un-manageable
                // credential (management is entitlement-gated). Resolved directly here since auth carries no
                // tenant/plan context yet (the anonymous-endpoint posture, as LDAP bind-at-login does).
                var edition = await db.Organizations
                    .IgnoreQueryFilters()
                    .Where(o => o.Id == key.OrganizationId)
                    .Select(o => o.Edition)
                    .FirstOrDefaultAsync();
                // Through the shared effective-feature helper (base plan ∪ activated modules), NOT the base plan
                // directly — so a CE org that activated the `api-extensions` module can actually use its keys, not
                // just mint them (docs/29 M-16). Resolved from the SAME RLS-bypass lookup scope so the CE reader's
                // install-global module query runs under bypass; a strict no-op in SaaS (NoActivatedModules).
                entitled = await lookupScope.ServiceProvider
                    .GetRequiredService<IEffectiveFeatureSet>()
                    .HasFeatureAsync(edition, PlanFeatures.ApiKeys, Context.RequestAborted);
            }
        }

        if (key is null || !key.IsUsable(clock.UtcNow) || !entitled)
        {
            return AuthenticateResult.Fail("Unknown, expired, revoked, or unlicensed API key.");
        }

        // Scope the rest of the request to the key's org (RLS + tenant + audit). Runs during authorization, i.e.
        // after TenantResolutionMiddleware, so nothing overwrites it.
        Context.RequestServices.GetRequiredService<ISettableTenantContext>().SetTenant(key.OrganizationId);

        // A key carries ONLY org + its scopes (+ akid). It deliberately has NO `sub`/`mbr`/`sid` — it is not a
        // human session — so ICurrentUser.UserId is null and a key can't act as a person on endpoints gated on
        // the user identity rather than a scope (session management, MFA, profile). Attribution flows through
        // `akid` → the api_key audit actor. (docs/26 §5; hardened after the wave-1 security review.)
        var claims = new List<Claim>
        {
            new(WireHqClaims.OrganizationId, key.OrganizationId.ToString()),
            new(WireHqClaims.ApiKeyId, key.Id.ToString()),
        };

        foreach (var scope in key.Scopes)
        {
            claims.Add(new Claim(WireHqClaims.Permission, scope.PermissionKey));
        }

        await TouchLastUsedAsync(key);

        var identity = new ClaimsIdentity(claims, Scheme.Name, nameType: null, roleType: WireHqClaims.Permission);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name));
    }

    private static string? ExtractKey(HttpRequest request)
    {
        var apiKeyHeader = request.Headers["X-Api-Key"].ToString();
        if (ApiKeyToken.LooksLikeApiKey(apiKeyHeader))
        {
            return apiKeyHeader;
        }

        var authorization = request.Headers.Authorization.ToString();
        const string bearer = "Bearer ";
        if (authorization.StartsWith(bearer, StringComparison.OrdinalIgnoreCase))
        {
            var token = authorization[bearer.Length..].Trim();
            if (ApiKeyToken.LooksLikeApiKey(token))
            {
                return token;
            }
        }

        return null;
    }

    /// <summary>Update last-used at most once/minute (a direct UPDATE, no load) so auth isn't a write on every call.</summary>
    private async Task TouchLastUsedAsync(ApiKey key)
    {
        var now = clock.UtcNow;
        if (key.LastUsedAtUtc is { } last && now - last < LastUsedThrottle)
        {
            return;
        }

        using var scope = scopeFactory.CreateScope();
        scope.ServiceProvider.GetRequiredService<ISettableTenantContext>().SetBypass();
        var db = scope.ServiceProvider.GetRequiredService<IApplicationDbContext>();
        await db.ApiKeys
            .IgnoreQueryFilters()
            .Where(k => k.Id == key.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(k => k.LastUsedAtUtc, now));
    }
}
