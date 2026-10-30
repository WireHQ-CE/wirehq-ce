using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WireHQ.Application.Abstractions.Persistence;
using WireHQ.Application.Abstractions.Security;
using WireHQ.Application.Features.Notifications;
using WireHQ.Domain.Notifications;
using Xunit;

namespace WireHQ.Api.IntegrationTests;

/// <summary>
/// The delivery status callback (docs/36 §7/§12, SMS Wave 4 W4) — the half that turns "possibly sent" back into a fact.
/// Proves end-to-end against a real Postgres that a correctly Twilio-signed callback resolves an <c>Unresolved</c>
/// delivery, that an unsigned one changes nothing, that a replay is a no-op, that a callback cannot rewrite an outcome
/// we observed ourselves, that one org's credential cannot authenticate a callback about another org's delivery, and
/// that a carrier opt-out arriving on this path self-heals the suppression ledger just as it does on the send path.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class SmsStatusCallbackTests(WireHqApiFactory factory)
{
    private readonly WireHqApiFactory _factory = factory;

    [Fact]
    public async Task A_signed_delivered_callback_resolves_an_unresolved_delivery()
    {
        var org = await ArrangeSmsOrgAsync("Status Delivered");
        var deliveryId = await SeedUnresolvedDeliveryAsync(org.OrgId, "+447700900201");

        var response = await PostStatusAsync(org, deliveryId, "delivered", messageSid: "SM-final");

        response.StatusCode.Should().Be(HttpStatusCode.NoContent, "the callback is fail-closed + no-oracle — always 204");
        var delivery = await LoadAsync(deliveryId);
        delivery.Status.Should().Be(NotificationDeliveryStatus.Delivered);
        delivery.ProviderMessageId.Should().Be("SM-final");
    }

    [Fact]
    public async Task A_signed_failure_callback_resolves_to_failed_and_does_not_reschedule()
    {
        var org = await ArrangeSmsOrgAsync("Status Failed");
        var deliveryId = await SeedUnresolvedDeliveryAsync(org.OrgId, "+447700900202");

        await PostStatusAsync(org, deliveryId, "undelivered", errorCode: "30006");

        var delivery = await LoadAsync(deliveryId);
        delivery.Status.Should().Be(NotificationDeliveryStatus.Failed);
        delivery.NextAttemptAtUtc.Should().BeNull("an unresolved send is terminal — it never re-enters the retry ladder");
        delivery.LastError.Should().Contain("30006", "the provider's error CODE is useful");
        delivery.LastError.Should().NotContain("+447700900202", "…but its message is not, and could carry the number (docs/36 §5)");
    }

    [Fact]
    public async Task An_interim_status_resolves_nothing()
    {
        // queued/sending/sent are not outcomes. Acting on one would swap an honest uncertainty for a wrong answer.
        var org = await ArrangeSmsOrgAsync("Status Interim");
        var deliveryId = await SeedUnresolvedDeliveryAsync(org.OrgId, "+447700900203");

        await PostStatusAsync(org, deliveryId, "sent");

        (await LoadAsync(deliveryId)).Status.Should().Be(NotificationDeliveryStatus.Unresolved);
    }

    [Fact]
    public async Task An_unsigned_callback_changes_nothing()
    {
        var org = await ArrangeSmsOrgAsync("Status Unsigned");
        var deliveryId = await SeedUnresolvedDeliveryAsync(org.OrgId, "+447700900204");

        var response = await PostStatusAsync(org, deliveryId, "delivered", signature: "not-a-valid-signature");

        response.StatusCode.Should().Be(HttpStatusCode.NoContent, "never a 4xx — that would be an oracle and a retry storm");
        (await LoadAsync(deliveryId)).Status.Should().Be(NotificationDeliveryStatus.Unresolved);
    }

    [Fact]
    public async Task One_orgs_credential_cannot_authenticate_a_callback_about_another_orgs_delivery()
    {
        // The delivery id names the org; the signature is checked with THAT org's credential. So knowing a delivery id
        // is worth nothing without the operator's own auth token.
        var orgA = await ArrangeSmsOrgAsync("Status Tenant A");
        var orgB = await ArrangeSmsOrgAsync("Status Tenant B");
        var deliveryId = await SeedUnresolvedDeliveryAsync(orgA.OrgId, "+447700900205");

        // Sign with org B's auth token, aimed at org A's delivery.
        await PostStatusAsync(orgB with { OrgId = orgA.OrgId }, deliveryId, "delivered");

        (await LoadAsync(deliveryId)).Status.Should().Be(NotificationDeliveryStatus.Unresolved,
            "org B's credential must not resolve org A's delivery");
    }

    [Fact]
    public async Task A_replayed_callback_is_a_no_op()
    {
        var org = await ArrangeSmsOrgAsync("Status Replay");
        var deliveryId = await SeedUnresolvedDeliveryAsync(org.OrgId, "+447700900206");

        await PostStatusAsync(org, deliveryId, "delivered", messageSid: "SM-first");
        await PostStatusAsync(org, deliveryId, "failed", errorCode: "30006");

        (await LoadAsync(deliveryId)).Status.Should().Be(NotificationDeliveryStatus.Delivered,
            "the first resolution stands — providers retry callbacks, and a replay must not move a resolved row");
    }

    [Fact]
    public async Task A_callback_cannot_rewrite_a_delivery_whose_outcome_we_saw_for_ourselves()
    {
        var org = await ArrangeSmsOrgAsync("Status Observed");
        var deliveryId = await SeedUnresolvedDeliveryAsync(org.OrgId, "+447700900207", markSucceeded: true);

        await PostStatusAsync(org, deliveryId, "failed", errorCode: "30006");

        (await LoadAsync(deliveryId)).Status.Should().Be(NotificationDeliveryStatus.Delivered);
    }

    [Fact]
    public async Task A_carrier_opt_out_on_the_callback_self_heals_the_suppression_ledger()
    {
        // 21610 can surface here as well as on the send response. The backstop must not depend on which path told us.
        var org = await ArrangeSmsOrgAsync("Status Optout");
        const string number = "+447700900208";
        var deliveryId = await SeedUnresolvedDeliveryAsync(org.OrgId, number);

        await PostStatusAsync(org, deliveryId, "undelivered", errorCode: "21610");

        (await ActiveSuppressionsAsync(org.OrgId, number)).Should().Be(1,
            "a carrier opt-out reported on the status callback suppresses the number just as one reported on the send does");
    }

    // ---- arrange ----

    private sealed record SmsOrg(Guid OrgId, string Token, string AuthToken);

    private async Task<SmsOrg> ArrangeSmsOrgAsync(string name)
    {
        var client = _factory.CreateClient();
        var email = $"{name.Replace(' ', '.').ToLower()}+{Guid.NewGuid():N}@wirehq.test";
        (await client.PostAsJsonAsync("/api/v1/auth/register",
            new { email, password = "Sup3rSecret!!", firstName = "Status", lastName = "Test", acceptTerms = true }))
            .StatusCode.Should().Be(HttpStatusCode.Created);

        var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password = "Sup3rSecret!!" });
        var token = (await login.Content.ReadFromJsonAsync<TokenResponse>())!.AccessToken;
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        var orgId = (await client.GetFromJsonAsync<MeResponse>("/api/v1/auth/me"))!.ActiveOrganizationId!.Value;

        var inboundToken = $"tok_{Guid.NewGuid():N}";
        var authToken = $"authtoken_{Guid.NewGuid():N}";

        using var scope = _factory.CreateBypassScope();
        var db = scope.ServiceProvider.GetRequiredService<IApplicationDbContext>();
        var protector = scope.ServiceProvider.GetRequiredService<ISecretProtector>();

        var config = NotificationChannelConfig.Create(orgId, ChannelKind.Sms);
        config.SetSmsProvider(NotificationProviderKind.Twilio, protector.Protect($"ACtest:{authToken}"), "+15550001111");
        config.MintInboundWebhookToken(
            SmsInboundToken.Hash(inboundToken),
            inboundToken[..NotificationChannelConfig.InboundWebhookTokenPrefixLength],
            DateTimeOffset.UtcNow,
            grace: null);
        db.NotificationChannelConfigs.Add(config);
        await db.SaveChangesAsync(CancellationToken.None);

        return new SmsOrg(orgId, inboundToken, authToken);
    }

    /// <summary>Seed a delivery in the state a lost send response leaves behind (or an observed success, for contrast).</summary>
    private async Task<Guid> SeedUnresolvedDeliveryAsync(Guid orgId, string number, bool markSucceeded = false)
    {
        using var scope = _factory.CreateBypassScope();
        var db = scope.ServiceProvider.GetRequiredService<IApplicationDbContext>();

        var delivery = NotificationDelivery.Create(
            orgId, Guid.CreateVersion7(), Guid.CreateVersion7(), ChannelKind.Sms,
            new[] { "notifications.sms" }, number, "WireHQ: alert", "MFA was disabled", dedupValue: null,
            nowUtc: DateTimeOffset.UtcNow);

        if (markSucceeded)
        {
            delivery.MarkSucceeded(201, DateTimeOffset.UtcNow, "SM-observed");
        }
        else
        {
            delivery.MarkUnresolved("The provider did not confirm.");
        }

        db.NotificationDeliveries.Add(delivery);
        await db.SaveChangesAsync(CancellationToken.None);
        return delivery.Id;
    }

    private async Task<HttpResponseMessage> PostStatusAsync(
        SmsOrg org, Guid deliveryId, string status, string? messageSid = null, string? errorCode = null,
        string? signature = null)
    {
        var path = $"/api/v1/sms/status/{deliveryId}";
        var form = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // A status callback carries the org's own sender in From/To — VerifyAndParse requires a From.
            ["From"] = "+15550001111",
            ["To"] = "+447700900999",
            ["MessageStatus"] = status,
            ["MessageSid"] = messageSid ?? "SM-callback",
        };
        if (errorCode is not null)
        {
            form["ErrorCode"] = errorCode;
        }

        var signed = signature ?? TwilioSignature($"{WireHqApiFactory.SmsInboundTestBaseUrl}{path}", form, org.AuthToken);

        var client = _factory.CreateClient(); // anonymous — the callback is unauthenticated
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new FormUrlEncodedContent(form) };
        request.Headers.Add("X-Twilio-Signature", signed);
        return await client.SendAsync(request);
    }

    /// <summary>Twilio's signature scheme: Base64(HMAC-SHA1(authToken, URL + concat(key+value sorted by key))).</summary>
    private static string TwilioSignature(string url, IReadOnlyDictionary<string, string> form, string authToken)
    {
        var canonical = new StringBuilder(url);
        foreach (var pair in form.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            canonical.Append(pair.Key).Append(pair.Value);
        }

#pragma warning disable CA5350 // Twilio's webhook signature is defined as HMAC-SHA1 — this mirrors it for the test.
        var mac = HMACSHA1.HashData(Encoding.UTF8.GetBytes(authToken), Encoding.UTF8.GetBytes(canonical.ToString()));
#pragma warning restore CA5350
        return Convert.ToBase64String(mac);
    }

    private async Task<NotificationDelivery> LoadAsync(Guid deliveryId)
    {
        using var scope = _factory.CreateBypassScope();
        var db = scope.ServiceProvider.GetRequiredService<IApplicationDbContext>();
        return await db.NotificationDeliveries.IgnoreQueryFilters().AsNoTracking().FirstAsync(d => d.Id == deliveryId);
    }

    private async Task<int> ActiveSuppressionsAsync(Guid orgId, string number)
    {
        using var scope = _factory.CreateBypassScope();
        var db = scope.ServiceProvider.GetRequiredService<IApplicationDbContext>();
        return await db.SmsOptOuts.IgnoreQueryFilters()
            .CountAsync(o => o.OrganizationId == orgId && o.PhoneE164 == number && o.ClearedAtUtc == null);
    }

    private sealed record TokenResponse(string AccessToken, int ExpiresIn);
    private sealed record MeResponse(Guid? ActiveOrganizationId);
}
