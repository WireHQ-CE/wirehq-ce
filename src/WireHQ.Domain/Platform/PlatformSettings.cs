using System.Text.RegularExpressions;
using WireHQ.Domain.Common;
using WireHQ.Shared.Results;

namespace WireHQ.Domain.Platform;

/// <summary>
/// Platform-wide (site-wide) configuration owned by the platform operator — <em>not</em> tenant-scoped.
/// There is exactly one row (<see cref="SingletonId"/>). Only a Super Admin can read/write it
/// (<c>IPlatformRequest</c>). Carries the Cloudflare Turnstile CAPTCHA settings and the SMTP settings for
/// transactional email; secrets (Turnstile secret, SMTP password) are stored encrypted
/// (<c>ISecretProtector</c>) and never leave the server. (docs/04-security.md)
/// </summary>
public sealed class PlatformSettings : Entity, IAuditable
{
    /// <summary>The id of the single platform-settings row — there is only ever one.</summary>
    public static readonly Guid SingletonId = Guid.Parse("0f1a7000-0000-7000-8000-000000000001");

    // EF Core
    private PlatformSettings()
    {
    }

    private PlatformSettings(Guid id)
        : base(id)
    {
    }

    /// <summary>When true, the public auth pages require a solved Turnstile challenge.</summary>
    public bool TurnstileEnabled { get; private set; }

    /// <summary>The Turnstile site key (public — sent to the browser to render the widget).</summary>
    public string? TurnstileSiteKey { get; private set; }

    /// <summary>The Turnstile secret key, encrypted at rest. Never returned by any API.</summary>
    public string? TurnstileSecretCiphertext { get; private set; }

    // --- SMTP (transactional email) ---

    /// <summary>When true (and configured), transactional email is sent via the SMTP server below.</summary>
    public bool SmtpEnabled { get; private set; }

    public string? SmtpHost { get; private set; }

    public int SmtpPort { get; private set; } = 587;

    public string? SmtpUsername { get; private set; }

    /// <summary>The SMTP password, encrypted at rest. Never returned by any API.</summary>
    public string? SmtpPasswordCiphertext { get; private set; }

    /// <summary>The envelope/From address transactional email is sent from.</summary>
    public string? SmtpFromEmail { get; private set; }

    /// <summary>The display name on the From address (e.g. "WireHQ").</summary>
    public string? SmtpFromName { get; private set; }

    /// <summary>True ⇒ implicit TLS on connect (port 465); false ⇒ STARTTLS when available (587/25).</summary>
    public bool SmtpUseSsl { get; private set; }

    // --- Analytics (Matomo) ---

    /// <summary>When true (and configured), the Matomo tracker is injected on every page.</summary>
    public bool AnalyticsEnabled { get; private set; }

    /// <summary>The Matomo instance URL (e.g. <c>//analytics.example.com/</c>). Public — sent to the browser.</summary>
    public string? MatomoUrl { get; private set; }

    /// <summary>The Matomo site id (kept as a string — Matomo treats it as an opaque id).</summary>
    public string? MatomoSiteId { get; private set; }

    // --- Stripe (billing) ---

    /// <summary>When true (and configured), the self-serve Stripe billing flows (Checkout/Portal/webhook) are live.</summary>
    public bool StripeEnabled { get; private set; }

    /// <summary>The Stripe publishable key (public — safe to expose; not used by the server-side flows today).</summary>
    public string? StripePublishableKey { get; private set; }

    /// <summary>The Stripe secret key, encrypted at rest. Never returned by any API.</summary>
    public string? StripeSecretCiphertext { get; private set; }

    /// <summary>The Stripe webhook signing secret, encrypted at rest. Never returned by any API.</summary>
    public string? StripeWebhookSecretCiphertext { get; private set; }

    /// <summary>The Stripe Price id for the monthly Pro subscription (<c>price_…</c>). Maps to <c>Edition.Pro</c>.</summary>
    public string? StripeProMonthlyPriceId { get; private set; }

    /// <summary>The Stripe Price id for the annual Pro subscription (<c>price_…</c>). Maps to <c>Edition.Pro</c>.</summary>
    public string? StripeProAnnualPriceId { get; private set; }

    // --- Marketplace commerce (one-off module sales — docs/19-marketplace-licensing.md §4) ---
    // Reuses the same Stripe account (the secret key above); a separate Stripe webhook *endpoint* is used for
    // the payment-mode marketplace events, so it carries its own signing secret. SaaS-only in practice (the
    // Community Edition has no marketplace) — these settings sit dormant there, like the WireHQ.Licensing verifier.

    /// <summary>When true (and Stripe is configured), the public one-off module Checkout flow is live.</summary>
    public bool MarketplaceCommerceEnabled { get; private set; }

    /// <summary>The signing secret for the marketplace Stripe webhook endpoint, encrypted at rest. Never returned.</summary>
    public string? StripeMarketplaceWebhookSecretCiphertext { get; private set; }

    // --- Plan pricing (the operator-set *display* prices shown on the public site + Plan page) ---
    // The actual charge is the Stripe Price referenced above; the operator keeps the two aligned.

    /// <summary>ISO currency code for the displayed plan prices (e.g. <c>GBP</c>). Drives the currency symbol.</summary>
    public string PricingCurrency { get; private set; } = "GBP";

    /// <summary>The displayed monthly Pro price (e.g. 29). Marketing/display only.</summary>
    public decimal ProMonthlyPrice { get; private set; } = 29m;

    /// <summary>The displayed annual Pro price (e.g. 290). Marketing/display only.</summary>
    public decimal ProAnnualPrice { get; private set; } = 290m;

    // --- Branding (the operator's own product name / logo / colour — docs/34, an install-global capability unlocked by
    // the branding.basic Marketplace module; a null field falls back to the shipped WireHQ brand) ---

    private static readonly Regex HexColor = new("^#[0-9A-Fa-f]{6}$", RegexOptions.Compiled);

    /// <summary>The operator's product name (replaces "WireHQ" in the UI + emails). Null ⇒ the shipped default.</summary>
    public string? ProductName { get; private set; }

    /// <summary>The operator's brand accent colour as <c>#rrggbb</c>. Null ⇒ the shipped gold.</summary>
    public string? BrandColor { get; private set; }

    /// <summary>The light-theme logo (a <see cref="BrandAsset"/> id). Null ⇒ the shipped WireHQ mark.</summary>
    public Guid? LogoLightAssetId { get; private set; }

    /// <summary>The dark-theme logo (a <see cref="BrandAsset"/> id). Null ⇒ the shipped WireHQ mark.</summary>
    public Guid? LogoDarkAssetId { get; private set; }

    /// <summary>The favicon (a <see cref="BrandAsset"/> id). Null ⇒ the shipped WireHQ favicon.</summary>
    public Guid? FaviconAssetId { get; private set; }

    /// <summary>Bumped on every branding change so clients + caches can detect a new brand (docs/34 BR-14).</summary>
    public int BrandRevision { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }
    public Guid? CreatedBy { get; private set; }
    public DateTimeOffset? UpdatedAtUtc { get; private set; }
    public Guid? UpdatedBy { get; private set; }

    /// <summary>True once both a site key and a secret are present — Turnstile can actually run.</summary>
    public bool TurnstileConfigured =>
        !string.IsNullOrWhiteSpace(TurnstileSiteKey) && !string.IsNullOrWhiteSpace(TurnstileSecretCiphertext);

    /// <summary>True once a host and a From address are set — SMTP can actually send.</summary>
    public bool SmtpConfigured =>
        !string.IsNullOrWhiteSpace(SmtpHost) && !string.IsNullOrWhiteSpace(SmtpFromEmail);

    /// <summary>True once a Matomo URL and site id are present — the tracker can actually load.</summary>
    public bool AnalyticsConfigured =>
        !string.IsNullOrWhiteSpace(MatomoUrl) && !string.IsNullOrWhiteSpace(MatomoSiteId);

    /// <summary>True once a secret key and at least the monthly Pro price are present — Checkout can run.</summary>
    public bool StripeConfigured =>
        !string.IsNullOrWhiteSpace(StripeSecretCiphertext) && !string.IsNullOrWhiteSpace(StripeProMonthlyPriceId);

    /// <summary>True once a webhook signing secret is present — inbound webhooks can be verified.</summary>
    public bool StripeWebhookConfigured => !string.IsNullOrWhiteSpace(StripeWebhookSecretCiphertext);

    /// <summary>True once the marketplace webhook signing secret is present — its inbound webhooks can be verified.</summary>
    public bool MarketplaceWebhookConfigured => !string.IsNullOrWhiteSpace(StripeMarketplaceWebhookSecretCiphertext);

    /// <summary>True once the operator has switched on marketplace commerce and a Stripe secret key is present —
    /// the one-off Checkout flow can run (a resolvable module price is still required per module).</summary>
    public bool MarketplaceCommerceConfigured =>
        MarketplaceCommerceEnabled && !string.IsNullOrWhiteSpace(StripeSecretCiphertext);

    /// <summary>True once the operator has set any brand override (name, colour or an image).</summary>
    public bool BrandingConfigured =>
        ProductName is not null || BrandColor is not null
        || LogoLightAssetId is not null || LogoDarkAssetId is not null || FaviconAssetId is not null;

    public static PlatformSettings CreateDefault() => new(SingletonId);

    /// <summary>
    /// Update the Turnstile configuration. A <see langword="null"/> <paramref name="secretCiphertext"/>
    /// keeps the existing secret (so the UI never has to round-trip it); an empty string clears it.
    /// </summary>
    public void SetTurnstile(bool enabled, string? siteKey, string? secretCiphertext)
    {
        TurnstileEnabled = enabled;
        TurnstileSiteKey = string.IsNullOrWhiteSpace(siteKey) ? null : siteKey.Trim();

        if (secretCiphertext is not null)
        {
            TurnstileSecretCiphertext = string.IsNullOrWhiteSpace(secretCiphertext) ? null : secretCiphertext;
        }
    }

    /// <summary>Flip just the on/off toggle (used by the demo seeder to keep demo logins unblocked).</summary>
    public void SetTurnstileEnabled(bool enabled) => TurnstileEnabled = enabled;

    /// <summary>
    /// Update the SMTP configuration. A <see langword="null"/> <paramref name="passwordCiphertext"/> keeps
    /// the existing password (so the UI never round-trips it); an empty string clears it.
    /// </summary>
    public void SetSmtp(
        bool enabled,
        string? host,
        int port,
        string? username,
        string? passwordCiphertext,
        string? fromEmail,
        string? fromName,
        bool useSsl)
    {
        SmtpEnabled = enabled;
        SmtpHost = string.IsNullOrWhiteSpace(host) ? null : host.Trim();
        SmtpPort = port is > 0 and <= 65535 ? port : 587;
        SmtpUsername = string.IsNullOrWhiteSpace(username) ? null : username.Trim();
        SmtpFromEmail = string.IsNullOrWhiteSpace(fromEmail) ? null : fromEmail.Trim();
        SmtpFromName = string.IsNullOrWhiteSpace(fromName) ? null : fromName.Trim();
        SmtpUseSsl = useSsl;

        if (passwordCiphertext is not null)
        {
            SmtpPasswordCiphertext = string.IsNullOrWhiteSpace(passwordCiphertext) ? null : passwordCiphertext;
        }
    }

    /// <summary>Update the Matomo analytics configuration (no secrets — all values are public).</summary>
    public void SetAnalytics(bool enabled, string? matomoUrl, string? matomoSiteId)
    {
        AnalyticsEnabled = enabled;
        MatomoUrl = string.IsNullOrWhiteSpace(matomoUrl) ? null : matomoUrl.Trim();
        MatomoSiteId = string.IsNullOrWhiteSpace(matomoSiteId) ? null : matomoSiteId.Trim();
    }

    /// <summary>
    /// Update the Stripe billing configuration. A <see langword="null"/> ciphertext for the secret or webhook
    /// secret keeps the stored value (so the UI never round-trips it); an empty string clears it — exactly how
    /// the Turnstile/SMTP secrets behave. The publishable key and price ids are not secret.
    /// </summary>
    public void SetStripe(
        bool enabled,
        string? publishableKey,
        string? secretCiphertext,
        string? webhookSecretCiphertext,
        string? proMonthlyPriceId,
        string? proAnnualPriceId)
    {
        StripeEnabled = enabled;
        StripePublishableKey = string.IsNullOrWhiteSpace(publishableKey) ? null : publishableKey.Trim();
        StripeProMonthlyPriceId = string.IsNullOrWhiteSpace(proMonthlyPriceId) ? null : proMonthlyPriceId.Trim();
        StripeProAnnualPriceId = string.IsNullOrWhiteSpace(proAnnualPriceId) ? null : proAnnualPriceId.Trim();

        if (secretCiphertext is not null)
        {
            StripeSecretCiphertext = string.IsNullOrWhiteSpace(secretCiphertext) ? null : secretCiphertext;
        }

        if (webhookSecretCiphertext is not null)
        {
            StripeWebhookSecretCiphertext = string.IsNullOrWhiteSpace(webhookSecretCiphertext) ? null : webhookSecretCiphertext;
        }
    }

    /// <summary>
    /// Update the marketplace-commerce configuration (docs/19 §4). Like <see cref="SetStripe"/>, a
    /// <see langword="null"/> <paramref name="marketplaceWebhookSecretCiphertext"/> keeps the stored secret (so
    /// the UI never round-trips it); an empty string clears it. The Stripe *secret key* is shared with billing.
    /// </summary>
    public void SetMarketplaceCommerce(bool enabled, string? marketplaceWebhookSecretCiphertext)
    {
        MarketplaceCommerceEnabled = enabled;

        if (marketplaceWebhookSecretCiphertext is not null)
        {
            StripeMarketplaceWebhookSecretCiphertext =
                string.IsNullOrWhiteSpace(marketplaceWebhookSecretCiphertext) ? null : marketplaceWebhookSecretCiphertext;
        }
    }

    /// <summary>Update the operator-set display prices for the plans (the public site + Plan page render these).</summary>
    public void SetPricing(string? currency, decimal proMonthlyPrice, decimal proAnnualPrice)
    {
        PricingCurrency = string.IsNullOrWhiteSpace(currency) ? "GBP" : currency.Trim().ToUpperInvariant();
        ProMonthlyPrice = proMonthlyPrice < 0 ? 0 : proMonthlyPrice;
        ProAnnualPrice = proAnnualPrice < 0 ? 0 : proAnnualPrice;
    }

    /// <summary>
    /// Update the operator's product name + brand colour, and bump <see cref="BrandRevision"/>. A blank value clears
    /// the field back to the shipped default. The colour is validated to a 6-digit hex (defense in depth over the
    /// command validator) — an invalid value is a caller error and returns <see cref="BrandingErrors.InvalidColor"/>.
    /// </summary>
    public Result SetBranding(string? productName, string? brandColor)
    {
        string? color;
        if (string.IsNullOrWhiteSpace(brandColor))
        {
            color = null;
        }
        else
        {
            var value = brandColor.Trim();
            if (!HexColor.IsMatch(value))
            {
                return BrandingErrors.InvalidColor;
            }

            color = value.ToLowerInvariant();
        }

        ProductName = string.IsNullOrWhiteSpace(productName) ? null : productName.Trim();
        BrandColor = color;
        BrandRevision++;
        return Result.Success();
    }

    /// <summary>Point a brand image slot at an uploaded <see cref="BrandAsset"/> (or <see langword="null"/> to clear
    /// it back to the shipped mark), and bump <see cref="BrandRevision"/>.</summary>
    public void SetBrandAsset(BrandAssetKind kind, Guid? assetId)
    {
        switch (kind)
        {
            case BrandAssetKind.LogoLight:
                LogoLightAssetId = assetId;
                break;
            case BrandAssetKind.LogoDark:
                LogoDarkAssetId = assetId;
                break;
            case BrandAssetKind.Favicon:
                FaviconAssetId = assetId;
                break;
        }

        BrandRevision++;
    }
}
