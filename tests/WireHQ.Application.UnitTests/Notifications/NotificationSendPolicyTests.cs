using FluentAssertions;
using WireHQ.Application.Abstractions.Notifications;
using WireHQ.Application.Features.Notifications;
using WireHQ.Domain.Notifications;
using Xunit;

namespace WireHQ.Application.UnitTests.Notifications;

/// <summary>
/// The two spend-safety rules the SMS pre-code adversarial review flagged (docs/36 §7): the finite-floor daily-cap clamp
/// (a cap is NEVER 0/uncapped) and the mapping of a terminal <see cref="ChannelSendResult.Suppressed"/> to
/// <c>delivery.Cancel</c> (NOT a 6× retry). Both are pure, so they are unit-tested away from the DB-bound scheduler.
/// </summary>
public sealed class NotificationSendPolicyTests
{
    [Theory]
    [InlineData(0, NotificationSendPolicy.DefaultSmsDailyCapPerOrg)] // absent / unset ⇒ the default floor
    [InlineData(-1, NotificationSendPolicy.DefaultSmsDailyCapPerOrg)] // negative ⇒ the default floor (never uncapped)
    [InlineData(-999, NotificationSendPolicy.DefaultSmsDailyCapPerOrg)]
    [InlineData(1, 1)] // a small positive configured value is honoured
    [InlineData(50, 50)]
    [InlineData(NotificationSendPolicy.HardMaxSmsDailyCapPerOrg, NotificationSendPolicy.HardMaxSmsDailyCapPerOrg)]
    [InlineData(NotificationSendPolicy.HardMaxSmsDailyCapPerOrg + 1, NotificationSendPolicy.HardMaxSmsDailyCapPerOrg)] // clamped down
    [InlineData(int.MaxValue, NotificationSendPolicy.HardMaxSmsDailyCapPerOrg)]
    public void ResolveSmsDailyCap_clamps_to_a_finite_floor_and_hard_max(int configured, int expected)
    {
        NotificationSendPolicy.ResolveSmsDailyCap(configured).Should().Be(expected);
    }

    [Fact]
    public void ResolveSmsDailyCap_default_is_never_zero_so_HasReached_can_never_treat_it_as_uncapped()
    {
        // Load-bearing: NotificationChannelUsage.HasReached(cap) treats cap<=0 as "no cap", so a 0 must never reach it.
        NotificationSendPolicy.ResolveSmsDailyCap(0).Should().BePositive();
    }

    [Fact]
    public void ApplyResult_marks_success_and_counts_one_against_the_daily_budget()
    {
        var delivery = NewSmsDelivery();
        var usage = NotificationChannelUsage.Start(delivery.OrganizationId, ChannelKind.Sms, Today);

        NotificationSendPolicy.ApplyResult(delivery, ChannelSendResult.Ok(), usage, Now);

        delivery.Status.Should().Be(NotificationDeliveryStatus.Delivered);
        usage.Count.Should().Be(1);
    }

    [Fact]
    public void ApplyResult_cancels_a_suppressed_result_rather_than_failing_it()
    {
        // A terminal opt-out (carrier 21610 / suppression) → Cancel, NOT MarkFailed — a MarkFailed would re-hammer the
        // opted-out number 6× on the backoff ladder (docs/36 §7).
        var delivery = NewSmsDelivery();
        var usage = NotificationChannelUsage.Start(delivery.OrganizationId, ChannelKind.Sms, Today);

        NotificationSendPolicy.ApplyResult(delivery, ChannelSendResult.Suppressed("Recipient has opted out of SMS."), usage, Now);

        delivery.Status.Should().Be(NotificationDeliveryStatus.Cancelled);
        delivery.Status.Should().NotBe(NotificationDeliveryStatus.Failed);
        delivery.NextAttemptAtUtc.Should().BeNull("a cancelled delivery is terminal and never retried");
        usage.Count.Should().Be(0, "a suppressed send is not counted against the budget");
    }

    [Fact]
    public void ApplyResult_reschedules_a_plain_failure_with_backoff()
    {
        var delivery = NewSmsDelivery();
        var usage = NotificationChannelUsage.Start(delivery.OrganizationId, ChannelKind.Sms, Today);

        NotificationSendPolicy.ApplyResult(delivery, ChannelSendResult.Failed("SMS could not be sent."), usage, Now);

        delivery.Status.Should().Be(NotificationDeliveryStatus.Pending, "a first failure is retryable, not terminal");
        delivery.Attempts.Should().Be(1);
        usage.Count.Should().Be(0);
    }

    // --- Send idempotency: an unconfirmed send is never blind-resent (docs/36 §7/§12, Wave 4 W4) ---

    [Fact]
    public void ApplyResult_marks_an_indeterminate_send_unresolved_and_never_retries_it()
    {
        // THE property W4 exists for. The request left the process and we never learned the outcome, so Twilio may well
        // have texted the recipient. A retry here is the dangerous move: it charges the operator twice and texts a real
        // person twice, most likely while something is already going wrong.
        var delivery = NewSmsDelivery();
        var usage = NotificationChannelUsage.Start(delivery.OrganizationId, ChannelKind.Sms, Today);

        NotificationSendPolicy.ApplyResult(
            delivery, ChannelSendResult.Indeterminate("no confirmation", "SM1234567890"), usage, Now);

        delivery.Status.Should().Be(NotificationDeliveryStatus.Unresolved);
        delivery.Status.Should().NotBe(NotificationDeliveryStatus.Pending, "an unresolved send must not re-enter the retry ladder");
        delivery.NextAttemptAtUtc.Should().BeNull("terminal — nothing is ever due again");
        delivery.IsTerminal.Should().BeTrue("otherwise the drain re-selects it forever");
        delivery.ProviderMessageId.Should().Be("SM1234567890", "the SID is the operator's only handle on a message we could not confirm");
    }

    [Fact]
    public void ApplyResult_counts_an_indeterminate_send_against_the_daily_budget()
    {
        // It probably cost money — that is the whole premise of not retrying it. Not counting it would let a run of
        // unconfirmed sends spend straight past a cap whose only job is to bound the operator's carrier bill.
        var delivery = NewSmsDelivery();
        var usage = NotificationChannelUsage.Start(delivery.OrganizationId, ChannelKind.Sms, Today);

        NotificationSendPolicy.ApplyResult(delivery, ChannelSendResult.Indeterminate("no confirmation"), usage, Now);

        usage.Count.Should().Be(1);
    }

    [Fact]
    public void ApplyResult_records_the_provider_message_id_on_a_successful_send()
    {
        var delivery = NewSmsDelivery();
        var usage = NotificationChannelUsage.Start(delivery.OrganizationId, ChannelKind.Sms, Today);

        NotificationSendPolicy.ApplyResult(
            delivery, ChannelSendResult.Ok(providerMessageId: "SMabcdef"), usage, Now);

        delivery.ProviderMessageId.Should().Be("SMabcdef");
    }

    [Fact]
    public void A_definite_provider_refusal_is_still_retried_so_the_two_failures_are_not_conflated()
    {
        // The counterpart to the test above, and the reason the distinction has to exist at all: a 4xx is Twilio saying
        // it rejected the request, so nothing was queued and retrying is both safe and free. Collapsing the two would
        // either double-charge on timeouts or abandon deliveries that were never sent.
        var delivery = NewSmsDelivery();
        var usage = NotificationChannelUsage.Start(delivery.OrganizationId, ChannelKind.Sms, Today);

        NotificationSendPolicy.ApplyResult(delivery, ChannelSendResult.Failed("SMS provider returned HTTP 400."), usage, Now);

        delivery.Status.Should().Be(NotificationDeliveryStatus.Pending);
        delivery.NextAttemptAtUtc.Should().NotBeNull();
        usage.Count.Should().Be(0);
    }

    // --- Dark-boundary send-gate (docs/36 §2/§10 — "no external send before Slice D") ---

    [Fact]
    public void Sms_is_off_by_default_so_the_channel_is_dark_without_operator_opt_in()
    {
        var options = new SmsNotificationOptions();

        options.Enabled.Should().BeFalse("Notifications:Sms:Enabled defaults OFF — Slice B is dark by default");
        options.DailyCapPerOrg.Should().Be(0);
    }

    [Fact]
    public void CancelIfSmsSendingDisabled_cancels_an_sms_delivery_when_sending_is_disabled()
    {
        var delivery = NewSmsDelivery();

        var gated = NotificationSendPolicy.CancelIfSmsSendingDisabled(delivery, smsSendingEnabled: false);

        gated.Should().BeTrue();
        delivery.Status.Should().Be(NotificationDeliveryStatus.Cancelled, "an SMS delivery is terminally cancelled while sending is dark");
        delivery.Status.Should().NotBe(NotificationDeliveryStatus.Pending);
        delivery.NextAttemptAtUtc.Should().BeNull("a cancelled delivery is terminal and never retried");
    }

    [Fact]
    public void CancelIfSmsSendingDisabled_passes_through_when_sending_is_enabled()
    {
        var delivery = NewSmsDelivery();

        var gated = NotificationSendPolicy.CancelIfSmsSendingDisabled(delivery, smsSendingEnabled: true);

        gated.Should().BeFalse();
        delivery.Status.Should().Be(NotificationDeliveryStatus.Pending, "an enabled SMS delivery proceeds to the send path");
    }

    [Fact]
    public void CancelIfSmsSendingDisabled_never_gates_a_non_sms_channel()
    {
        var email = NotificationDelivery.Create(
            organizationId: Guid.NewGuid(), ruleId: Guid.NewGuid(), jobId: Guid.NewGuid(), ChannelKind.Email,
            requiredFeatures: Array.Empty<string>(), recipient: "ops@wirehq.io", subject: "WireHQ: alert",
            body: "body", dedupValue: null, nowUtc: Now);

        NotificationSendPolicy.CancelIfSmsSendingDisabled(email, smsSendingEnabled: false).Should().BeFalse();
        email.Status.Should().Be(NotificationDeliveryStatus.Pending, "the dark-gate is SMS-only — Email is unaffected");
    }

    [Fact]
    public void HoldIfA2pNotAttested_holds_an_sms_delivery_as_a_transient_retry_when_the_config_is_not_attested()
    {
        var delivery = NewSmsDelivery();
        var config = NotificationChannelConfig.Create(delivery.OrganizationId, ChannelKind.Sms);
        config.SetSmsProvider(NotificationProviderKind.Twilio, "SECRET", "+15005550006"); // configured, but NOT attested

        var held = NotificationSendPolicy.HoldIfA2pNotAttested(delivery, config, Now);

        held.Should().BeTrue();
        delivery.Status.Should().Be(NotificationDeliveryStatus.Pending, "a hold is a transient failure (retries), NOT a Cancel");
        delivery.Attempts.Should().Be(1, "the delivery was marked failed once and will retry on the backoff ladder");
    }

    [Fact]
    public void HoldIfA2pNotAttested_passes_through_when_the_config_is_attested()
    {
        var delivery = NewSmsDelivery();
        var config = NotificationChannelConfig.Create(delivery.OrganizationId, ChannelKind.Sms);
        config.SetSmsProvider(NotificationProviderKind.Twilio, "SECRET", "+15005550006");
        config.AttestA2p(Guid.NewGuid(), reference: null, Now);

        NotificationSendPolicy.HoldIfA2pNotAttested(delivery, config, Now).Should().BeFalse();
        delivery.Status.Should().Be(NotificationDeliveryStatus.Pending, "an attested SMS delivery proceeds to the send path");
    }

    [Fact]
    public void HoldIfInboundNotReady_holds_when_the_webhook_has_never_carried_a_signed_request()
    {
        var delivery = NewSmsDelivery();
        var config = NotificationChannelConfig.Create(delivery.OrganizationId, ChannelKind.Sms);
        config.SetSmsProvider(NotificationProviderKind.Twilio, "SECRET", "+15005550006");
        config.AttestA2p(Guid.NewGuid(), reference: null, Now);
        config.MintInboundWebhookToken("hash", "abc123", Now, grace: null); // minted, but never proven

        var held = NotificationSendPolicy.HoldIfInboundNotReady(delivery, config, Now);

        held.Should().BeTrue(
            "a minted token proves nothing — sending under an unconnected webhook means messaging people who have " +
            "no working way to stop us, which is the one failure the whole compliance design exists to prevent");
        delivery.Status.Should().Be(NotificationDeliveryStatus.Pending, "a hold is transient, not a Cancel");
        delivery.Attempts.Should().Be(1);
    }

    [Fact]
    public void HoldIfInboundNotReady_passes_once_a_signed_request_has_arrived()
    {
        var delivery = NewSmsDelivery();
        var config = NotificationChannelConfig.Create(delivery.OrganizationId, ChannelKind.Sms);
        config.SetSmsProvider(NotificationProviderKind.Twilio, "SECRET", "+15005550006");
        config.MintInboundWebhookToken("hash", "abc123", Now, grace: null);
        config.MarkInboundCurrentTokenVerified(Now);

        NotificationSendPolicy.HoldIfInboundNotReady(delivery, config, Now).Should().BeFalse();
        delivery.Status.Should().Be(NotificationDeliveryStatus.Pending);
    }

    [Fact]
    public void HoldIfInboundNotReady_holds_an_org_with_no_token_at_all()
    {
        var delivery = NewSmsDelivery();
        var config = NotificationChannelConfig.Create(delivery.OrganizationId, ChannelKind.Sms);
        config.SetSmsProvider(NotificationProviderKind.Twilio, "SECRET", "+15005550006");

        NotificationSendPolicy.HoldIfInboundNotReady(delivery, config, Now).Should().BeTrue(
            "this is the pre-W2 state of every org — sending must not be the default before STOP can be received");
    }

    [Fact]
    public void HoldIfInboundNotReady_never_gates_a_non_sms_channel()
    {
        var email = NotificationDelivery.Create(
            organizationId: Guid.NewGuid(), ruleId: Guid.NewGuid(), jobId: Guid.NewGuid(), ChannelKind.Email,
            requiredFeatures: Array.Empty<string>(), recipient: "ops@wirehq.io", subject: "WireHQ: alert",
            body: "body", dedupValue: null, nowUtc: Now);

        NotificationSendPolicy.HoldIfInboundNotReady(email, config: null, Now).Should().BeFalse();
        email.Status.Should().Be(NotificationDeliveryStatus.Pending);
    }

    [Fact]
    public void HoldIfA2pNotAttested_never_gates_a_non_sms_channel()
    {
        var email = NotificationDelivery.Create(
            organizationId: Guid.NewGuid(), ruleId: Guid.NewGuid(), jobId: Guid.NewGuid(), ChannelKind.Email,
            requiredFeatures: Array.Empty<string>(), recipient: "ops@wirehq.io", subject: "WireHQ: alert",
            body: "body", dedupValue: null, nowUtc: Now);

        NotificationSendPolicy.HoldIfA2pNotAttested(email, config: null, Now).Should().BeFalse();
    }

    private static readonly DateTimeOffset Now = new(2026, 7, 19, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 7, 19);

    private static NotificationDelivery NewSmsDelivery() => NotificationDelivery.Create(
        organizationId: Guid.NewGuid(), ruleId: Guid.NewGuid(), jobId: Guid.NewGuid(), ChannelKind.Sms,
        requiredFeatures: new[] { "notifications.sms" }, recipient: "+15005550006", subject: "WireHQ: alert",
        body: "body", dedupValue: null, nowUtc: Now);
}
