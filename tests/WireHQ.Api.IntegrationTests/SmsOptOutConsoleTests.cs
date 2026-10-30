using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WireHQ.Application.Abstractions;
using WireHQ.Application.Abstractions.Persistence;
using WireHQ.Domain.Notifications;
using WireHQ.Domain.Organizations;
using Xunit;

namespace WireHQ.Api.IntegrationTests;

/// <summary>
/// The SMS opt-out console (docs/36 §4.3, Wave 4 W3) against a real Postgres. Four properties carry the feature, and
/// each fails silently if it regresses:
/// <list type="number">
/// <item><b>Numbers are masked.</b> The console proves a suppression exists; it is not a directory of the org's numbers.</item>
/// <item><b>Search is EXACT.</b> A prefix must not match. With a masked display, a partial match would be a
/// number-harvesting oracle — page a prefix, unmask the ledger four digits at a time.</item>
/// <item><b>The ledger is org-scoped.</b> Org A's opt-outs are invisible to org B.</item>
/// <item><b>There is no un-suppress.</b> Recording is idempotent and has no inverse: a suppression is lifted only by the
/// subscriber's own START through the signed inbound webhook.</item>
/// </list>
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class SmsOptOutConsoleTests : IDisposable
{
    private const string Password = "Sup3rSecret-OptOut!1";
    private const string OptOutsUrl = "/api/v1/notifications/sms/opt-outs";

    private readonly WireHqApiFactory _factory;
    private readonly Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> _host;

    public SmsOptOutConsoleTests(WireHqApiFactory factory)
    {
        _factory = factory;
        _host = factory.WithWebHostBuilder(_ => { });
    }

    public void Dispose() => _host.Dispose();

    [Fact]
    public async Task Lists_suppressions_with_the_number_masked()
    {
        var (client, orgId) = await RegisterOwnerAsync();
        var phone = UniquePhone();
        await SuppressDirectlyAsync(orgId, phone, SmsOptOutSource.StopKeyword, "STOP");

        var rows = await client.GetFromJsonAsync<List<OptOutDto>>(OptOutsUrl);

        var row = rows.Should().ContainSingle().Subject;
        row.PhoneMasked.Should().NotBe(phone, "the console must never return the plaintext number");
        row.PhoneMasked.Should().EndWith(phone[^4..], "the last 4 are kept so an operator can tell rows apart");
        row.PhoneMasked.Should().StartWith("•");
        row.IsActive.Should().BeTrue();
        row.Source.Should().Be("StopKeyword", "the enum crosses an API boundary as a string, not an int");
    }

    [Fact]
    public async Task Search_matches_the_exact_number_only_never_a_prefix()
    {
        var (client, orgId) = await RegisterOwnerAsync();
        var phone = UniquePhone();
        await SuppressDirectlyAsync(orgId, phone, SmsOptOutSource.StopKeyword, "STOP");

        // The exact number finds it...
        var exact = await client.GetFromJsonAsync<List<OptOutDto>>($"{OptOutsUrl}?phone={Uri.EscapeDataString(phone)}");
        exact.Should().ContainSingle();

        // ...but a strict prefix of it — itself a well-formed E.164, so it reaches the query rather than being rejected
        // as malformed — must find NOTHING. A LIKE/StartsWith here would be a harvesting oracle over the masked display.
        var prefix = phone[..^3];
        var partial = await client.GetFromJsonAsync<List<OptOutDto>>($"{OptOutsUrl}?phone={Uri.EscapeDataString(prefix)}");
        partial.Should().BeEmpty("search is an exact lookup, so it can only confirm a number the searcher already has");
    }

    [Fact]
    public async Task A_malformed_search_is_a_validation_error_not_a_silent_empty_list()
    {
        var (client, _) = await RegisterOwnerAsync();

        // Bare national numbers are rejected by PhoneNumber.Create (no resolvable country). Telling the operator their
        // input was malformed is not an oracle — the ledger is their own org's, and they already hold notifications.manage.
        var response = await client.GetAsync($"{OptOutsUrl}?phone=07700900123");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task One_orgs_opt_outs_are_invisible_to_another()
    {
        var (_, orgA) = await RegisterOwnerAsync();
        var (clientB, _) = await RegisterOwnerAsync();
        var phone = UniquePhone();
        await SuppressDirectlyAsync(orgA, phone, SmsOptOutSource.StopKeyword, "STOP");

        var seenByB = await clientB.GetFromJsonAsync<List<OptOutDto>>($"{OptOutsUrl}?phone={Uri.EscapeDataString(phone)}");

        seenByB.Should().BeEmpty("suppression is per (org, number) — org A's ledger is not org B's");
    }

    [Fact]
    public async Task An_operator_can_record_an_opt_out_that_arrived_off_channel()
    {
        var (client, orgId) = await RegisterOwnerAsync();
        var phone = UniquePhone();

        var response = await client.PostAsJsonAsync(OptOutsUrl, new { phone, reason = "phoned support, ticket 1234" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = (await response.Content.ReadFromJsonAsync<RecordResultDto>())!;
        result.Appended.Should().BeTrue();
        result.PhoneMasked.Should().NotBe(phone);

        var stored = await ActiveRowsAsync(orgId, phone);
        var row = stored.Should().ContainSingle().Subject;
        row.Source.Should().Be(SmsOptOutSource.OperatorRequest);
        row.Reason.Should().Be("phoned support, ticket 1234", "the reason column finally has a writer (W3)");
        row.Keyword.Should().BeNull("no keyword arrived — nobody texted anything");
    }

    [Fact]
    public async Task Recording_the_same_number_twice_is_a_no_op_not_a_second_active_row()
    {
        var (client, orgId) = await RegisterOwnerAsync();
        var phone = UniquePhone();

        (await client.PostAsJsonAsync(OptOutsUrl, new { phone, reason = (string?)null })).EnsureSuccessStatusCode();
        var second = await client.PostAsJsonAsync(OptOutsUrl, new { phone, reason = "asked again" });

        second.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = (await second.Content.ReadFromJsonAsync<RecordResultDto>())!;
        result.Appended.Should().BeFalse("the number was already suppressed — the operator's intent already holds");

        (await ActiveRowsAsync(orgId, phone)).Should().ContainSingle("a duplicate active row would only make the ledger harder to read");
    }

    [Fact]
    public async Task A_number_entered_with_separators_is_canonicalised_before_it_is_stored()
    {
        var (client, orgId) = await RegisterOwnerAsync();
        var phone = UniquePhone();
        var typed = $"{phone[..3]} {phone[3..6]} {phone[6..]}";

        (await client.PostAsJsonAsync(OptOutsUrl, new { phone = typed, reason = (string?)null })).EnsureSuccessStatusCode();

        // Stored in canonical E.164 — the same key the send path matches on. Anything else is a suppression the sender
        // would never find, i.e. a recorded opt-out that does not actually stop a message.
        (await ActiveRowsAsync(orgId, phone)).Should().ContainSingle();
    }

    [Fact]
    public async Task There_is_no_endpoint_that_lifts_a_suppression()
    {
        var (client, orgId) = await RegisterOwnerAsync();
        var phone = UniquePhone();
        await SuppressDirectlyAsync(orgId, phone, SmsOptOutSource.StopKeyword, "STOP");
        var rowId = (await ActiveRowsAsync(orgId, phone)).Single().Id;

        // Deliberately absent, not merely unimplemented: a control whose only purpose is to resume texting someone who
        // asked you to stop. Only the subscriber's own START (via the signed inbound webhook) clears a row. If this test
        // ever fails, the fix is not to update the test.
        foreach (var attempt in new[]
                 {
                     await client.DeleteAsync($"{OptOutsUrl}/{rowId}"),
                     await client.PostAsJsonAsync($"{OptOutsUrl}/{rowId}/clear", new { }),
                 })
        {
            attempt.StatusCode.Should().BeOneOf(
                HttpStatusCode.NotFound, HttpStatusCode.MethodNotAllowed, HttpStatusCode.Forbidden);
        }

        (await ActiveRowsAsync(orgId, phone)).Should().ContainSingle("the suppression is still standing");
    }

    // ---- helpers ----

    // E.164 with a UK test range prefix; the last digits vary so tests never collide in a shared database.
    private static string UniquePhone() => "+44770099" + Random.Shared.Next(100000, 999999).ToString()[..4];

    private IServiceScope BypassScope()
    {
        var scope = _host.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ISettableTenantContext>().SetBypass();
        return scope;
    }

    private async Task SuppressDirectlyAsync(Guid organizationId, string phone, SmsOptOutSource source, string? keyword)
    {
        using var scope = BypassScope();
        var db = scope.ServiceProvider.GetRequiredService<IApplicationDbContext>();
        var now = scope.ServiceProvider.GetRequiredService<IDateTimeProvider>().UtcNow;
        db.SmsOptOuts.Add(SmsOptOut.Suppress(organizationId, phone, null, source, keyword, reason: null, now));
        await db.SaveChangesAsync(default);
    }

    private async Task<List<SmsOptOut>> ActiveRowsAsync(Guid organizationId, string phone)
    {
        using var scope = BypassScope();
        var db = scope.ServiceProvider.GetRequiredService<IApplicationDbContext>();
        return await db.SmsOptOuts
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(o => o.OrganizationId == organizationId && o.PhoneE164 == phone && o.ClearedAtUtc == null)
            .ToListAsync();
    }

    private async Task<(HttpClient Client, Guid OrganizationId)> RegisterOwnerAsync()
    {
        var client = _host.CreateClient();
        var email = $"optout+{Guid.NewGuid():N}@wirehq.test";
        (await client.PostAsJsonAsync("/api/v1/auth/register",
            new { email, password = Password, firstName = "OptOut", lastName = "Owner", acceptTerms = true })).EnsureSuccessStatusCode();
        await _factory.VerifyEmailAsync(email);

        var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password = Password });
        login.EnsureSuccessStatusCode();
        var token = (await login.Content.ReadFromJsonAsync<LoginDto>())!.AccessToken;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var me = (await client.GetFromJsonAsync<MeDto>("/api/v1/auth/me"))!;
        var orgId = me.ActiveOrganizationId!.Value;
        await _factory.SetEditionAsync(orgId, OrganizationEdition.Enterprise);
        return (client, orgId);
    }

    private sealed record OptOutDto(
        Guid Id, string PhoneMasked, string? Sender, string Source, string? Keyword, string? ClearedKeyword,
        string? Reason, DateTimeOffset SuppressedAtUtc, DateTimeOffset? ClearedAtUtc, bool IsActive);

    private sealed record RecordResultDto(bool Appended, string PhoneMasked);

    private sealed record LoginDto(string AccessToken);

    private sealed record MeDto(Guid? ActiveOrganizationId);
}
