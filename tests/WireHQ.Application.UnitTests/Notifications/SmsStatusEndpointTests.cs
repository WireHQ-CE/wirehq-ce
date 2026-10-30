using FluentAssertions;
using WireHQ.Application.Features.Notifications;
using Xunit;

namespace WireHQ.Application.UnitTests.Notifications;

/// <summary>
/// The status-callback URL composer (docs/36 §7, Wave 4 W4). The provider signs the URL it posts to, so the URL we hand
/// over on send and the URL we reconstruct to verify the callback MUST come from one place — if they drift, the HMAC
/// never matches and every callback is silently dropped while everything looks healthy. Pinned here, exactly as the
/// inbound composer is.
/// </summary>
public sealed class SmsStatusEndpointTests
{
    private static readonly Guid DeliveryId = Guid.Parse("0199c0de-cafe-7000-8000-00000000beef");

    [Fact]
    public void The_composed_url_and_the_verification_url_are_identical_for_the_same_request()
    {
        const string baseUrl = "https://wirehq.example";

        var handedToProvider = SmsStatusEndpoint.ComposeStatusUrl(baseUrl, DeliveryId)!;

        // What the provider then POSTs to is that URL's path — reconstruct and compare.
        var path = handedToProvider[baseUrl.Length..];
        SmsStatusEndpoint.ComposeVerificationUrl(baseUrl, path).Should().Be(handedToProvider);
    }

    [Fact]
    public void The_url_carries_the_delivery_id_under_the_scrubbed_status_prefix()
    {
        var url = SmsStatusEndpoint.ComposeStatusUrl("https://wirehq.example", DeliveryId);

        url.Should().Be($"https://wirehq.example/api/v1/sms/status/{DeliveryId}");
        SmsStatusEndpoint.StatusPathPrefix.Should().Be("/api/v1/sms/status/",
            "ServerSpanScrubber masks the segment after /sms/status/ — moving this route silently changes what is masked");
    }

    [Theory]
    [InlineData(null)]
    public void No_pinned_base_means_no_callback_url_rather_than_a_guessed_one(string? baseUrl)
    {
        // Fail closed, like the inbound composer: a fallback origin would point a real provider at somebody else's host.
        SmsStatusEndpoint.ComposeStatusUrl(baseUrl, DeliveryId).Should().BeNull();
    }

    [Fact]
    public void An_empty_delivery_id_produces_no_callback_url()
    {
        // A callback that cannot name a delivery resolves nothing, so asking for one would only invite noise.
        SmsStatusEndpoint.ComposeStatusUrl("https://wirehq.example", Guid.Empty).Should().BeNull();
    }
}
