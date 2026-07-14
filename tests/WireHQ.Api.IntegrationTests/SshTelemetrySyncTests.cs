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
/// The end-to-end telemetry proof: deploy a real config to a real SSH host, then pull live status and
/// assert the per-peer handshake/transfer lands in the model. The throwaway host's `wg` shim synthesizes
/// a <c>wg show dump</c> from the deployed config, so the dump's peer public keys match the real peers —
/// closing the loop the deploy test opened: deploy → status sync → telemetry persisted + surfaced.
/// (docs/12 §10, gap #2)
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class SshTelemetrySyncTests(WireHqApiFactory factory) : IAsyncLifetime
{
    private readonly WireHqApiFactory _factory = factory;
    private IFutureDockerImage _image = null!;
    private IContainer _host = null!;

    public async Task InitializeAsync()
    {
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
    public async Task Deploy_then_refresh_lands_telemetry_and_detects_drift()
    {
        var client = _factory.CreateClient();
        await AuthenticateAsOwnerAsync(client);

        const int listenPort = 51887;
        var networkId = await CreatedId(client.PostAsJsonAsync("/api/v1/wireguard/networks", new { name = "Telemetry", cidr = "10.85.0.0/24" }));
        var instanceId = await CreatedId(client.PostAsJsonAsync("/api/v1/wireguard/instances",
            new { networkId, name = "TelemetryGW", listenPort, interfaceAddress = "10.85.0.1/24" }));
        var peerResponse = await client.PostAsJsonAsync($"/api/v1/wireguard/instances/{instanceId}/peers", new { name = "Dev", deviceType = "Laptop" });
        peerResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        var peer = (await peerResponse.Content.ReadFromJsonAsync<PeerCreated>())!;

        var sshTargetId = await CreatedId(client.PostAsJsonAsync("/api/v1/wireguard/ssh-targets", new
        {
            name = "Telemetry host",
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
        (await PollUntilTerminalAsync(client, jobId)).Should().Be("Succeeded");

        // Pull live status: parses `wg show dump` over SSH and persists the telemetry.
        var statusResponse = await client.PostAsync($"/api/v1/wireguard/instances/{instanceId}/status", content: null);
        statusResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var status = (await statusResponse.Content.ReadFromJsonAsync<InstanceStatusResponse>())!;

        status.HasLiveStatus.Should().BeTrue();
        status.State.Should().Be("Running");
        status.ListenPort.Should().Be(listenPort, because: "the dump is synthesized from the deployed config");
        var observed = status.Peers.Should().ContainSingle(p => p.PublicKey == peer.PublicKey).Subject;
        observed.RxBytes.Should().Be(1024);
        observed.TxBytes.Should().Be(2048);
        observed.LastHandshakeAtUtc.Should().NotBeNull();
        status.HasDrift.Should().BeFalse(because: "the freshly-deployed config matches desired");

        // …and it persisted: the peer list now reports the live handshake + transfer (was always 0 before).
        var peers = (await client.GetFromJsonAsync<List<PeerListEntry>>($"/api/v1/wireguard/instances/{instanceId}/peers"))!;
        var stored = peers.Single(p => p.Id == peer.Id);
        stored.LastHandshakeAtUtc.Should().NotBeNull();
        stored.RxBytes.Should().Be(1024);
        stored.TxBytes.Should().Be(2048);

        // Diverge desired from the deployed config: add a peer but DON'T re-deploy → drift.
        (await client.PostAsJsonAsync($"/api/v1/wireguard/instances/{instanceId}/peers", new { name = "Drifter", deviceType = "Phone" }))
            .StatusCode.Should().Be(HttpStatusCode.Created);
        var drifted = await PostStatusAsync(client, instanceId);
        drifted.HasDrift.Should().BeTrue(because: "a peer was added to desired state but not deployed to the host");

        // The Deployment panel reads drift off the target binding too (kept fresh by the same sync).
        var target = (await client.GetFromJsonAsync<InstanceTargetResponse>($"/api/v1/wireguard/instances/{instanceId}/target"))!;
        target.HasDrift.Should().BeTrue();
    }

    private async Task<InstanceStatusResponse> PostStatusAsync(HttpClient client, Guid instanceId)
    {
        var response = await client.PostAsync($"/api/v1/wireguard/instances/{instanceId}/status", content: null);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<InstanceStatusResponse>())!;
    }

    private async Task<string> PollUntilTerminalAsync(HttpClient client, Guid jobId)
    {
        for (var i = 0; i < 60; i++)
        {
            var detail = await client.GetFromJsonAsync<DeploymentDetail>($"/api/v1/wireguard/deployments/{jobId}");
            if (detail is not null && detail.Status is "Succeeded" or "Failed" or "RolledBack")
            {
                return detail.Status;
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
            new { email, password, firstName = "Telemetry Owner", lastName = "Test", acceptTerms = true });
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
    private sealed record InstanceStatusResponse(bool HasLiveStatus, string State, int? ListenPort, bool HasDrift, IReadOnlyList<PeerTelemetry> Peers);
    private sealed record PeerTelemetry(string PublicKey, DateTimeOffset? LastHandshakeAtUtc, long RxBytes, long TxBytes, string? Endpoint);
    private sealed record PeerListEntry(Guid Id, DateTimeOffset? LastHandshakeAtUtc, long RxBytes, long TxBytes);
    private sealed record InstanceTargetResponse(bool HasDrift);
}
