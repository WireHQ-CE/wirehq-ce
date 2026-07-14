using System.Net;
using FluentAssertions;
using WireHQ.Domain.Webhooks;
using Xunit;

namespace WireHQ.Domain.UnitTests.Webhooks;

public sealed class WebhookAddressGuardTests
{
    [Theory]
    [InlineData("127.0.0.1", true)]        // loopback
    [InlineData("10.0.0.5", true)]         // RFC1918
    [InlineData("172.16.4.4", true)]       // RFC1918
    [InlineData("172.32.0.1", false)]      // just outside 172.16/12
    [InlineData("192.168.1.1", true)]      // RFC1918
    [InlineData("169.254.169.254", true)]  // link-local / cloud metadata
    [InlineData("100.64.0.1", true)]       // CGNAT
    [InlineData("0.0.0.0", true)]          // unspecified
    [InlineData("8.8.8.8", false)]         // public
    [InlineData("93.184.216.34", false)]   // public
    [InlineData("::1", true)]              // IPv6 loopback
    [InlineData("fe80::1", true)]          // IPv6 link-local
    [InlineData("fc00::1", true)]          // IPv6 ULA
    [InlineData("2606:4700:4700::1111", false)] // public IPv6
    public void IsBlocked_classifies_private_and_public_addresses(string ip, bool expected) =>
        WebhookAddressGuard.IsBlocked(IPAddress.Parse(ip)).Should().Be(expected);
}

public sealed class WebhookEventMatcherTests
{
    [Theory]
    [InlineData("identity.users.invite", "identity.users.invite", true)]   // exact
    [InlineData("identity.users.invite", "identity.users.update", false)]
    [InlineData("identity.users.*", "identity.users.invite", true)]        // prefix glob
    [InlineData("identity.users.*", "identity.users", true)]               // prefix itself
    [InlineData("identity.users.*", "identity.usersX", false)]             // not a false-prefix
    [InlineData("identity.users.*", "identity.roles.read", false)]
    [InlineData("*", "anything.at.all", true)]                             // all
    public void Matches_honours_exact_prefix_and_wildcard(string pattern, string action, bool expected) =>
        WebhookEventMatcher.Matches(pattern, action).Should().Be(expected);
}

public sealed class WebhookEndpointTests
{
    private static readonly Guid Org = Guid.NewGuid();

    [Fact]
    public void Create_rejects_a_non_http_url()
    {
        WebhookEndpoint.Create(Org, "ftp://example.com", null, ["*"], "cipher").IsFailure.Should().BeTrue();
        WebhookEndpoint.Create(Org, "not-a-url", null, ["*"], "cipher").IsFailure.Should().BeTrue();
    }

    [Fact]
    public void Create_requires_at_least_one_event_type()
    {
        WebhookEndpoint.Create(Org, "https://example.com/hook", null, [], "cipher").IsFailure.Should().BeTrue();
        WebhookEndpoint.Create(Org, "https://example.com/hook", null, [" "], "cipher").IsFailure.Should().BeTrue();
    }

    [Fact]
    public void A_valid_endpoint_is_created_active_and_matches_its_subscriptions()
    {
        var endpoint = WebhookEndpoint.Create(Org, "https://example.com/hook", "CI", ["wg.*", "api.keys.created"], "cipher").Value;

        endpoint.IsActive.Should().BeTrue();
        endpoint.EventTypes.Should().HaveCount(2);
        endpoint.Matches("wg.peers.created").Should().BeTrue();
        endpoint.Matches("api.keys.created").Should().BeTrue();
        endpoint.Matches("identity.users.invite").Should().BeFalse();

        endpoint.Disable();
        endpoint.IsActive.Should().BeFalse();
        endpoint.Enable();
        endpoint.IsActive.Should().BeTrue();
    }
}

public sealed class WebhookDeliveryTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 8, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_new_delivery_is_pending_and_due_immediately()
    {
        var delivery = WebhookDelivery.Create(Guid.NewGuid(), Guid.NewGuid(), "wg.peers.created", "{}", Now);

        delivery.Status.Should().Be(WebhookDeliveryStatus.Pending);
        delivery.Attempts.Should().Be(0);
        delivery.NextAttemptAtUtc.Should().Be(Now);
    }

    [Fact]
    public void A_success_marks_delivered()
    {
        var delivery = WebhookDelivery.Create(Guid.NewGuid(), Guid.NewGuid(), "e", "{}", Now);

        delivery.MarkSucceeded(200, Now);

        delivery.Status.Should().Be(WebhookDeliveryStatus.Delivered);
        delivery.Attempts.Should().Be(1);
        delivery.LastResponseCode.Should().Be(200);
        delivery.DeliveredAtUtc.Should().Be(Now);
        delivery.NextAttemptAtUtc.Should().BeNull();
        delivery.IsTerminal.Should().BeTrue();
    }

    [Fact]
    public void Failures_retry_with_growing_backoff_then_fail_after_the_cap()
    {
        var delivery = WebhookDelivery.Create(Guid.NewGuid(), Guid.NewGuid(), "e", "{}", Now);

        // First failure → retry in 30s, still pending.
        delivery.MarkFailed(500, "HTTP 500", Now);
        delivery.Status.Should().Be(WebhookDeliveryStatus.Pending);
        delivery.Attempts.Should().Be(1);
        delivery.NextAttemptAtUtc.Should().Be(Now.AddSeconds(30));

        // Second failure → the next backoff step (2m).
        delivery.MarkFailed(500, "HTTP 500", Now);
        delivery.Attempts.Should().Be(2);
        delivery.NextAttemptAtUtc.Should().Be(Now.AddMinutes(2));

        // Exhaust the remaining attempts.
        delivery.MarkFailed(500, null, Now); // 3
        delivery.MarkFailed(500, null, Now); // 4
        delivery.MarkFailed(500, null, Now); // 5 — last retry
        delivery.Status.Should().Be(WebhookDeliveryStatus.Pending);

        delivery.MarkFailed(500, null, Now); // 6 — give up
        delivery.Attempts.Should().Be(WebhookDelivery.MaxAttempts);
        delivery.Status.Should().Be(WebhookDeliveryStatus.Failed);
        delivery.NextAttemptAtUtc.Should().BeNull();
        delivery.IsTerminal.Should().BeTrue();
    }

    [Fact]
    public void Cancel_is_terminal_and_not_retried()
    {
        var delivery = WebhookDelivery.Create(Guid.NewGuid(), Guid.NewGuid(), "e", "{}", Now);

        delivery.Cancel("Endpoint disabled or removed", Now);

        delivery.Status.Should().Be(WebhookDeliveryStatus.Failed);
        delivery.NextAttemptAtUtc.Should().BeNull();
        delivery.LastError.Should().Be("Endpoint disabled or removed");
        delivery.IsTerminal.Should().BeTrue();
    }
}
