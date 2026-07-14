using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WireHQ.Application.Abstractions.Persistence;
using Xunit;

namespace WireHQ.Api.IntegrationTests;

/// <summary>
/// Declarative auditing (docs/15 §5, ADR-031): a command marked <c>IAuditableRequest</c> is audited by the
/// pipeline's <c>AuditBehavior</c> — exactly once (no double-audit), atomically with the action, with the
/// target and a before/after diff derived from the EF ChangeTracker. The WireGuard network create/update/
/// delete use cases are the migrated showcase: they no longer call <c>IAuditWriter</c> by hand.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class DeclarativeAuditTests(WireHqApiFactory factory)
{
    private readonly WireHqApiFactory _factory = factory;

    [Fact]
    public async Task Create_update_delete_each_audit_exactly_once_with_an_ef_diff()
    {
        var client = _factory.CreateClient();
        await AuthenticateAsOwnerAsync(client);

        // CREATE → a single "Added" entry whose diff carries the new business values.
        var create = await client.PostAsJsonAsync("/api/v1/wireguard/networks",
            new { name = "Diff Net", cidr = "10.60.0.0/24", dns = new[] { "1.1.1.1", "9.9.9.9" } });
        create.StatusCode.Should().Be(HttpStatusCode.Created);
        var network = (await create.Content.ReadFromJsonAsync<NetworkResponse>())!;

        var created = await SingleAuditAsync("wg.network.created", network.Id.ToString());
        created.TargetType.Should().Be("WireGuardNetwork"); // derived from the one changed entity, not hand-typed
        var createdChange = SingleChange(created.Changes);
        createdChange.GetProperty("operation").GetString().Should().Be("Added");
        createdChange.GetProperty("entity").GetString().Should().Be("WireGuardNetwork");
        var createdProps = createdChange.GetProperty("changes");
        createdProps.GetProperty("Name").GetProperty("new").GetString().Should().Be("Diff Net");
        createdProps.GetProperty("Cidr").GetProperty("new").GetString().Should().Be(network.Cidr);
        createdProps.GetProperty("Dns").GetProperty("new").GetArrayLength().Should().Be(2);

        // UPDATE (rename only) → a single "Modified" entry diffing just the field that changed.
        var update = await client.PatchAsJsonAsync($"/api/v1/wireguard/networks/{network.Id}",
            new { name = "Renamed Net" });
        update.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var updated = await SingleAuditAsync("wg.network.updated", network.Id.ToString());
        var updatedChange = SingleChange(updated.Changes);
        updatedChange.GetProperty("operation").GetString().Should().Be("Modified");
        var name = updatedChange.GetProperty("changes").GetProperty("Name");
        name.GetProperty("old").GetString().Should().Be("Diff Net");
        name.GetProperty("new").GetString().Should().Be("Renamed Net");
        // An unchanged field is not in the diff.
        updatedChange.GetProperty("changes").TryGetProperty("Cidr", out _).Should().BeFalse();

        // DELETE → a single "Deleted" entry.
        var delete = await client.DeleteAsync($"/api/v1/wireguard/networks/{network.Id}");
        delete.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var deleted = await SingleAuditAsync("wg.network.deleted", network.Id.ToString());
        SingleChange(deleted.Changes).GetProperty("operation").GetString().Should().Be("Deleted");
    }

    /// <summary>Loads the (expected single) audit row for an action+target, asserting it was written exactly once.</summary>
    private async Task<(string? TargetType, string? Changes)> SingleAuditAsync(string action, string targetId)
    {
        using var scope = _factory.CreateBypassScope();
        var db = scope.ServiceProvider.GetRequiredService<IApplicationDbContext>();
        var rows = await db.AuditLogs.IgnoreQueryFilters()
            .Where(a => a.Action == action && a.TargetId == targetId)
            .ToListAsync();
        rows.Should().ContainSingle($"'{action}' must be audited exactly once — no double-audit");
        return (rows[0].TargetType, rows[0].Changes);
    }

    private static JsonElement SingleChange(string? changesJson)
    {
        changesJson.Should().NotBeNull("the AuditBehavior captures an EF diff into Changes");
        using var doc = JsonDocument.Parse(changesJson!);
        doc.RootElement.ValueKind.Should().Be(JsonValueKind.Array);
        doc.RootElement.GetArrayLength().Should().Be(1);
        return doc.RootElement[0].Clone();
    }

    private async Task AuthenticateAsOwnerAsync(HttpClient client)
    {
        var email = $"decl-audit+{Guid.NewGuid():N}@wirehq.test";
        const string password = "Sup3rSecret!!";

        var register = await client.PostAsJsonAsync("/api/v1/auth/register",
            new { email, password, firstName = "Decl", lastName = "Audit", acceptTerms = true });
        register.StatusCode.Should().Be(HttpStatusCode.Created);

        await _factory.VerifyEmailAsync(email); // creating networks is gated by verified email

        var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password });
        var token = (await login.Content.ReadFromJsonAsync<LoginResponse>())!.AccessToken;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    private sealed record NetworkResponse(Guid Id, string Name, string Cidr);
    private sealed record LoginResponse(string AccessToken);
}
