using FluentAssertions;
using WireHQ.Domain.Notifications;
using Xunit;

namespace WireHQ.Domain.UnitTests.Notifications;

/// <summary>
/// The inbound webhook token lifecycle (SMS Wave 4, W2). These pin the compliance guarantee rather than the mechanics:
/// an organisation may only send SMS once its STOP webhook has been <b>demonstrated</b> to work, and a rotation must
/// never open a window in which a STOP is dropped.
/// </summary>
public sealed class InboundWebhookTokenTests
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 31, 9, 0, 0, TimeSpan.Zero);

    private static NotificationChannelConfig SmsConfig()
    {
        var config = NotificationChannelConfig.Create(Guid.CreateVersion7(), ChannelKind.Sms);
        config.SetSmsProvider(NotificationProviderKind.Twilio, "cipher", "+15550001111");
        return config;
    }

    [Fact]
    public void A_minted_token_is_not_yet_ready_to_send()
    {
        var config = SmsConfig();

        config.MintInboundWebhookToken("hash-1", "abc123", T0, grace: null);

        config.HasInboundWebhookToken.Should().BeTrue();
        config.InboundReady.Should().BeFalse(
            "a token that has never carried a signed request proves nothing — an unconnected webhook is, from the " +
            "subscriber's side, the same as having no webhook at all");
    }

    [Fact]
    public void Readiness_requires_a_verified_request_on_the_current_token()
    {
        var config = SmsConfig();
        config.MintInboundWebhookToken("hash-1", "abc123", T0, grace: null);

        config.MarkInboundCurrentTokenVerified(T0.AddMinutes(5));

        config.InboundReady.Should().BeTrue();
        config.InboundCurrentTokenVerifiedAtUtc.Should().Be(T0.AddMinutes(5));
    }

    [Fact]
    public void Rotating_keeps_the_old_token_working_through_the_grace_window()
    {
        var config = SmsConfig();
        config.MintInboundWebhookToken("hash-1", "abc123", T0, grace: null);
        config.MarkInboundCurrentTokenVerified(T0);

        config.MintInboundWebhookToken("hash-2", "def456", T0, grace: NotificationChannelConfig.InboundWebhookTokenGrace);

        config.PreviousInboundWebhookTokenHash.Should().Be("hash-1",
            "the operator's provider is still posting to the old URL until they repoint it — a STOP arriving in that " +
            "gap is exactly the one we cannot lose");
        config.IsInboundTokenRotating(T0.AddHours(1)).Should().BeTrue();
        config.IsInboundTokenRotating(T0 + NotificationChannelConfig.InboundWebhookTokenGrace.Add(TimeSpan.FromMinutes(1)))
            .Should().BeFalse("the overlap lapses on read — no sweeper needed");
    }

    [Fact]
    public void Rotating_revokes_readiness_until_the_new_token_is_proven()
    {
        var config = SmsConfig();
        config.MintInboundWebhookToken("hash-1", "abc123", T0, grace: null);
        config.MarkInboundCurrentTokenVerified(T0);
        config.InboundReady.Should().BeTrue();

        config.MintInboundWebhookToken("hash-2", "def456", T0, grace: NotificationChannelConfig.InboundWebhookTokenGrace);

        config.InboundReady.Should().BeFalse(
            "the proof is bound to the token that earned it — carrying it over would let a botched rotation keep " +
            "reporting ready while nothing reaches the new URL");
    }

    [Fact]
    public void Revoking_stops_sending_immediately_and_discards_the_old_hash()
    {
        var config = SmsConfig();
        config.MintInboundWebhookToken("hash-1", "abc123", T0, grace: null);
        config.MarkInboundCurrentTokenVerified(T0);

        config.ClearInboundWebhookToken();

        config.HasInboundWebhookToken.Should().BeFalse();
        config.PreviousInboundWebhookTokenHash.Should().BeNull("a revoke is the leak response — no overlap");
        config.InboundReady.Should().BeFalse("no inbound endpoint means no receivable STOP, so sending must hold too");
    }

    [Fact]
    public void Minting_after_a_leak_does_not_leave_the_old_token_alive()
    {
        var config = SmsConfig();
        config.MintInboundWebhookToken("leaked", "abc123", T0, grace: null);

        config.MintInboundWebhookToken("fresh", "def456", T0, grace: null);

        config.PreviousInboundWebhookTokenHash.Should().BeNull();
        config.IsInboundTokenRotating(T0).Should().BeFalse();
    }

    [Fact]
    public void Changing_the_sender_revokes_the_proof_but_keeps_the_token()
    {
        var config = SmsConfig();
        config.MintInboundWebhookToken("hash-1", "abc123", T0, grace: null);
        config.MarkInboundCurrentTokenVerified(T0);

        config.SetSmsProvider(NotificationProviderKind.Twilio, "new-cipher", "+15550002222");

        config.InboundReady.Should().BeFalse("a different account is a different sender — the old proof was for something else");
        config.HasInboundWebhookToken.Should().BeTrue(
            "but the URL is account-independent, and invalidating it would silently break a provider still posting to it");
    }

    [Fact]
    public void A_request_on_the_previous_token_does_not_satisfy_the_hold()
    {
        var config = SmsConfig();
        config.MintInboundWebhookToken("hash-1", "abc123", T0, grace: null);
        config.MintInboundWebhookToken("hash-2", "def456", T0, grace: NotificationChannelConfig.InboundWebhookTokenGrace);

        config.MarkInboundPreviousTokenSeen(T0.AddMinutes(5));

        config.InboundReady.Should().BeFalse(
            "traffic on the OLD token proves the provider has NOT been repointed — treating it as proof of the new " +
            "one is precisely the mistake that would let a half-finished rotation send");
        config.InboundPreviousTokenSeenAtUtc.Should().Be(T0.AddMinutes(5));
    }
}
