using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Xunit;

namespace WireHQ.Api.IntegrationTests;

/// <summary>
/// Exercises SSH deployment-target management (Phase 1 slice 1): CRUD against a real Postgres, that the
/// encrypted credential is never returned, and that validation + not-found behave. (docs/12 §6)
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class SshTargetTests(WireHqApiFactory factory)
{
    private const string Secret = "-----BEGIN OPENSSH PRIVATE KEY-----\nNEVER-LEAK-THIS-KEY-MATERIAL\n-----END OPENSSH PRIVATE KEY-----";

    private readonly WireHqApiFactory _factory = factory;

    [Fact]
    public async Task Ssh_targets_round_trip_and_never_expose_the_credential()
    {
        var client = _factory.CreateClient();
        await AuthenticateAsOwnerAsync(client);

        // Create.
        var create = await client.PostAsJsonAsync("/api/v1/wireguard/ssh-targets", new
        {
            name = "Edge GW",
            host = "vpn.acme.test",
            port = 22,
            username = "deploy",
            authKind = "PrivateKey",
            credential = Secret,
            hostKeyFingerprint = "SHA256:abc123",
        });
        create.StatusCode.Should().Be(HttpStatusCode.Created);
        var id = (await create.Content.ReadFromJsonAsync<IdResponse>())!.Id;
        id.Should().NotBeEmpty();

        // List — present, and the raw response must not contain the secret.
        var listResponse = await client.GetAsync("/api/v1/wireguard/ssh-targets");
        listResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        (await listResponse.Content.ReadAsStringAsync()).Should().NotContain("NEVER-LEAK-THIS-KEY-MATERIAL");
        var list = (await listResponse.Content.ReadFromJsonAsync<List<SshTargetItem>>())!;
        list.Should().ContainSingle(t => t.Id == id).Which.Host.Should().Be("vpn.acme.test");

        // Get one — also free of the secret.
        var getResponse = await client.GetAsync($"/api/v1/wireguard/ssh-targets/{id}");
        (await getResponse.Content.ReadAsStringAsync()).Should().NotContain("NEVER-LEAK-THIS-KEY-MATERIAL");
        var detail = (await getResponse.Content.ReadFromJsonAsync<SshTargetItem>())!;
        detail.Username.Should().Be("deploy");
        detail.AuthKind.Should().Be("PrivateKey");
        detail.HostKeyFingerprint.Should().Be("SHA256:abc123");

        // Update name + host (and rotate the credential).
        var patch = await client.PatchAsJsonAsync($"/api/v1/wireguard/ssh-targets/{id}",
            new { name = "Edge GW (renamed)", host = "vpn2.acme.test", credential = "newsecret" });
        patch.StatusCode.Should().Be(HttpStatusCode.NoContent);
        var afterPatch = (await client.GetFromJsonAsync<SshTargetItem>($"/api/v1/wireguard/ssh-targets/{id}"))!;
        afterPatch.Name.Should().Be("Edge GW (renamed)");
        afterPatch.Host.Should().Be("vpn2.acme.test");

        // Delete — gone.
        (await client.DeleteAsync($"/api/v1/wireguard/ssh-targets/{id}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await client.GetAsync($"/api/v1/wireguard/ssh-targets/{id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Creating_a_target_without_a_host_is_rejected()
    {
        var client = _factory.CreateClient();
        await AuthenticateAsOwnerAsync(client);

        var response = await client.PostAsJsonAsync("/api/v1/wireguard/ssh-targets", new
        {
            name = "Bad", host = "", username = "deploy", authKind = "PrivateKey", credential = "x",
        });
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    private static async Task AuthenticateAsOwnerAsync(HttpClient client)
    {
        var unique = Guid.NewGuid().ToString("N");
        var email = $"owner+{unique}@wirehq.test";
        const string password = "Sup3rSecret!!";

        var register = await client.PostAsJsonAsync("/api/v1/auth/register",
            new { email, password, firstName = "SSH Owner", lastName = "Test", acceptTerms = true });
        register.StatusCode.Should().Be(HttpStatusCode.Created);

        var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password });
        login.StatusCode.Should().Be(HttpStatusCode.OK);
        var token = (await login.Content.ReadFromJsonAsync<LoginResponse>())!.AccessToken;

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    private sealed record IdResponse(Guid Id);
    private sealed record LoginResponse(string AccessToken);
    private sealed record SshTargetItem(
        Guid Id, string Name, string Host, int Port, string Username, string AuthKind, string? HostKeyFingerprint, DateTimeOffset CreatedAtUtc);
}
