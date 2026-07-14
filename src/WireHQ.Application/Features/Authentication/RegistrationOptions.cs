namespace WireHQ.Application.Features.Authentication;

/// <summary>
/// Whether self-serve signup (<c>POST /auth/register</c>) is open. On by default — the SaaS posture.
/// Self-hosted installs set <c>Auth:OpenRegistration=false</c> so the instance stays invite-only after
/// the first Owner is seeded (<c>SelfHostOwnerSeeder</c>; docs/17-community-edition.md). Bound once in
/// Infrastructure DI from configuration — the Application layer never reads <c>IConfiguration</c>
/// directly (same pattern as <see cref="Entitlements.EntitlementOptions"/>).
/// </summary>
public sealed class RegistrationOptions
{
    public bool OpenRegistration { get; init; } = true;
}
