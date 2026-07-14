using FluentAssertions;
using WireHQ.Domain.Platform;
using Xunit;

namespace WireHQ.Domain.UnitTests.Platform;

public sealed class PlatformSettingsTests
{
    [Fact]
    public void CreateDefault_is_the_singleton_and_starts_disabled_and_unconfigured()
    {
        var settings = PlatformSettings.CreateDefault();

        settings.Id.Should().Be(PlatformSettings.SingletonId);
        settings.TurnstileEnabled.Should().BeFalse();
        settings.TurnstileConfigured.Should().BeFalse();
    }

    [Fact]
    public void SetTurnstile_sets_enabled_site_key_and_secret()
    {
        var settings = PlatformSettings.CreateDefault();

        settings.SetTurnstile(enabled: true, siteKey: "site", secretCiphertext: "cipher");

        settings.TurnstileEnabled.Should().BeTrue();
        settings.TurnstileSiteKey.Should().Be("site");
        settings.TurnstileSecretCiphertext.Should().Be("cipher");
        settings.TurnstileConfigured.Should().BeTrue();
    }

    [Fact]
    public void SetTurnstile_with_a_null_secret_keeps_the_existing_secret()
    {
        var settings = PlatformSettings.CreateDefault();
        settings.SetTurnstile(true, "site", "cipher");

        settings.SetTurnstile(false, "site-2", secretCiphertext: null);

        settings.TurnstileEnabled.Should().BeFalse();
        settings.TurnstileSiteKey.Should().Be("site-2");
        settings.TurnstileSecretCiphertext.Should().Be("cipher", because: "a null secret means 'keep'");
    }

    [Fact]
    public void SetTurnstile_with_a_blank_secret_clears_it()
    {
        var settings = PlatformSettings.CreateDefault();
        settings.SetTurnstile(true, "site", "cipher");

        settings.SetTurnstile(true, "site", secretCiphertext: "");

        settings.TurnstileSecretCiphertext.Should().BeNull();
        settings.TurnstileConfigured.Should().BeFalse();
    }

    [Fact]
    public void SetSmtp_stores_the_config_and_reports_configured()
    {
        var settings = PlatformSettings.CreateDefault();

        settings.SetSmtp(enabled: true, host: "smtp.test", port: 587, username: "u",
            passwordCiphertext: "cipher", fromEmail: "no-reply@test", fromName: "WireHQ", useSsl: false);

        settings.SmtpEnabled.Should().BeTrue();
        settings.SmtpHost.Should().Be("smtp.test");
        settings.SmtpPort.Should().Be(587);
        settings.SmtpFromEmail.Should().Be("no-reply@test");
        settings.SmtpPasswordCiphertext.Should().Be("cipher");
        settings.SmtpConfigured.Should().BeTrue();
    }

    [Fact]
    public void SetSmtp_with_a_null_password_keeps_the_existing_one()
    {
        var settings = PlatformSettings.CreateDefault();
        settings.SetSmtp(true, "smtp.test", 587, "u", "cipher", "no-reply@test", "WireHQ", false);

        settings.SetSmtp(false, "smtp.test", 25, "u", passwordCiphertext: null, "no-reply@test", "WireHQ", false);

        settings.SmtpEnabled.Should().BeFalse();
        settings.SmtpPort.Should().Be(25);
        settings.SmtpPasswordCiphertext.Should().Be("cipher", because: "a null password means 'keep'");
    }

    [Fact]
    public void SetSmtp_falls_back_to_587_for_an_out_of_range_port()
    {
        var settings = PlatformSettings.CreateDefault();

        settings.SetSmtp(true, "smtp.test", port: 0, null, "cipher", "no-reply@test", null, false);

        settings.SmtpPort.Should().Be(587);
    }

    [Fact]
    public void SetStripe_stores_keys_and_reports_configured_once_a_secret_and_price_are_present()
    {
        var settings = PlatformSettings.CreateDefault();
        settings.StripeConfigured.Should().BeFalse();

        settings.SetStripe(
            enabled: true, publishableKey: "pk_test", secretCiphertext: "sk_cipher",
            webhookSecretCiphertext: "wh_cipher", proMonthlyPriceId: "price_monthly", proAnnualPriceId: "price_annual");

        settings.StripeEnabled.Should().BeTrue();
        settings.StripePublishableKey.Should().Be("pk_test");
        settings.StripeSecretCiphertext.Should().Be("sk_cipher");
        settings.StripeWebhookSecretCiphertext.Should().Be("wh_cipher");
        settings.StripeProMonthlyPriceId.Should().Be("price_monthly");
        settings.StripeProAnnualPriceId.Should().Be("price_annual");
        settings.StripeConfigured.Should().BeTrue();
        settings.StripeWebhookConfigured.Should().BeTrue();
    }

    [Fact]
    public void SetStripe_with_null_secrets_keeps_the_existing_ones_but_blank_clears_them()
    {
        var settings = PlatformSettings.CreateDefault();
        settings.SetStripe(true, "pk", "sk_cipher", "wh_cipher", "price_monthly", null);

        // A null ciphertext means "keep" (the UI never round-trips the stored secret).
        settings.SetStripe(false, "pk2", secretCiphertext: null, webhookSecretCiphertext: null, "price_monthly", null);
        settings.StripeEnabled.Should().BeFalse();
        settings.StripePublishableKey.Should().Be("pk2");
        settings.StripeSecretCiphertext.Should().Be("sk_cipher", because: "a null secret means 'keep'");
        settings.StripeWebhookSecretCiphertext.Should().Be("wh_cipher");

        // An empty string explicitly clears it.
        settings.SetStripe(false, "pk2", secretCiphertext: "", webhookSecretCiphertext: "", "price_monthly", null);
        settings.StripeSecretCiphertext.Should().BeNull();
        settings.StripeWebhookSecretCiphertext.Should().BeNull();
        settings.StripeConfigured.Should().BeFalse(because: "no secret means Checkout cannot run");
    }
}
