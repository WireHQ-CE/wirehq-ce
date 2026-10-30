using FluentAssertions;
using WireHQ.Application.Features.Notifications;
using WireHQ.Domain.Notifications;
using Xunit;

namespace WireHQ.Application.UnitTests.Notifications;

/// <summary>
/// The SMS provider read surface (docs/36-notifications.md §5): the encrypted credential is WRITE-ONLY — it is never
/// returned by any query. The status DTO exposes only provider + configured + a masked From, so the "credential never
/// returned" property is unit-tested on the pure mapping.
/// </summary>
public sealed class SmsProviderStatusTests
{
    [Fact]
    public void Status_maps_configured_and_masks_from_without_exposing_the_credential()
    {
        var config = NotificationChannelConfig.Create(Guid.NewGuid(), ChannelKind.Sms);
        config.SetSmsProvider(NotificationProviderKind.Twilio, credentialCiphertext: "ENCRYPTED-SECRET", fromValue: "+14155550123");

        var dto = SmsProviderStatus.From(config);

        dto.Provider.Should().Be(nameof(NotificationProviderKind.Twilio));
        dto.Configured.Should().BeTrue();
        dto.Enabled.Should().BeTrue();
        dto.FromMasked.Should().Be(new string('•', "+14155550123".Length - 4) + "0123");

        // The DTO must not carry the credential in ANY field (no property equals/contains the ciphertext).
        typeof(SmsProviderStatusDto).GetProperties().Select(p => p.Name)
            .Should().NotContain(n => n.Contains("Credential", StringComparison.OrdinalIgnoreCase));
        dto.ToString().Should().NotContain("ENCRYPTED-SECRET");
    }

    [Fact]
    public void Unconfigured_org_reports_not_configured_with_no_from()
    {
        var none = SmsProviderStatus.From(null);
        none.Provider.Should().Be("None");
        none.Configured.Should().BeFalse();
        none.InboundTokenPresent.Should().BeFalse();
        none.InboundReady.Should().BeFalse();

        var empty = NotificationChannelConfig.Create(Guid.NewGuid(), ChannelKind.Sms);
        var dto = SmsProviderStatus.From(empty);
        dto.Configured.Should().BeFalse();
        dto.FromMasked.Should().BeNull();
        dto.A2pAttested.Should().BeFalse("a fresh config has no attestation");
        dto.InboundReady.Should().BeFalse("no token has been minted, let alone demonstrated");
    }

    [Fact]
    public void Status_surfaces_the_a2p_attestation_but_never_the_credential()
    {
        var config = NotificationChannelConfig.Create(Guid.NewGuid(), ChannelKind.Sms);
        config.SetSmsProvider(NotificationProviderKind.Twilio, "ENCRYPTED-SECRET", "+14155550123");
        var user = Guid.NewGuid();
        config.AttestA2p(user, "CAMPAIGN-123", DateTimeOffset.UnixEpoch);

        var dto = SmsProviderStatus.From(config);

        dto.A2pAttested.Should().BeTrue();
        dto.A2pAttestedAtUtc.Should().Be(DateTimeOffset.UnixEpoch);
        dto.A2pReference.Should().Be("CAMPAIGN-123");
        dto.ToString().Should().NotContain("ENCRYPTED-SECRET");
    }

    [Fact]
    public void Changing_the_provider_clears_the_a2p_attestation()
    {
        var config = NotificationChannelConfig.Create(Guid.NewGuid(), ChannelKind.Sms);
        config.SetSmsProvider(NotificationProviderKind.Twilio, "SECRET-1", "+14155550123");
        config.AttestA2p(Guid.NewGuid(), "CAMPAIGN-123", DateTimeOffset.UnixEpoch);

        // Re-saving the provider (a new number/account = a new campaign) must force re-attestation.
        config.SetSmsProvider(NotificationProviderKind.Twilio, "SECRET-2", "+14155550999");

        config.A2pAttested.Should().BeFalse();
        SmsProviderStatus.From(config).A2pReference.Should().BeNull();
    }

    [Theory]
    [InlineData("+14155550123", "••••••••0123")]
    [InlineData("MG0123", "••0123")]
    [InlineData("1234", "••••")]
    [InlineData("12", "••••")]
    public void MaskFrom_keeps_only_the_last_four(string from, string expected)
    {
        SmsProviderStatus.MaskFrom(from).Should().Be(expected);
    }
}
