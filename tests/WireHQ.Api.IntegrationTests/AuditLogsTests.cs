using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Xunit;

namespace WireHQ.Api.IntegrationTests;

/// <summary>
/// The customer audit read (docs/15 §5): an audited action records the actor's email snapshotted at write
/// time (so it survives user deletion) and the correlation reference (ADR-030), and both are surfaced on the
/// tenant audit feed — tying an audit entry to its logs + trace.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class AuditLogsTests(WireHqApiFactory factory)
{
    private readonly WireHqApiFactory _factory = factory;

    [Fact]
    public async Task An_audited_action_records_the_actor_email_and_correlation_and_surfaces_them_on_the_feed()
    {
        var client = _factory.CreateClient();
        var email = await AuthenticateAsOwnerAsync(client);

        // A mutating, audited action (wg.network.created).
        var create = await client.PostAsJsonAsync("/api/v1/wireguard/networks",
            new { name = "Audit Net", cidr = "10.55.0.0/24", dns = new[] { "1.1.1.1" } });
        create.StatusCode.Should().Be(HttpStatusCode.Created);
        var correlationId = create.Headers.GetValues("X-Correlation-Id").Single();

        var feed = (await client.GetFromJsonAsync<PagedResult<AuditItem>>(
            "/api/v1/audit-logs?action=wg.network.created"))!;

        var entry = feed.Items.Should().ContainSingle().Subject;
        entry.ActorEmail.Should().Be(email);          // snapshotted at write time — survives user deletion
        entry.ActorType.Should().Be("user");
        entry.CorrelationId.Should().Be(correlationId); // ties this audit row to its trace + logs (ADR-030)
        entry.Outcome.Should().Be("Success");
    }

    private async Task<string> AuthenticateAsOwnerAsync(HttpClient client)
    {
        var email = $"audit-owner+{Guid.NewGuid():N}@wirehq.test";
        const string password = "Sup3rSecret!!";

        var register = await client.PostAsJsonAsync("/api/v1/auth/register",
            new { email, password, firstName = "Audit", lastName = "Owner", acceptTerms = true });
        register.StatusCode.Should().Be(HttpStatusCode.Created);

        await _factory.VerifyEmailAsync(email); // creating networks is gated by verified email

        var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password });
        var token = (await login.Content.ReadFromJsonAsync<LoginResponse>())!.AccessToken;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return email;
    }

    private sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, int Total);
    private sealed record AuditItem(Guid Id, Guid? ActorUserId, string? ActorEmail, string ActorType, string Action,
        string Outcome, string? TargetType, string? TargetId, string? IpAddress, string? CorrelationId, DateTimeOffset OccurredAtUtc);
    private sealed record LoginResponse(string AccessToken);
}
