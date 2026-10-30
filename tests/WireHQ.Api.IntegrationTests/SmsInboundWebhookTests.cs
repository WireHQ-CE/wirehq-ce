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
/// The inbound SMS STOP/HELP webhook (docs/36 §5, Slice D). Proves the security spine end-to-end against a real
/// Postgres: a correctly Twilio-signed STOP appends an active suppression row for the resolved org; a bad signature is
/// a silent 204 drop with NO ledger write (fail-closed, no oracle); START clears the suppression (a state transition);
/// and — the load-bearing tenant-isolation property — an opt-out routed to org A's token can never suppress the number
/// in org B. The org is resolved from the inbound token (hashed), and the suppression is written under a tenant bypass
/// with an EXPLICITLY-resolved org.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class SmsInboundWebhookTests(WireHqApiFactory factory)
{
    private readonly WireHqApiFactory _factory = factory;

    [Fact]
    public async Task A_signed_stop_suppresses_the_number_then_start_clears_it()
    {
        var org = await ArrangeSmsOrgAsync("Stop Flow");
        const string number = "+447700900123";

        // A correctly-signed STOP → 204 + an active suppression row for (org, number).
        (await PostAsync(org, number, "STOP")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await ActiveSuppressionsAsync(org.OrgId, number)).Should().Be(1, "a signed STOP suppresses the number");

        // A redelivered STOP is idempotent (append-only, no duplicate active row).
        (await PostAsync(org, number, "STOP")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await ActiveSuppressionsAsync(org.OrgId, number)).Should().Be(1, "a repeat STOP does not append a second active row");

        // START clears it (a state transition — the row is kept but no longer active).
        (await PostAsync(org, number, "START")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await ActiveSuppressionsAsync(org.OrgId, number)).Should().Be(0, "START clears the suppression");
        (await TotalRowsAsync(org.OrgId, number)).Should().Be(1, "the ledger is append-only — START clears, never deletes");
    }

    [Fact]
    public async Task A_bad_signature_is_dropped_with_no_ledger_write()
    {
        var org = await ArrangeSmsOrgAsync("Bad Sig");
        const string number = "+447700900456";

        var response = await PostAsync(org, number, "STOP", signature: "not-a-valid-signature");

        response.StatusCode.Should().Be(HttpStatusCode.NoContent, "the webhook is fail-closed + no-oracle — never a 4xx");
        (await ActiveSuppressionsAsync(org.OrgId, number)).Should().Be(0, "an unverifiable request must not touch the ledger");
    }

    [Fact]
    public async Task An_opt_out_in_one_org_does_not_suppress_the_number_in_another_org()
    {
        var orgA = await ArrangeSmsOrgAsync("Tenant A");
        var orgB = await ArrangeSmsOrgAsync("Tenant B");
        const string number = "+447700900789";

        (await PostAsync(orgA, number, "STOP")).StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await ActiveSuppressionsAsync(orgA.OrgId, number)).Should().Be(1, "org A opted the number out");
        (await ActiveSuppressionsAsync(orgB.OrgId, number)).Should().Be(0, "org B is a different sender — its consent is untouched");
    }

    // ---- arrange ----

    private sealed record SmsOrg(Guid OrgId, string Token, string AuthToken);

    /// <summary>Register an org and seed a configured SMS channel with a known inbound token + Twilio credential.</summary>
    private async Task<SmsOrg> ArrangeSmsOrgAsync(string name)
    {
        var client = _factory.CreateClient();
        var email = $"{name.Replace(' ', '.').ToLower()}+{Guid.NewGuid():N}@wirehq.test";
        (await client.PostAsJsonAsync("/api/v1/auth/register",
            new { email, password = "Sup3rSecret!!", firstName = name, lastName = "Test", acceptTerms = true }))
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
        // grace: null ⇒ mint outright (no prior token to overlap with).
        config.MintInboundWebhookToken(
            SmsInboundToken.Hash(inboundToken),
            inboundToken[..NotificationChannelConfig.InboundWebhookTokenPrefixLength],
            DateTimeOffset.UtcNow,
            grace: null);
        db.NotificationChannelConfigs.Add(config);
        await db.SaveChangesAsync(CancellationToken.None);

        return new SmsOrg(orgId, inboundToken, authToken);
    }

    private async Task<HttpResponseMessage> PostAsync(SmsOrg org, string from, string body, string? signature = null)
    {
        var path = $"/api/v1/sms/inbound/{org.Token}";
        var form = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["From"] = from,
            ["To"] = "+15550001111",
            ["Body"] = body,
        };

        var signed = signature ?? TwilioSignature($"{WireHqApiFactory.SmsInboundTestBaseUrl}{path}", form, org.AuthToken);

        var client = _factory.CreateClient(); // anonymous — the webhook is unauthenticated
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

    private async Task<int> ActiveSuppressionsAsync(Guid orgId, string number)
    {
        using var scope = _factory.CreateBypassScope();
        var db = scope.ServiceProvider.GetRequiredService<IApplicationDbContext>();
        return await db.SmsOptOuts.IgnoreQueryFilters()
            .CountAsync(o => o.OrganizationId == orgId && o.PhoneE164 == number && o.ClearedAtUtc == null);
    }

    private async Task<int> TotalRowsAsync(Guid orgId, string number)
    {
        using var scope = _factory.CreateBypassScope();
        var db = scope.ServiceProvider.GetRequiredService<IApplicationDbContext>();
        return await db.SmsOptOuts.IgnoreQueryFilters().CountAsync(o => o.OrganizationId == orgId && o.PhoneE164 == number);
    }

    private sealed record TokenResponse(string AccessToken, int ExpiresIn);
    private sealed record MeResponse(Guid? ActiveOrganizationId);
}
