namespace WireHQ.Application.Features.Authentication;

/// <summary>
/// Whether the browser first-run setup (<c>POST /auth/setup</c>) is available. Off by default — the
/// SaaS posture (SaaS instances are bootstrapped by seeders and always have users). Self-hosted
/// installs set <c>Setup:Enabled=true</c> so a fresh, ownerless instance greets its first visitor
/// with the in-browser setup wizard instead of the sign-in page; the endpoint hard-disables itself
/// the moment any user exists (docs/17-community-edition.md). Bound once in Infrastructure DI from
/// configuration — same pattern as <see cref="RegistrationOptions"/>.
/// </summary>
public sealed class SetupOptions
{
    public bool Enabled { get; init; }
}
