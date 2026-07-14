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
/// The agent end-to-end proof: the <b>real compiled Go agent</b> runs in a throwaway container and drives the
/// whole loop against the real mTLS gateway — enrol (CSR → client cert), poll a signed job over mTLS, verify
/// the bundle signature against the org CA, apply it with (fake) <c>wg-quick</c>, and report back. No kernel
/// module (ADR-002); the agent reaches the gateway outbound via <c>host.docker.internal</c> and trusts the
/// dev self-signed server cert with <c>--insecure</c>. This exercises the <b>AgentManaged</b> custody path
/// through the real binary: WireHQ ships a key-less bundle, the agent generates + injects its own interface
/// key, and reports the public key WireHQ then adopts. (docs/13-agent.md, ADR-028)
/// </summary>
public sealed class LiveAgentDeployTests(AgentGatewayFixture fixture) : IClassFixture<AgentGatewayFixture>, IAsyncLifetime
{
    private readonly AgentGatewayFixture _fixture = fixture;
    private IFutureDockerImage _image = null!;
    private IContainer _agent = null!;
    private string _contextDir = null!;

    public async Task InitializeAsync()
    {
        // Assemble the build context: the copied Dockerfile + `wg` shim (output dir) plus the repo's agent/
        // source grafted in, so the multi-stage `COPY agent/` resolves. Resolved via AppContext.BaseDirectory
        // + the sln-root walk — robust under CI's deterministic (path-mapped) builds.
        _contextDir = BuildAgentContext();

        _image = new ImageFromDockerfileBuilder()
            .WithDockerfileDirectory(_contextDir)
            .WithDockerfile("Dockerfile")
            .WithName("wirehq-agent-test-host:latest")
            .WithCleanUp(false)
            .Build();
        await _image.CreateAsync();

        // The agent connects OUTBOUND to the gateway, which the test process hosts on the machine's loopback.
        // Host network mode lets the container reach it at localhost in every environment — the CI runner
        // (dotnet on the host) and a host-networked test container alike — without brittle host-gateway routing.
        _agent = new ContainerBuilder()
            .WithImage(_image)
            .WithCreateParameterModifier(parameters => parameters.HostConfig.NetworkMode = "host")
            .Build();
        await _agent.StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _agent.DisposeAsync();
        await _image.DisposeAsync();
        try { Directory.Delete(_contextDir, recursive: true); } catch { /* best-effort temp cleanup */ }
    }

    [Fact]
    public async Task The_real_agent_enrols_pulls_a_signed_job_applies_it_and_reports_succeeded()
    {
        var api = _fixture.CreateApiClient();
        var email = await RegisterOwnerAsync(api);
        await _fixture.VerifyEmailAsync(email); // the verified-email gate guards instance creation

        // An instance (WireHQ generates its key) + an active peer, bound to the agent under AgentManaged custody.
        var networkId = await CreatedId(api.PostAsJsonAsync("/api/v1/wireguard/networks",
            new { name = "LiveAgentNet", cidr = "10.92.0.0/24" }));
        var created = (await (await api.PostAsJsonAsync("/api/v1/wireguard/instances",
            new { networkId, name = "LiveAgentGW", listenPort = 51902, interfaceAddress = "10.92.0.1/24" }))
            .Content.ReadFromJsonAsync<CreateInstanceDto>())!;
        var (instanceId, originalPublicKey) = (created.Id, created.PublicKey);

        var peer = (await (await api.PostAsJsonAsync($"/api/v1/wireguard/instances/{instanceId}/peers",
            new { name = "Dev", deviceType = "Laptop" })).Content.ReadFromJsonAsync<PeerCreated>())!;

        (await api.PutAsJsonAsync($"/api/v1/wireguard/instances/{instanceId}/target",
            new { kind = "Agent", agentId = await EnrolAgentAsync(api), keyCustody = "AgentManaged" })).StatusCode
            .Should().Be(HttpStatusCode.NoContent);

        // Deploy — the dispatcher leaves the Pull (agent) job Dispatched, awaiting the agent's poll.
        var deploy = await api.PostAsync($"/api/v1/wireguard/instances/{instanceId}/deploy", null);
        var jobId = (await deploy.Content.ReadFromJsonAsync<DeployResponse>())!.JobId;
        await PollJobStatusAsync(api, jobId, "Dispatched");

        // The real binary runs ONE cycle: heartbeat → pull the signed job → verify → generate+inject its key
        // → wg-quick up → report Succeeded → telemetry.
        var run = await _agent.ExecAsync(["wirehq-agent", "run", "--once", "--server", GatewayUrl(), "--insecure"]);
        run.ExitCode.Should().Be(0, because: $"agent run failed:\n{run.Stdout}\n{run.Stderr}");

        await PollJobStatusAsync(api, jobId, "Succeeded");

        // The config the agent applied landed on the host — with the peer block AND the agent-injected
        // PrivateKey (WireHQ shipped it key-less).
        var conf = await _agent.ExecAsync(["cat", "/etc/wireguard/wg0.conf"]);
        conf.Stdout.Should().Contain("[Interface]")
            .And.Contain("PrivateKey =")
            .And.Contain(peer.PublicKey);

        // WireHQ adopted the agent-generated interface public key (replacing its own) and cleared "pending".
        var detail = (await api.GetFromJsonAsync<InstanceDetailDto>($"/api/v1/wireguard/instances/{instanceId}"))!;
        detail.PublicKey.Should().NotBe(originalPublicKey);
        var target = (await api.GetFromJsonAsync<TargetDto>($"/api/v1/wireguard/instances/{instanceId}/target"))!;
        target.AgentKeyPending.Should().BeFalse();
        target.HasDrift.Should().BeFalse(); // the agent reported its freshly-applied config as in-sync

        // Drift: tamper the deployed config on the host, run another cycle → the agent detects + reports it.
        (await _agent.ExecAsync(["sh", "-c", "echo '# tampered out-of-band' >> /etc/wireguard/wg0.conf"]))
            .ExitCode.Should().Be(0);
        var recheck = await _agent.ExecAsync(["wirehq-agent", "run", "--once", "--server", GatewayUrl(), "--insecure"]);
        recheck.ExitCode.Should().Be(0, because: $"agent run failed:\n{recheck.Stdout}\n{recheck.Stderr}");

        await PollUntilAsync(async () =>
            (await api.GetFromJsonAsync<TargetDto>($"/api/v1/wireguard/instances/{instanceId}/target"))!.HasDrift);
        (await api.GetFromJsonAsync<TargetDto>($"/api/v1/wireguard/instances/{instanceId}/target"))!
            .HasDrift.Should().BeTrue();
    }

    private static async Task PollUntilAsync(Func<Task<bool>> condition)
    {
        for (var i = 0; i < 20 && !await condition(); i++)
        {
            await Task.Delay(250);
        }
    }

    /// <summary>Mints a token and runs `wirehq-agent enroll` inside the container; returns the new agent id.</summary>
    private async Task<Guid> EnrolAgentAsync(HttpClient api)
    {
        var token = (await (await api.PostAsJsonAsync("/api/v1/agents/enroll-tokens", new { }))
            .Content.ReadFromJsonAsync<MintResponse>())!.Token;

        var enrol = await _agent.ExecAsync(
            ["wirehq-agent", "enroll", "--server", GatewayUrl(), "--token", token, "--insecure", "--name", "live-agent"]);
        enrol.ExitCode.Should().Be(0, because: $"agent enroll failed:\n{enrol.Stdout}\n{enrol.Stderr}");

        var agents = (await api.GetFromJsonAsync<List<AgentDto>>("/api/v1/agents"))!;
        return agents.Should().ContainSingle(a => a.Status == "Active").Subject.Id;
    }

    private string GatewayUrl() => $"https://localhost:{_fixture.AgentPort}";

    /// <summary>Builds a Docker build context: the copied Dockerfile + `wg` shim, plus the repo's agent/ source.</summary>
    private static string BuildAgentContext()
    {
        var contextDir = Path.Combine(Path.GetTempPath(), "wirehq-agent-host-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(contextDir);

        var source = Path.Combine(AppContext.BaseDirectory, "agent-host");
        foreach (var file in Directory.GetFiles(source))
        {
            File.Copy(file, Path.Combine(contextDir, Path.GetFileName(file)), overwrite: true);
        }

        CopyDirectory(LocateAgentSource(), Path.Combine(contextDir, "agent"));
        return contextDir;
    }

    /// <summary>Walks up from the test output dir to the repo root (WireHQ.sln) and returns its agent/ dir.</summary>
    private static string LocateAgentSource()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "WireHQ.sln")))
        {
            root = root.Parent;
        }

        if (root is null)
        {
            throw new InvalidOperationException("Could not locate the repository root (WireHQ.sln).");
        }

        return Path.Combine(root.FullName, "agent");
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var dir in Directory.GetDirectories(source))
        {
            var name = Path.GetFileName(dir);
            if (name is "bin" or "obj" or ".git")
            {
                continue; // build artifacts / VCS — never part of the agent build context
            }

            CopyDirectory(dir, Path.Combine(destination, name));
        }

        foreach (var file in Directory.GetFiles(source))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
        }
    }

    private static async Task<string> RegisterOwnerAsync(HttpClient api)
    {
        var email = $"live-agent+{Guid.NewGuid():N}@wirehq.test";
        const string password = "Sup3rSecret!!";
        await api.PostAsJsonAsync("/api/v1/auth/register",
            new { email, password, firstName = "Live", lastName = "Agent", acceptTerms = true });
        var login = await api.PostAsJsonAsync("/api/v1/auth/login", new { email, password });
        var token = (await login.Content.ReadFromJsonAsync<LoginResponse>())!.AccessToken;
        api.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return email;
    }

    private static async Task<Guid> CreatedId(Task<HttpResponseMessage> request)
    {
        var response = await request;
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<IdResponse>())!.Id;
    }

    private static async Task PollJobStatusAsync(HttpClient api, Guid jobId, string expected)
    {
        for (var i = 0; i < 40; i++)
        {
            var detail = await api.GetFromJsonAsync<JobStatusDto>($"/api/v1/wireguard/deployments/{jobId}");
            if (detail!.Status == expected)
            {
                return;
            }

            await Task.Delay(500);
        }

        throw new Xunit.Sdk.XunitException($"Job {jobId} did not reach '{expected}'.");
    }

    private sealed record LoginResponse(string AccessToken);
    private sealed record MintResponse(Guid Id, string Token, DateTimeOffset ExpiresAtUtc);
    private sealed record AgentDto(Guid Id, string Name, string Status);
    private sealed record IdResponse(Guid Id);
    private sealed record CreateInstanceDto(Guid Id, string Slug, string PublicKey, int ListenPort);
    private sealed record PeerCreated(Guid Id, string PublicKey, string AssignedAddress);
    private sealed record DeployResponse(Guid JobId, string Status);
    private sealed record JobStatusDto(string Status);
    private sealed record InstanceDetailDto(string PublicKey);
    private sealed record TargetDto(string Kind, string KeyCustody, bool AgentKeyPending, bool HasDrift);
}
