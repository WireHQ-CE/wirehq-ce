using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using WireHQ.Api.Observability;
using WireHQ.Api.Security;
using WireHQ.Application.Abstractions.Persistence;
using WireHQ.Domain.ApiKeys;
using WireHQ.Domain.Identity;
using WireHQ.Identity.Jwt;
using WireHQ.Modules.Orchestration.Gateway;

namespace WireHQ.Api.Extensions;

public static class AuthenticationSetup
{
    /// <summary>The default request scheme: a policy scheme that forwards to the API-key handler when the request
    /// carries an API-key credential (an <c>X-Api-Key</c> header or a <c>Bearer whq_…</c>), else to JWT — so keys
    /// and sessions work on the same endpoints. (docs/26-api-keys-webhooks.md §5)</summary>
    public const string SmartAuthenticationScheme = "SmartJwtOrApiKey";

    /// <summary>
    /// Configures JWT bearer authentication + authorization. Lives in the host (which owns the
    /// request pipeline); validation parameters are derived from the bound <see cref="JwtOptions"/>.
    /// </summary>
    public static IServiceCollection AddWireHqAuthentication(this IServiceCollection services, IConfiguration configuration)
    {
        var jwt = configuration.GetSection(JwtOptions.SectionName).Get<JwtOptions>() ?? new JwtOptions();

        // Keep claim names as issued ("sub", "perm", …) rather than the legacy SOAP mappings.
        JwtSecurityTokenHandler.DefaultInboundClaimTypeMap.Clear();

        services
            .AddAuthentication(SmartAuthenticationScheme)
            // Pick the scheme per request: an API-key credential → the API-key handler; otherwise JWT. Forwards
            // challenge/forbid too, so an unauthenticated request still gets the JWT 401. (docs/26 §5)
            .AddPolicyScheme(SmartAuthenticationScheme, "JWT or API key", policy =>
            {
                policy.ForwardDefaultSelector = context =>
                {
                    if (ApiKeyToken.LooksLikeApiKey(context.Request.Headers["X-Api-Key"].ToString()))
                    {
                        return ApiKeyAuthenticationHandler.SchemeName;
                    }

                    var authorization = context.Request.Headers.Authorization.ToString();
                    return authorization.StartsWith("Bearer " + ApiKeyToken.Prefix, StringComparison.OrdinalIgnoreCase)
                        ? ApiKeyAuthenticationHandler.SchemeName
                        : JwtBearerDefaults.AuthenticationScheme;
                };
            })
            .AddJwtBearer(options =>
            {
                options.MapInboundClaims = false;
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = jwt.Issuer,
                    ValidateAudience = true,
                    ValidAudience = jwt.Audience,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.SigningKey)),
                    ClockSkew = TimeSpan.FromSeconds(jwt.ClockSkewSeconds),
                    NameClaimType = JwtRegisteredClaimNames.Sub,
                };

                // Make session revocation immediate: reject a still-valid JWT whose server-side
                // session has been revoked (logout, "log out everywhere", password reset). This is
                // what gives the 15-minute access token a real kill switch. (docs/04-security.md)
                options.Events = new JwtBearerEvents
                {
                    OnTokenValidated = async context =>
                    {
                        var sessionId = context.Principal?.FindFirstValue(WireHqClaims.SessionId);
                        if (!Guid.TryParse(sessionId, out var sid))
                        {
                            return;
                        }

                        var db = context.HttpContext.RequestServices.GetRequiredService<IApplicationDbContext>();
                        var active = await db.UserSessions
                            .IgnoreQueryFilters()
                            .AnyAsync(s => s.Id == sid && s.RevokedAtUtc == null);

                        if (!active)
                        {
                            context.Fail("session_revoked");
                        }
                    },
                };
            })
            // API keys: a scheme that authenticates from an X-Api-Key / Bearer whq_… credential (hash → key →
            // org + scopes). Routed to by the smart policy scheme above. (docs/26-api-keys-webhooks.md §5)
            .AddScheme<ApiKeyAuthenticationOptions, ApiKeyAuthenticationHandler>(
                ApiKeyAuthenticationHandler.SchemeName, _ => { })
            // The agent data plane: a second scheme that authenticates /agent/v1/* from the validated mTLS
            // client certificate (fingerprint → agent → org). The :8080 JWT listener never sees a client
            // cert, so this scheme simply NoResults there. (ADR-028)
            .AddScheme<AgentCertificateAuthenticationOptions, AgentCertificateAuthenticationHandler>(
                AgentCertificateAuthenticationHandler.SchemeName, _ => { });

        // The platform-operator policy gates the operator-only dependency-health endpoint (docs/15 §13): the
        // caller must hold a platform role (Super Admin or the read-mostly Support tier, ADR-032). Org-scoped
        // use cases keep using the MediatR IPlatformRequest/IAuthorizedRequest gates; this policy is for the
        // raw MapHealthChecks endpoint, which doesn't flow through the pipeline.
        services.AddAuthorizationBuilder()
            .AddPolicy(HealthEndpoints.PlatformOperatorPolicy, policy => policy
                .RequireAuthenticatedUser()
                .RequireClaim(WireHqClaims.PlatformRole, nameof(PlatformRole.SuperAdmin), nameof(PlatformRole.Support)));
        return services;
    }
}
