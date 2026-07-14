using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using WireHQ.Application.Abstractions.Security;
using WireHQ.Domain.Platform;

namespace WireHQ.Infrastructure.Persistence.Seeding;

/// <summary>
/// Ensures the single platform-settings row exists. On first creation it seeds the Cloudflare Turnstile
/// keys + on/off default (<c>Turnstile:*</c>) and the SMTP settings (<c>Smtp:*</c>) from configuration, so a
/// deployment can ship pre-configured. Secrets are encrypted at rest. Idempotent: once the row exists,
/// operator changes (made in Settings) are never overwritten — EXCEPT when <c>Smtp:SyncFromConfig=true</c>,
/// which re-applies the <c>Smtp:*</c> config on every boot. That is the self-hosted (Community Edition)
/// posture, where the <c>.env</c> file is the ONLY SMTP interface (there is no platform Settings UI);
/// the SaaS default (false) keeps operator edits authoritative. (docs/04-security.md, docs/17 §7)
/// </summary>
public sealed class PlatformSettingsSeeder(
    ApplicationDbContext dbContext,
    ISecretProtector secretProtector,
    IConfiguration configuration) : IDataSeeder
{
    public int Order => 30;

    public async Task SeedAsync(CancellationToken cancellationToken = default)
    {
        var existing = await dbContext.PlatformSettings.FirstOrDefaultAsync(cancellationToken);
        if (existing is not null)
        {
            if (string.Equals(configuration["Smtp:SyncFromConfig"], "true", StringComparison.OrdinalIgnoreCase)
                && ApplySmtpFromConfig(existing))
            {
                await dbContext.SaveChangesAsync(cancellationToken);
            }

            return;
        }

        var siteKey = configuration["Turnstile:SiteKey"];
        var secret = configuration["Turnstile:SecretKey"];
        // Ship on by default; the test factory and the demo seeder turn it off so they don't break.
        var enabledByDefault = configuration["Turnstile:EnabledByDefault"] is not { } flag
            || string.Equals(flag, "true", StringComparison.OrdinalIgnoreCase);

        var secretCiphertext = string.IsNullOrWhiteSpace(secret) ? null : secretProtector.Protect(secret);

        var settings = PlatformSettings.CreateDefault();
        settings.SetTurnstile(enabledByDefault, siteKey, secretCiphertext);

        // SMTP — seeded from config when present (e.g. the dev stack points at Mailpit); otherwise left
        // blank for the Super Admin to fill in under Settings → Email.
        ApplySmtpFromConfig(settings);

        // Analytics (Matomo) — seeded ONLY from config, defaulting to OFF with no endpoint. There is no
        // hardcoded tracker URL: a deployment that wants analytics supplies Analytics:* explicitly (the SaaS
        // sets them in its production compose), so the open-source build ships no phone-home and no vendor
        // host. Public values (no secret); the Super Admin can toggle/change them in Settings.
        var analyticsUrl = configuration["Analytics:MatomoUrl"];
        var analyticsEnabled = string.Equals(configuration["Analytics:Enabled"], "true", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(analyticsUrl);
        settings.SetAnalytics(
            enabled: analyticsEnabled,
            matomoUrl: string.IsNullOrWhiteSpace(analyticsUrl) ? null : analyticsUrl,
            matomoSiteId: configuration["Analytics:MatomoSiteId"]);

        dbContext.PlatformSettings.Add(settings);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Applies the <c>Smtp:*</c> configuration to <paramref name="settings"/>. A no-op (returns false)
    /// when <c>Smtp:Host</c> is unset, so a sync boot without SMTP config never clears an existing setup.
    /// </summary>
    private bool ApplySmtpFromConfig(PlatformSettings settings)
    {
        var smtpHost = configuration["Smtp:Host"];
        if (string.IsNullOrWhiteSpace(smtpHost))
        {
            return false;
        }

        var smtpPassword = configuration["Smtp:Password"];
        var smtpPasswordCiphertext = string.IsNullOrWhiteSpace(smtpPassword) ? null : secretProtector.Protect(smtpPassword);
        settings.SetSmtp(
            enabled: configuration["Smtp:Enabled"] is { } e && string.Equals(e, "true", StringComparison.OrdinalIgnoreCase),
            host: smtpHost,
            port: int.TryParse(configuration["Smtp:Port"], out var port) ? port : 587,
            username: configuration["Smtp:Username"],
            passwordCiphertext: smtpPasswordCiphertext,
            fromEmail: configuration["Smtp:FromEmail"],
            fromName: configuration["Smtp:FromName"],
            useSsl: string.Equals(configuration["Smtp:UseSsl"], "true", StringComparison.OrdinalIgnoreCase));
        return true;
    }
}
