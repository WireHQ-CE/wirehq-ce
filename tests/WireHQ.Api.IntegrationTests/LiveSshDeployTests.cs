using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Images;
using FluentAssertions;
using Xunit;

namespace WireHQ.Api.IntegrationTests;

/// <summary>
/// The end-to-end proof: deploy a real WireGuard config to a real SSH host (a throwaway container with
/// fake wireguard-tools, so no kernel module is needed — ADR-002) and assert it lands on the host. This
/// closes the gap left by the fake-session unit tests: real SSH connect → auth → upload → command
/// sequence → verify → success, and the rendered config is actually written. (docs/12 §6)
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class LiveSshDeployTests(WireHqApiFactory factory) : IAsyncLifetime
{
    private readonly WireHqApiFactory _factory = factory;
    private IFutureDockerImage _image = null!;
    private IContainer _host = null!;

    public async Task InitializeAsync()
    {
        // Resolve the build context from the test's output dir (the ssh-host folder is copied there),
        // not from the source tree — CI's deterministic builds rewrite source paths to /_/… .
        _image = new ImageFromDockerfileBuilder()
            .WithDockerfileDirectory(Path.Combine(AppContext.BaseDirectory, "ssh-host"))
            .WithDockerfile("Dockerfile")
            .WithName("wirehq-ssh-test-host:latest")
            .WithCleanUp(false)
            .Build();
        await _image.CreateAsync();

        _host = new ContainerBuilder()
            .WithImage(_image)
            .WithPortBinding(22, assignRandomHostPort: true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilPortIsAvailable(22))
            .Build();
        await _host.StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _host.DisposeAsync();
        await _image.DisposeAsync();
    }

    [Fact]
    public async Task Deploys_a_real_config_over_ssh_and_it_lands_on_the_host()
    {
        var client = _factory.CreateClient();
        await AuthenticateAsOwnerAsync(client);

        // Instance + an active peer, so the rendered server config carries a [Peer] block.
        var networkId = await CreatedId(client.PostAsJsonAsync("/api/v1/wireguard/networks", new { name = "Live", cidr = "10.84.0.0/24" }));
        var instanceId = await CreatedId(client.PostAsJsonAsync("/api/v1/wireguard/instances",
            new { networkId, name = "LiveGW", listenPort = 51884, interfaceAddress = "10.84.0.1/24" }));
        var peerResponse = await client.PostAsJsonAsync($"/api/v1/wireguard/instances/{instanceId}/peers", new { name = "Dev", deviceType = "Laptop" });
        peerResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        var peer = (await peerResponse.Content.ReadFromJsonAsync<PeerCreated>())!;

        // Register the throwaway host as an SSH target (password auth, no pinned key → trust-on-first-use), bind, deploy.
        var sshTargetId = await CreatedId(client.PostAsJsonAsync("/api/v1/wireguard/ssh-targets", new
        {
            name = "Live host",
            host = _host.Hostname,
            port = (int)_host.GetMappedPublicPort(22),
            username = "deploy",
            authKind = "Password",
            credential = "deploypw",
        }));
        (await client.PutAsJsonAsync($"/api/v1/wireguard/instances/{instanceId}/target",
            new { kind = "Ssh", sshTargetId })).StatusCode.Should().Be(HttpStatusCode.NoContent);

        var queued = await client.PostAsync($"/api/v1/wireguard/instances/{instanceId}/deploy", content: null);
        queued.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var jobId = (await queued.Content.ReadFromJsonAsync<JobResponse>())!.JobId;

        var detail = await PollUntilTerminalAsync(client, jobId);
        detail.Status.Should().Be("Succeeded", because: detail.Error ?? "no error reported");

        // The rendered server config actually landed on the host.
        var cat = await _host.ExecAsync(["cat", "/etc/wireguard/wg0.conf"]);
        cat.Stdout.Should().Contain("[Interface]").And.Contain(peer.PublicKey);
    }

    private async Task<DeploymentDetail> PollUntilTerminalAsync(HttpClient client, Guid jobId)
    {
        for (var i = 0; i < 60; i++)
        {
            var detail = await client.GetFromJsonAsync<DeploymentDetail>($"/api/v1/wireguard/deployments/{jobId}");
            if (detail is not null && detail.Status is "Succeeded" or "Failed" or "RolledBack")
            {
                return detail;
            }

            await Task.Delay(500);
        }

        throw new TimeoutException($"Deployment {jobId} did not finish in time.");
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
            new { email, password, firstName = "Live Owner", lastName = "Test", acceptTerms = true });
        register.StatusCode.Should().Be(HttpStatusCode.Created);

        await _factory.VerifyEmailAsync(email);

        var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password });
        var token = (await login.Content.ReadFromJsonAsync<LoginResponse>())!.AccessToken;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    private sealed record IdResponse(Guid Id);
    private sealed record LoginResponse(string AccessToken);
    private sealed record PeerCreated(Guid Id, string PublicKey, string AssignedAddress);
    private sealed record JobResponse(Guid JobId, string Status);
    private sealed record DeploymentDetail(Guid Id, string Status, string? Error);
}
