using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Npgsql;
using Xunit;

namespace WireHQ.Api.IntegrationTests;

/// <summary>
/// Keyset pagination + rich filters on the audit reads (docs/15 §5). Proves the cursor walks every row exactly
/// once — including the tricky case of two rows sharing a microsecond timestamp, where correctness hinges on the
/// <c>id</c> tie-break (<c>Guid.CompareTo</c>) translating to the SAME Postgres <c>uuid</c> ordering as the
/// <c>ORDER BY id DESC</c> — and that the filters (actor, free-text) narrow the feed. Runtime-verified against a
/// real Postgres because this is exactly the kind of bug that only shows up at a real page boundary.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class AuditKeysetTests(WireHqApiFactory factory)
{
    private readonly WireHqApiFactory _factory = factory;
    private const string Probe = "kt.probe";

    [Fact]
    public async Task Keyset_walks_every_row_once_across_a_timestamp_tie_and_filters_narrow_the_feed()
    {
        var client = _factory.CreateClient();
        var (email, orgId) = await RegisterVerifiedAsync(client);
        Authorize(client, await LoginAsync(client, email));

        // Four probe rows; r2 and r3 deliberately share a timestamp (the tie that exercises the id tie-break).
        var now = DateTimeOffset.UtcNow;
        var r1 = await InsertAsync(orgId, now.AddSeconds(-10), "alice@kt.test");
        var r2 = await InsertAsync(orgId, now.AddSeconds(-20), "bob@kt.test");
        var r3 = await InsertAsync(orgId, now.AddSeconds(-20), "alice@kt.test"); // tie with r2
        var r4 = await InsertAsync(orgId, now.AddSeconds(-30), "bob@kt.test");
        var expected = new[] { r1, r2, r3, r4 };

        // Walk one row at a time, following the opaque cursor, until it runs out.
        var seen = new List<(Guid Id, DateTimeOffset Occurred)>();
        string? cursor = null;
        for (var guard = 0; guard < 20; guard++)
        {
            var url = $"/api/v1/audit-logs?action={Probe}&pageSize=1" + (cursor is null ? "" : $"&cursor={cursor}");
            var page = (await client.GetFromJsonAsync<Cursored<AuditItem>>(url))!;
            if (page.Items.Count == 0)
            {
                break;
            }

            page.Items.Should().ContainSingle();
            seen.Add((page.Items[0].Id, page.Items[0].OccurredAtUtc));
            cursor = page.NextCursor;
            if (cursor is null)
            {
                break;
            }
        }

        seen.Select(s => s.Id).Should().BeEquivalentTo(expected, "every probe row is returned exactly once, no dup/skip across the tie");
        seen.Select(s => s.Occurred).Should().BeInDescendingOrder("the feed is ordered newest-first");
        seen.First().Id.Should().Be(r1);
        seen.Last().Id.Should().Be(r4);

        // Filters: actor and free-text narrow the feed (one page big enough to hold the matches).
        var byActor = (await client.GetFromJsonAsync<Cursored<AuditItem>>($"/api/v1/audit-logs?action={Probe}&actor=alice&pageSize=50"))!;
        byActor.Items.Select(i => i.Id).Should().BeEquivalentTo(new[] { r1, r3 });

        var byText = (await client.GetFromJsonAsync<Cursored<AuditItem>>($"/api/v1/audit-logs?action={Probe}&q=bob&pageSize=50"))!;
        byText.Items.Select(i => i.Id).Should().BeEquivalentTo(new[] { r2, r4 });
    }

    // NB: the cross-tenant `/platform/audit` keyset test lives in PlatformAuditKeysetTests.cs —
    // the SaaS-only file the Community Edition strip removes.

    private async Task<Guid> InsertAsync(Guid org, DateTimeOffset occurredAt, string actorEmail)
    {
        var id = Guid.CreateVersion7();
        await using var conn = new NpgsqlConnection(Environment.GetEnvironmentVariable("ConnectionStrings__Admin")!);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "INSERT INTO audit.audit_logs (id, organization_id, actor_type, actor_email, action, outcome, occurred_at_utc) " +
            "VALUES (@id, @org, 'user', @actor, @action, 'Success', @occ)";
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("org", org);
        cmd.Parameters.AddWithValue("actor", actorEmail);
        cmd.Parameters.AddWithValue("action", Probe);
        cmd.Parameters.AddWithValue("occ", occurredAt);
        await cmd.ExecuteNonQueryAsync();
        return id;
    }

    private async Task<(string Email, Guid OrganizationId)> RegisterVerifiedAsync(HttpClient client, string name = "Keyset Owner")
    {
        var email = $"{name.Replace(' ', '.').ToLower()}+{Guid.NewGuid():N}@wirehq.test";
        var response = await client.PostAsJsonAsync("/api/v1/auth/register",
            new { email, password = "Sup3rSecret!!", firstName = name, lastName = "Test", acceptTerms = true });
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = (await response.Content.ReadFromJsonAsync<RegisterResponse>())!;
        await _factory.VerifyEmailAsync(email);
        return (email, body.OrganizationId);
    }

    private static async Task<string> LoginAsync(HttpClient client, string email)
    {
        var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password = "Sup3rSecret!!" });
        login.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await login.Content.ReadFromJsonAsync<TokenResponse>())!.AccessToken;
    }

    private static void Authorize(HttpClient client, string token) =>
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

    private sealed record RegisterResponse(Guid UserId, Guid OrganizationId, string OrganizationSlug);
    private sealed record TokenResponse(string AccessToken, int ExpiresIn);
    private sealed record Cursored<T>(IReadOnlyList<T> Items, string? NextCursor);
    private sealed record AuditItem(Guid Id, string Action, string? ActorEmail, DateTimeOffset OccurredAtUtc);
}
