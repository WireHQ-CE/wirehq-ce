using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using WireHQ.Application.Abstractions.Persistence;
using WireHQ.Domain.Organizations;
using Xunit;

namespace WireHQ.Api.IntegrationTests;

/// <summary>
/// Per-edition audit visibility (docs/15 §5): a tenant reads back only as far as its plan's retention window
/// (Community 30d / Pro 1y / Enterprise unlimited). Physical data may persist longer (the sweeper's ceiling);
/// this is the customer-visible clamp on the read.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class AuditVisibilityTests(WireHqApiFactory factory)
{
    private readonly WireHqApiFactory _factory = factory;

    [Fact]
    public async Task The_audit_feed_is_clamped_to_the_edition_retention_window()
    {
        var client = _factory.CreateClient();
        await AuthenticateAsOwnerAsync(client);

        // A recent audited action + the org it belongs to.
        await CreateNetworkAsync(client, "Visible Net", "10.95.0.0/24");
        var org = await OrganizationIdAsync();

        // An audit row well outside Community's 30-day window (but inside Pro's year).
        const string oldAction = "visibility.old.event";
        await InsertOldAuditAsync(org, oldAction, DateTimeOffset.UtcNow.AddDays(-40));

        // Enterprise (the suite default) has an unlimited window — the 40-day-old row is visible.
        (await FeedActionsAsync(client)).Should().Contain(oldAction);

        // Downgrade to Community (30-day window): the old row drops out of the read, recent rows remain.
        await _factory.SetEditionAsync(org, OrganizationEdition.Community);

        var community = await FeedActionsAsync(client);
        community.Should().NotContain(oldAction, "it's older than Community's 30-day visibility window");
        community.Should().Contain("wg.network.created", "recent events stay visible");
    }

    private static async Task<List<string>> FeedActionsAsync(HttpClient client)
    {
        var feed = (await client.GetFromJsonAsync<PagedResult<AuditItem>>("/api/v1/audit-logs?pageSize=200"))!;
        return feed.Items.Select(i => i.Action).ToList();
    }

    private async Task<Guid> OrganizationIdAsync()
    {
        using var scope = _factory.CreateBypassScope();
        var db = scope.ServiceProvider.GetRequiredService<IApplicationDbContext>();
        var row = await db.AuditLogs
            .Where(a => a.Action == "wg.network.created" && a.OrganizationId != null)
            .OrderByDescending(a => a.OccurredAtUtc)
            .FirstAsync();
        return row.OrganizationId!.Value;
    }

    private static async Task InsertOldAuditAsync(Guid org, string action, DateTimeOffset occurredAt)
    {
        await using var conn = new NpgsqlConnection(Environment.GetEnvironmentVariable("ConnectionStrings__Admin")!);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "INSERT INTO audit.audit_logs (id, organization_id, actor_type, action, outcome, occurred_at_utc) " +
            "VALUES (@id, @org, 'user', @action, 'Success', @occ)";
        cmd.Parameters.AddWithValue("id", Guid.CreateVersion7());
        cmd.Parameters.AddWithValue("org", org);
        cmd.Parameters.AddWithValue("action", action);
        cmd.Parameters.AddWithValue("occ", occurredAt);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task CreateNetworkAsync(HttpClient client, string name, string cidr)
    {
        var response = await client.PostAsJsonAsync("/api/v1/wireguard/networks", new { name, cidr });
        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    private async Task AuthenticateAsOwnerAsync(HttpClient client)
    {
        var email = $"audit-vis+{Guid.NewGuid():N}@wirehq.test";
        const string password = "Sup3rSecret!!";

        var register = await client.PostAsJsonAsync("/api/v1/auth/register",
            new { email, password, firstName = "Audit", lastName = "Vis", acceptTerms = true });
        register.StatusCode.Should().Be(HttpStatusCode.Created);

        await _factory.VerifyEmailAsync(email);

        var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password });
        var token = (await login.Content.ReadFromJsonAsync<LoginResponse>())!.AccessToken;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    private sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, int Total);
    private sealed record AuditItem(Guid Id, string Action, string Outcome, DateTimeOffset OccurredAtUtc);
    private sealed record LoginResponse(string AccessToken);
}
