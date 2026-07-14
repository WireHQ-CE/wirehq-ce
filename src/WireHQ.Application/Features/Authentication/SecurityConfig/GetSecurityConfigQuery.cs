using Microsoft.EntityFrameworkCore;
using WireHQ.Application.Abstractions.Persistence;
using WireHQ.Application.Common.Messaging;
using WireHQ.Shared.Results;

namespace WireHQ.Application.Features.Authentication.SecurityConfig;

/// <summary>
/// The public, unauthenticated security config the auth pages need to decide whether to render the
/// Turnstile widget. Exposes only the (public) site key + an effective on/off flag — never the secret.
/// No authorization marker ⇒ it flows through the pipeline anonymously. (docs/04-security.md)
/// </summary>
public sealed record GetSecurityConfigQuery : IQuery<SecurityConfigResponse>;

public sealed record SecurityConfigResponse(
    bool TurnstileEnabled,
    string? TurnstileSiteKey,
    bool RegistrationEnabled,
    bool SetupRequired);

public sealed class GetSecurityConfigQueryHandler(
    IApplicationDbContext dbContext,
    RegistrationOptions registration,
    SetupOptions setup)
    : IQueryHandler<GetSecurityConfigQuery, SecurityConfigResponse>
{
    public async Task<Result<SecurityConfigResponse>> Handle(GetSecurityConfigQuery query, CancellationToken cancellationToken)
    {
        var settings = await dbContext.PlatformSettings.AsNoTracking().FirstOrDefaultAsync(cancellationToken);

        // "Effective" enabled: only advertise it when it's both toggled on AND actually configured, so
        // the page never tries to render a widget with no site key.
        var enabled = settings is { TurnstileEnabled: true, TurnstileConfigured: true };

        // First-run setup: a self-hosted (Setup:Enabled) instance with no users yet sends its first
        // visitor to the in-browser setup wizard instead of sign-in (CompleteSetupCommand enforces
        // the same conditions server-side).
        var setupRequired = setup.Enabled
            && !await dbContext.Users.IgnoreQueryFilters().AnyAsync(cancellationToken);

        // Registration visibility rides along so the auth pages can hide signup on invite-only
        // (self-hosted) installs — the API enforces it regardless (RegisterCommandHandler).
        return new SecurityConfigResponse(
            enabled, enabled ? settings!.TurnstileSiteKey : null, registration.OpenRegistration, setupRequired);
    }
}
