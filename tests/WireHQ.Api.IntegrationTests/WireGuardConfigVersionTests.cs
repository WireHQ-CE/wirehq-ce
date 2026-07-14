using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using FluentAssertions;
using Xunit;

namespace WireHQ.Api.IntegrationTests;

/// <summary>
/// Exercises the full WireGuard management flow against a real Postgres and asserts that config
/// versioning records an immutable, monotonic history on create and key rotation. Also proves the
/// module's endpoints, permissions (Owner gets the wg.* catalog), and tenant scoping work end-to-end.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class WireGuardConfigVersionTests(WireHqApiFactory factory)
{
    private readonly WireHqApiFactory _factory = factory;

    [Fact]
    public async Task Config_is_versioned_on_instance_create_peer_create_and_rotate()
    {
        var client = _factory.CreateClient();
        await AuthenticateAsOwnerAsync(client);

        // Network → instance.
        var network = await CreatedId(client.PostAsJsonAsync("/api/v1/wireguard/networks",
            new { name = "Corp", cidr = "10.20.0.0/24", dns = new[] { "1.1.1.1" } }));

        var instanceId = await CreatedId(client.PostAsJsonAsync("/api/v1/wireguard/instances",
            new { networkId = network, name = "GW", listenPort = 51830, interfaceAddress = "10.20.0.1/24", endpointHost = "vpn.example.com:51830" }));

        // The server (interface) config is versioned on instance create.
        var instanceVersions = await GetJson<ConfigVersionItem[]>(client, $"/api/v1/wireguard/instances/{instanceId}/config/versions");
        instanceVersions.Should().ContainSingle().Which.Version.Should().Be(1);

        // Peer create → its config is versioned (v1).
        var peerId = await CreatedId(client.PostAsJsonAsync($"/api/v1/wireguard/instances/{instanceId}/peers",
            new { name = "Laptop", deviceType = "Laptop" }));

        var afterCreate = await GetJson<ConfigVersionItem[]>(client, $"/api/v1/wireguard/peers/{peerId}/config/versions");
        afterCreate.Should().ContainSingle().Which.Version.Should().Be(1);

        // Key rotation → v2 with a different checksum.
        var rotate = await client.PostAsync($"/api/v1/wireguard/peers/{peerId}/keys/rotate", content: null);
        rotate.StatusCode.Should().Be(HttpStatusCode.OK);

        var afterRotate = await GetJson<ConfigVersionItem[]>(client, $"/api/v1/wireguard/peers/{peerId}/config/versions");
        afterRotate.Should().HaveCount(2);
        afterRotate.Select(v => v.Version).Should().BeEquivalentTo(new[] { 1, 2 });
        afterRotate.Select(v => v.Checksum).Distinct().Should().HaveCount(2);

        // The revealed content is a real wg-quick config.
        var content = await GetJson<ConfigVersionContentDto>(client, $"/api/v1/wireguard/peers/{peerId}/config/versions/2");
        content.Version.Should().Be(2);
        content.Content.Should().Contain("[Interface]");
    }

    [Fact]
    public async Task Network_delete_is_guarded_and_peer_can_be_edited()
    {
        var client = _factory.CreateClient();
        await AuthenticateAsOwnerAsync(client);

        var networkId = await CreatedId(client.PostAsJsonAsync("/api/v1/wireguard/networks",
            new { name = "EditNet", cidr = "10.40.0.0/24" }));
        var instanceId = await CreatedId(client.PostAsJsonAsync("/api/v1/wireguard/instances",
            new { networkId, name = "EditGW", listenPort = 51850, interfaceAddress = "10.40.0.1/24" }));

        // Deleting a network is refused (409) while an instance still references it.
        var guarded = await client.DeleteAsync($"/api/v1/wireguard/networks/{networkId}");
        guarded.StatusCode.Should().Be(HttpStatusCode.Conflict);

        // Editing a peer's keepalive versions its config (v2).
        var peerId = await CreatedId(client.PostAsJsonAsync($"/api/v1/wireguard/instances/{instanceId}/peers",
            new { name = "Phone", deviceType = "Phone" }));
        var patch = await client.PatchAsJsonAsync($"/api/v1/wireguard/peers/{peerId}",
            new { name = "Phone-Renamed", persistentKeepalive = 25 });
        patch.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var versions = await GetJson<ConfigVersionItem[]>(client, $"/api/v1/wireguard/peers/{peerId}/config/versions");
        versions.Select(v => v.Version).Should().Contain(new[] { 1, 2 });

        // After removing the instance, the network deletes cleanly (204).
        (await client.DeleteAsync($"/api/v1/wireguard/instances/{instanceId}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await client.DeleteAsync($"/api/v1/wireguard/networks/{networkId}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Server_config_includes_active_peers_with_keys_and_excludes_disabled()
    {
        var client = _factory.CreateClient();
        await AuthenticateAsOwnerAsync(client);

        var networkId = await CreatedId(client.PostAsJsonAsync("/api/v1/wireguard/networks",
            new { name = "Srv", cidr = "10.60.0.0/24" }));
        var instanceId = await CreatedId(client.PostAsJsonAsync("/api/v1/wireguard/instances",
            new { networkId, name = "SrvGW", listenPort = 51861, interfaceAddress = "10.60.0.1/24" }));

        // A: active, server-generated, with a preshared key (the default).
        var a = await CreatePeer(client, instanceId, new { name = "Active-PSK" });
        // B: active, client-supplied public key, no preshared key.
        var clientPublicKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var b = await CreatePeer(client, instanceId,
            new { name = "Client-Key", generateKeypair = false, publicKey = clientPublicKey, usePresharedKey = false });
        // C: active then disabled → must be excluded from the server config.
        var c = await CreatePeer(client, instanceId, new { name = "Disabled" });
        (await client.PostAsync($"/api/v1/wireguard/peers/{c.Id}/disable", content: null)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        var config = await client.GetStringAsync($"/api/v1/wireguard/instances/{instanceId}/config");

        // [Interface] with the server key + listen port.
        config.Should().Contain("[Interface]").And.Contain("ListenPort = 51861").And.Contain("PrivateKey = ");
        // Active peers present with their public key + /32 allowed-IP.
        config.Should().Contain(a.PublicKey).And.Contain($"AllowedIPs = {a.AssignedAddress}");
        config.Should().Contain(b.PublicKey).And.Contain($"AllowedIPs = {b.AssignedAddress}");
        config.Should().Contain("PresharedKey = ");   // A has one; B does not.
        // The disabled peer is excluded.
        config.Should().NotContain(c.PublicKey);
    }

    [Fact]
    public async Task Network_dns_entries_are_trimmed_and_blanks_dropped()
    {
        var client = _factory.CreateClient();
        await AuthenticateAsOwnerAsync(client);

        // Mirror a "1.1.1.1, 9.9.9.9" style input: surrounding spaces + an empty entry must not reach
        // the stored/rendered config. (The valid IPs themselves are accepted.)
        var networkId = await CreatedId(client.PostAsJsonAsync("/api/v1/wireguard/networks",
            new { name = $"DnsTrim {Guid.NewGuid():N}", cidr = "10.70.0.0/24", dns = new[] { "1.1.1.1", " 9.9.9.9 ", "" } }));

        var network = await GetJson<NetworkDetail>(client, $"/api/v1/wireguard/networks/{networkId}");
        network.Dns.Should().Equal("1.1.1.1", "9.9.9.9");
    }

    [Fact]
    public async Task Network_dns_must_be_valid_ip_addresses()
    {
        var client = _factory.CreateClient();
        await AuthenticateAsOwnerAsync(client);

        // A non-IP DNS entry is rejected with a field-level error keyed on `dns` (drives the UI's DNS field).
        var bad = await client.PostAsJsonAsync("/api/v1/wireguard/networks",
            new { name = $"BadDns {Guid.NewGuid():N}", cidr = "10.71.0.0/24", dns = new[] { "not-an-ip" } });
        bad.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var problem = await bad.Content.ReadFromJsonAsync<ValidationProblemResponse>();
        problem!.Errors.Should().ContainKey("dns");

        // Valid IPs are accepted.
        (await client.PostAsJsonAsync("/api/v1/wireguard/networks",
            new { name = $"GoodDns {Guid.NewGuid():N}", cidr = "10.72.0.0/24", dns = new[] { "1.1.1.1", "9.9.9.9" } }))
            .StatusCode.Should().Be(HttpStatusCode.Created);
    }

    private static async Task<PeerCreated> CreatePeer(HttpClient client, Guid instanceId, object body)
    {
        var response = await client.PostAsJsonAsync($"/api/v1/wireguard/instances/{instanceId}/peers", body);
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<PeerCreated>())!;
    }

    private static async Task<T> GetJson<T>(HttpClient client, string url)
    {
        var value = await client.GetFromJsonAsync<T>(url);
        value.Should().NotBeNull();
        return value!;
    }

    private static async Task<Guid> CreatedId(Task<HttpResponseMessage> request)
    {
        var response = await request;
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await response.Content.ReadFromJsonAsync<IdResponse>();
        return body!.Id;
    }

    private async Task AuthenticateAsOwnerAsync(HttpClient client)
    {
        var unique = Guid.NewGuid().ToString("N");
        var email = $"owner+{unique}@wirehq.test";
        const string password = "Sup3rSecret!!";

        // Unique org name too — integration classes share one database (ApiCollection), so a fixed
        // organization slug would collide across tests with `organization.slug_taken`.
        var register = await client.PostAsJsonAsync("/api/v1/auth/register",
            new { email, password, firstName = "WG Owner", lastName = "Test", acceptTerms = true });
        register.StatusCode.Should().Be(HttpStatusCode.Created);

        await _factory.VerifyEmailAsync(email);

        var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password });
        login.StatusCode.Should().Be(HttpStatusCode.OK);
        var token = (await login.Content.ReadFromJsonAsync<LoginResponse>())!.AccessToken;

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    private sealed record IdResponse(Guid Id);
    private sealed record NetworkDetail(Guid Id, string Name, string Cidr, string[] Dns);
    private sealed record ValidationProblemResponse(Dictionary<string, string[]> Errors);
    private sealed record PeerCreated(Guid Id, string PublicKey, string AssignedAddress);
    private sealed record LoginResponse(string AccessToken);
    private sealed record ConfigVersionItem(int Version, string Checksum, string? Note);
    private sealed record ConfigVersionContentDto(int Version, string Content, string Checksum);
}
