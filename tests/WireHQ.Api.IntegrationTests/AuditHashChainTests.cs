using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using WireHQ.Application.Abstractions.Persistence;
using WireHQ.Domain.Auditing;
using Xunit;

namespace WireHQ.Api.IntegrationTests;

/// <summary>
/// End-to-end tamper-evidence (ADR-031, docs/15 §5): the <c>AuditChainInterceptor</c> links every new audit
/// row into its tenant's hash chain under a per-tenant advisory lock, and <c>GET /audit-logs/verify</c>
/// re-derives the chain. Proves a real chain forms over real actions (genesis + linkage), verifies intact,
/// and that mutating a row through the owner connection (the only role that can — the app role is append-only)
/// is detected.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class AuditHashChainTests(WireHqApiFactory factory)
{
    private readonly WireHqApiFactory _factory = factory;

    [Fact]
    public async Task New_audit_rows_are_chained_and_the_chain_verifies_intact()
    {
        var client = _factory.CreateClient();
        await AuthenticateAsOwnerAsync(client);

        await CreateNetworkAsync(client, "Chain A", "10.71.0.0/24");
        await CreateNetworkAsync(client, "Chain B", "10.72.0.0/24");
        await CreateNetworkAsync(client, "Chain C", "10.73.0.0/24");

        var verify = await client.GetFromJsonAsync<VerifyResponse>("/api/v1/audit-logs/verify");
        verify!.IsIntact.Should().BeTrue();
        verify.VerifiedCount.Should().BeGreaterThanOrEqualTo(3);

        // Inspect the raw chain for the tenant: every row hashed, genesis has no predecessor, each link joins.
        var rows = await LoadChainAsync(await OrganizationIdAsync());
        rows.Should().HaveCountGreaterThanOrEqualTo(3);
        rows[0].PrevHash.Should().BeNull("the genesis entry has no predecessor");
        foreach (var row in rows)
        {
            row.EntryHash.Should().NotBeNull("every chained row carries its hash");
        }

        for (var i = 1; i < rows.Count; i++)
        {
            rows[i].PrevHash.Should().Equal(rows[i - 1].EntryHash, "each entry links to the one before it");
        }
    }

    [Fact]
    public async Task Tampering_with_a_committed_row_is_detected_by_verification()
    {
        var client = _factory.CreateClient();
        await AuthenticateAsOwnerAsync(client);

        await CreateNetworkAsync(client, "Tamper A", "10.81.0.0/24");
        await CreateNetworkAsync(client, "Tamper B", "10.82.0.0/24");

        (await client.GetFromJsonAsync<VerifyResponse>("/api/v1/audit-logs/verify"))!.IsIntact.Should().BeTrue();

        // Mutate the genesis row's content through the owner connection — the app role is blocked from UPDATE,
        // so this is exactly the "someone with DB access altered history" scenario the chain exists to expose.
        var rows = await LoadChainAsync(await OrganizationIdAsync());
        var genesis = rows[0];
        await TamperActionAsync(genesis.Id, "tampered.action");

        var verify = await client.GetFromJsonAsync<VerifyResponse>("/api/v1/audit-logs/verify");
        verify!.IsIntact.Should().BeFalse();
        verify.BrokenAtEntryId.Should().Be(genesis.Id);
    }

    private async Task CreateNetworkAsync(HttpClient client, string name, string cidr)
    {
        var response = await client.PostAsJsonAsync("/api/v1/wireguard/networks", new { name, cidr });
        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    private async Task<Guid> OrganizationIdAsync()
    {
        using var scope = _factory.CreateBypassScope();
        var db = scope.ServiceProvider.GetRequiredService<IApplicationDbContext>();
        // The most recent network-create audit row belongs to the org under test (the suite serialises tests).
        var row = await db.AuditLogs
            .Where(a => a.Action == "wg.network.created" && a.OrganizationId != null)
            .OrderByDescending(a => a.OccurredAtUtc)
            .FirstAsync();
        return row.OrganizationId!.Value;
    }

    private async Task<List<AuditLog>> LoadChainAsync(Guid organizationId)
    {
        using var scope = _factory.CreateBypassScope();
        var db = scope.ServiceProvider.GetRequiredService<IApplicationDbContext>();
        return await db.AuditLogs
            .Where(a => a.OrganizationId == organizationId)
            .OrderBy(a => a.OccurredAtUtc)
            .ThenBy(a => a.Id)
            .ToListAsync();
    }

    private static async Task TamperActionAsync(Guid entryId, string newAction)
    {
        // The owner/superuser connection (ADR-027) — the only role permitted to UPDATE the append-only table.
        var adminConnectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Admin")!;
        await using var connection = new NpgsqlConnection(adminConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE audit.audit_logs SET action = @action WHERE id = @id";
        command.Parameters.AddWithValue("action", newAction);
        command.Parameters.AddWithValue("id", entryId);
        var affected = await command.ExecuteNonQueryAsync();
        affected.Should().Be(1);
    }

    private async Task AuthenticateAsOwnerAsync(HttpClient client)
    {
        var email = $"audit-chain+{Guid.NewGuid():N}@wirehq.test";
        const string password = "Sup3rSecret!!";

        var register = await client.PostAsJsonAsync("/api/v1/auth/register",
            new { email, password, firstName = "Audit", lastName = "Chain", acceptTerms = true });
        register.StatusCode.Should().Be(HttpStatusCode.Created);

        await _factory.VerifyEmailAsync(email); // creating networks is gated by verified email

        var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password });
        var token = (await login.Content.ReadFromJsonAsync<LoginResponse>())!.AccessToken;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    private sealed record VerifyResponse(bool IsIntact, int VerifiedCount, Guid? BrokenAtEntryId, string? Detail);
    private sealed record LoginResponse(string AccessToken);
}
