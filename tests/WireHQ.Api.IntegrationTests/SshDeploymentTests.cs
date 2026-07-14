using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Xunit;

namespace WireHQ.Api.IntegrationTests;

/// <summary>
/// End-to-end binding + SSH deployment routing against a real Postgres + the running dispatcher. Binds
/// an instance to an SSH target and proves a deploy is routed through the SSH provider (real SSH.NET):
/// against an unreachable host the job fails gracefully with a connection error and rolls nothing
/// forward. (The happy-path deploy orchestration is covered by SshWireGuardProviderTests.) (docs/12 §6)
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class SshDeploymentTests(WireHqApiFactory factory)
{
    private readonly WireHqApiFactory _factory = factory;

    [Fact]
    public async Task Instance_target_binding_round_trips()
    {
        var client = _factory.CreateClient();
        await AuthenticateAsOwnerAsync(client);

        var instanceId = await CreateInstanceAsync(client, "10.82.0.0/24", "10.82.0.1/24", 51882);

        // Defaults to Local when unset.
        var initial = (await client.GetFromJsonAsync<TargetDto>($"/api/v1/wireguard/instances/{instanceId}/target"))!;
        initial.Kind.Should().Be("Local");

        var sshTargetId = await CreateSshTargetAsync(client, "vpn.example.test", 1);

        var bind = await client.PutAsJsonAsync($"/api/v1/wireguard/instances/{instanceId}/target",
            new { kind = "Ssh", sshTargetId, interfaceName = "wg1" });
        bind.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var bound = (await client.GetFromJsonAsync<TargetDto>($"/api/v1/wireguard/instances/{instanceId}/target"))!;
        bound.Kind.Should().Be("Ssh");
        bound.SshTargetId.Should().Be(sshTargetId);
        bound.InterfaceName.Should().Be("wg1");
        bound.SshTargetName.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Deploying_an_ssh_bound_instance_routes_through_the_ssh_provider_and_fails_gracefully()
    {
        var client = _factory.CreateClient();
        await AuthenticateAsOwnerAsync(client);

        var instanceId = await CreateInstanceAsync(client, "10.83.0.0/24", "10.83.0.1/24", 51883);

        // An SSH target that refuses connections (closed port) → fast, deterministic failure.
        var sshTargetId = await CreateSshTargetAsync(client, "127.0.0.1", 1);
        (await client.PutAsJsonAsync($"/api/v1/wireguard/instances/{instanceId}/target",
            new { kind = "Ssh", sshTargetId })).StatusCode.Should().Be(HttpStatusCode.NoContent);

        var queued = await client.PostAsync($"/api/v1/wireguard/instances/{instanceId}/deploy", content: null);
        queued.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var jobId = (await queued.Content.ReadFromJsonAsync<JobResponse>())!.JobId;

        var detail = await PollUntilTerminalAsync(client, jobId);
        detail.Status.Should().Be("Failed");
        detail.Error.Should().NotBeNullOrEmpty();
        detail.Events.Select(e => e.Phase).Should().ContainInOrder("queued", "dispatched", "applying", "failed");
    }

    private static async Task<Guid> CreateInstanceAsync(HttpClient client, string cidr, string interfaceAddress, int port)
    {
        var networkId = await CreatedId(client.PostAsJsonAsync("/api/v1/wireguard/networks", new { name = $"N{port}", cidr }));
        return await CreatedId(client.PostAsJsonAsync("/api/v1/wireguard/instances",
            new { networkId, name = $"GW{port}", listenPort = port, interfaceAddress }));
    }

    private static async Task<Guid> CreateSshTargetAsync(HttpClient client, string host, int port)
    {
        var create = await client.PostAsJsonAsync("/api/v1/wireguard/ssh-targets", new
        {
            name = $"T-{host}-{port}", host, port, username = "deploy", authKind = "Password", credential = "pw",
        });
        create.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await create.Content.ReadFromJsonAsync<IdResponse>())!.Id;
    }

    private static async Task<DeploymentDetail> PollUntilTerminalAsync(HttpClient client, Guid jobId)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(40))
        {
            var detail = await client.GetFromJsonAsync<DeploymentDetail>($"/api/v1/wireguard/deployments/{jobId}");
            if (detail is not null && detail.Status is "Succeeded" or "Failed" or "RolledBack")
            {
                return detail;
            }

            await Task.Delay(500);
        }

        throw new TimeoutException($"Deployment {jobId} did not reach a terminal state in time.");
    }

    private static async Task<Guid> CreatedId(Task<HttpResponseMessage> request)
    {
        var response = await request;
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<IdResponse>())!.Id;
    }

    private async Task AuthenticateAsOwnerAsync(HttpClient client)
    {
        var unique = Guid.NewGuid().ToString("N");
        var email = $"owner+{unique}@wirehq.test";
        const string password = "Sup3rSecret!!";

        var register = await client.PostAsJsonAsync("/api/v1/auth/register",
            new { email, password, firstName = "Deploy Owner", lastName = "Test", acceptTerms = true });
        register.StatusCode.Should().Be(HttpStatusCode.Created);

        await _factory.VerifyEmailAsync(email);

        var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password });
        login.StatusCode.Should().Be(HttpStatusCode.OK);
        var token = (await login.Content.ReadFromJsonAsync<LoginResponse>())!.AccessToken;

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    private sealed record IdResponse(Guid Id);
    private sealed record LoginResponse(string AccessToken);
    private sealed record JobResponse(Guid JobId, string Status);
    private sealed record TargetDto(Guid InstanceId, string Kind, Guid? SshTargetId, string? SshTargetName, string InterfaceName);
    private sealed record DeploymentEventItem(string Phase, string? Detail, DateTimeOffset AtUtc);
    private sealed record DeploymentDetail(Guid Id, string Status, string? Error, List<DeploymentEventItem> Events);
}
