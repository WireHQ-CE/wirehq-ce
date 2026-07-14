using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Xunit;

namespace WireHQ.Api.IntegrationTests;

/// <summary>
/// The fleet dashboard query: a cross-instance overview with a fleet-wide summary, spanning the WireGuard
/// instances + their deployment targets + peer connectivity. (docs/12 §13 Phase 3)
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class FleetTests(WireHqApiFactory factory)
{
    private readonly WireHqApiFactory _factory = factory;

    [Fact]
    public async Task Fleet_aggregates_instances_targets_and_peers_with_a_summary()
    {
        var client = _factory.CreateClient();
        await AuthenticateAsOwnerAsync(client);

        var networkId = await CreatedId(client.PostAsJsonAsync("/api/v1/wireguard/networks",
            new { name = "FleetNet", cidr = "10.94.0.0/24" }));

        // Instance A stays Local (config-only); instance B binds to an SSH target.
        var instanceA = await CreatedId(client.PostAsJsonAsync("/api/v1/wireguard/instances",
            new { networkId, name = "Fleet-A", listenPort = 51910, interfaceAddress = "10.94.0.1/24" }));
        var instanceB = await CreatedId(client.PostAsJsonAsync("/api/v1/wireguard/instances",
            new { networkId, name = "Fleet-B", listenPort = 51911, interfaceAddress = "10.94.0.2/24" }));

        (await client.PostAsJsonAsync($"/api/v1/wireguard/instances/{instanceA}/peers",
            new { name = "Dev", deviceType = "Laptop" })).StatusCode.Should().Be(HttpStatusCode.Created);

        var sshTargetId = await CreatedId(client.PostAsJsonAsync("/api/v1/wireguard/ssh-targets",
            new { name = "Fleet host", host = "10.0.0.9", port = 22, username = "deploy", authKind = "Password", credential = "pw" }));
        (await client.PutAsJsonAsync($"/api/v1/wireguard/instances/{instanceB}/target",
            new { kind = "Ssh", sshTargetId })).StatusCode.Should().Be(HttpStatusCode.NoContent);

        var fleet = (await client.GetFromJsonAsync<FleetDto>("/api/v1/wireguard/fleet"))!;

        fleet.Summary.TotalInstances.Should().Be(2);
        fleet.Summary.LocalTargets.Should().Be(1);
        fleet.Summary.SshTargets.Should().Be(1);
        fleet.Summary.PeersTotal.Should().Be(1);

        var a = fleet.Instances.Single(i => i.InstanceId == instanceA);
        a.TargetKind.Should().Be("Local");
        a.NetworkName.Should().Be("FleetNet");
        a.PeersTotal.Should().Be(1);

        var b = fleet.Instances.Single(i => i.InstanceId == instanceB);
        b.TargetKind.Should().Be("Ssh");
        b.TargetName.Should().Be("Fleet host");
    }

    private async Task AuthenticateAsOwnerAsync(HttpClient client)
    {
        var email = $"fleet+{Guid.NewGuid():N}@wirehq.test";
        const string password = "Sup3rSecret!!";
        await client.PostAsJsonAsync("/api/v1/auth/register",
            new { email, password, firstName = "Fleet", lastName = "Owner", acceptTerms = true });
        await _factory.VerifyEmailAsync(email);
        var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password });
        var token = (await login.Content.ReadFromJsonAsync<LoginResponse>())!.AccessToken;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    private static async Task<Guid> CreatedId(Task<HttpResponseMessage> request)
    {
        var response = await request;
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<IdResponse>())!.Id;
    }

    private sealed record IdResponse(Guid Id);
    private sealed record LoginResponse(string AccessToken);
    private sealed record FleetDto(FleetSummary Summary, List<FleetInstance> Instances);
    private sealed record FleetSummary(int TotalInstances, int LocalTargets, int SshTargets, int AgentTargets, int PeersTotal);
    private sealed record FleetInstance(Guid InstanceId, string Name, string? NetworkName, string TargetKind, string? TargetName, string Status, bool HasDrift, int PeersTotal);
}
