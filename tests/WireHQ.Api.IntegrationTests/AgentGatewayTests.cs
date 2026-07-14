using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using FluentAssertions;
using Xunit;

namespace WireHQ.Api.IntegrationTests;

/// <summary>
/// Proves the agent mTLS gateway end-to-end through a real TLS handshake (ADR-028): mint → enrol (CSR → signed
/// client cert) → an authenticated heartbeat over mTLS; a disabled agent's cert is rejected (the no-CRL kill
/// switch); the token is single-use; a request with no client cert is unauthorized; and the agent routes are
/// not reachable on the JWT listener.
/// </summary>
public sealed class AgentGatewayTests(AgentGatewayFixture fixture) : IClassFixture<AgentGatewayFixture>
{
    private readonly AgentGatewayFixture _fixture = fixture;

    [Fact]
    public async Task Enroll_then_authenticated_heartbeat_succeeds_and_binds_the_agent_to_its_org()
    {
        var api = _fixture.CreateApiClient();
        await RegisterOwnerAsync(api);
        var token = await MintTokenAsync(api);

        var (csrPem, key) = BuildCsr();
        using var noCertClient = _fixture.CreateAgentClient(null);
        var enrollResponse = await noCertClient.PostAsJsonAsync("/agent/v1/enroll",
            new { token, csr = csrPem, name = "edge-1", platform = "linux-amd64" });
        enrollResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var enrolled = (await enrollResponse.Content.ReadFromJsonAsync<EnrollResponse>())!;
        enrolled.CertificatePem.Should().Contain("BEGIN CERTIFICATE");

        // Present the issued cert over mTLS — the heartbeat authenticates and is recorded.
        using var clientCert = BuildClientCertificate(enrolled.CertificatePem, key);
        using var agentClient = _fixture.CreateAgentClient(clientCert);
        var heartbeat = await agentClient.PostAsJsonAsync("/agent/v1/heartbeat", new { version = "0.1.0" });
        heartbeat.StatusCode.Should().Be(HttpStatusCode.OK);

        // The agent is bound to the minting org: that org's operator API sees it; its version was recorded.
        var agents = (await api.GetFromJsonAsync<List<AgentDto>>("/api/v1/agents"))!;
        var row = agents.Should().ContainSingle(a => a.Id == enrolled.AgentId).Subject;
        row.Status.Should().Be("Active");
        row.Version.Should().Be("0.1.0");
    }

    [Fact]
    public async Task A_disabled_agent_certificate_is_rejected()
    {
        var api = _fixture.CreateApiClient();
        await RegisterOwnerAsync(api);
        var token = await MintTokenAsync(api);

        var (csrPem, key) = BuildCsr();
        using var noCertClient = _fixture.CreateAgentClient(null);
        var enrolled = (await (await noCertClient.PostAsJsonAsync("/agent/v1/enroll",
            new { token, csr = csrPem })).Content.ReadFromJsonAsync<EnrollResponse>())!;

        using var clientCert = BuildClientCertificate(enrolled.CertificatePem, key);
        using var agentClient = _fixture.CreateAgentClient(clientCert);

        // Works while active...
        (await agentClient.PostAsJsonAsync("/agent/v1/heartbeat", new { })).StatusCode.Should().Be(HttpStatusCode.OK);

        // ...then the operator disables it → the SAME cert is rejected immediately (no CRL).
        (await api.PostAsync($"/api/v1/agents/{enrolled.AgentId}/disable", null)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await agentClient.PostAsJsonAsync("/agent/v1/heartbeat", new { })).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task An_enrollment_token_is_single_use()
    {
        var api = _fixture.CreateApiClient();
        await RegisterOwnerAsync(api);
        var token = await MintTokenAsync(api);

        using var noCertClient = _fixture.CreateAgentClient(null);
        var (firstCsr, _) = BuildCsr();
        (await noCertClient.PostAsJsonAsync("/agent/v1/enroll", new { token, csr = firstCsr })).StatusCode
            .Should().Be(HttpStatusCode.OK);

        var (secondCsr, _) = BuildCsr();
        (await noCertClient.PostAsJsonAsync("/agent/v1/enroll", new { token, csr = secondCsr })).StatusCode
            .Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Heartbeat_without_a_client_certificate_is_unauthorized()
    {
        using var noCertClient = _fixture.CreateAgentClient(null);
        (await noCertClient.PostAsJsonAsync("/agent/v1/heartbeat", new { version = "x" })).StatusCode
            .Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Agent_routes_are_not_served_on_the_jwt_listener()
    {
        var api = _fixture.CreateApiClient();
        var (csrPem, _) = BuildCsr();
        // The JWT (:main) listener must not expose the agent surface — the port guard 404s it.
        (await api.PostAsJsonAsync("/agent/v1/enroll", new { token = "whatever", csr = csrPem })).StatusCode
            .Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_deployment_to_an_agent_is_delivered_as_a_signed_bundle_and_completes()
    {
        var api = _fixture.CreateApiClient();
        var email = await RegisterOwnerAsync(api);
        await _fixture.VerifyEmailAsync(email); // the verified-email gate guards instance creation

        // Enrol an agent and keep its client cert + the org CA it was given.
        var token = await MintTokenAsync(api);
        var (csrPem, key) = BuildCsr();
        using var noCertClient = _fixture.CreateAgentClient(null);
        var enrolled = (await (await noCertClient.PostAsJsonAsync("/agent/v1/enroll",
            new { token, csr = csrPem, name = "deployer" })).Content.ReadFromJsonAsync<EnrollResponse>())!;
        using var clientCert = BuildClientCertificate(enrolled.CertificatePem, key);
        using var agentClient = _fixture.CreateAgentClient(clientCert);

        // Create an instance and bind it to the agent.
        var networkId = await CreatedId(api.PostAsJsonAsync("/api/v1/wireguard/networks",
            new { name = "AgentNet", cidr = "10.90.0.0/24" }));
        var instanceId = await CreatedId(api.PostAsJsonAsync("/api/v1/wireguard/instances",
            new { networkId, name = "AgentGW", listenPort = 51900, interfaceAddress = "10.90.0.1/24" }));
        (await api.PutAsJsonAsync($"/api/v1/wireguard/instances/{instanceId}/target",
            new { kind = "Agent", agentId = enrolled.AgentId })).StatusCode.Should().Be(HttpStatusCode.NoContent);

        // Deploy — the dispatcher leaves a Pull (agent) job Dispatched, awaiting the agent.
        var deploy = await api.PostAsync($"/api/v1/wireguard/instances/{instanceId}/deploy", null);
        deploy.StatusCode.Should().Be(HttpStatusCode.Accepted);
        // The deploy's correlation reference (the spine) must reach the agent on the job bundle (ADR-030).
        var correlationId = deploy.Headers.GetValues("X-Correlation-Id").Single();
        var jobId = (await deploy.Content.ReadFromJsonAsync<DeployResponse>())!.JobId;
        await PollJobStatusAsync(api, jobId, "Dispatched");

        // The agent pulls its jobs — one signed bundle for the bound instance, carrying the deploy's correlation.
        var jobs = (await agentClient.GetFromJsonAsync<List<AgentJob>>("/agent/v1/jobs"))!;
        var job = jobs.Should().ContainSingle(j => j.JobId == jobId).Subject;
        job.InstanceId.Should().Be(instanceId);
        job.Bundle.Should().Contain("[Interface]");
        job.CorrelationId.Should().Be(correlationId);

        // The bundle is signed by the org CA the agent holds — verifiable, and tamper-evident.
        SignatureValid(enrolled.CaCertificatePem, job.Bundle, job.Signature).Should().BeTrue();
        SignatureValid(enrolled.CaCertificatePem, job.Bundle + "x", job.Signature).Should().BeFalse();

        // The agent reports success (echoing the correlation back) → the job completes.
        (await agentClient.PostAsJsonAsync($"/agent/v1/jobs/{jobId}/result",
            new { status = "Succeeded", appliedConfigHash = "deadbeef", correlationId })).StatusCode.Should().Be(HttpStatusCode.NoContent);
        await PollJobStatusAsync(api, jobId, "Succeeded");

        // Telemetry: the agent reports a peer's handshake + transfer; it lands on the peer.
        (await api.PostAsJsonAsync($"/api/v1/wireguard/instances/{instanceId}/peers",
            new { name = "Dev", deviceType = "Laptop" })).EnsureSuccessStatusCode();
        var peerPublicKey = (await api.GetFromJsonAsync<List<PeerDto>>($"/api/v1/wireguard/instances/{instanceId}/peers"))!.Single().PublicKey;

        (await agentClient.PostAsJsonAsync("/agent/v1/telemetry", new
        {
            instanceId,
            peers = new[] { new { publicKey = peerPublicKey, lastHandshakeAtUtc = DateTimeOffset.UtcNow, rxBytes = 4096L, txBytes = 8192L, endpoint = "203.0.113.7:51820" } },
        })).StatusCode.Should().Be(HttpStatusCode.NoContent);

        var peer = (await api.GetFromJsonAsync<List<PeerDto>>($"/api/v1/wireguard/instances/{instanceId}/peers"))!.Single();
        peer.RxBytes.Should().Be(4096);
        peer.TxBytes.Should().Be(8192);
        peer.LastHandshakeAtUtc.Should().NotBeNull();
    }

    [Fact]
    public async Task AgentManaged_custody_delivers_a_key_less_bundle_and_adopts_the_agent_reported_key()
    {
        var api = _fixture.CreateApiClient();
        var email = await RegisterOwnerAsync(api);
        await _fixture.VerifyEmailAsync(email);

        var token = await MintTokenAsync(api);
        var (csrPem, key) = BuildCsr();
        using var noCertClient = _fixture.CreateAgentClient(null);
        var enrolled = (await (await noCertClient.PostAsJsonAsync("/agent/v1/enroll",
            new { token, csr = csrPem, name = "managed" })).Content.ReadFromJsonAsync<EnrollResponse>())!;
        using var clientCert = BuildClientCertificate(enrolled.CertificatePem, key);
        using var agentClient = _fixture.CreateAgentClient(clientCert);

        // Create an instance (WireHQ generates its key) and bind it to the agent under AgentManaged custody.
        var networkId = await CreatedId(api.PostAsJsonAsync("/api/v1/wireguard/networks",
            new { name = "ManagedNet", cidr = "10.91.0.0/24" }));
        var create = await api.PostAsJsonAsync("/api/v1/wireguard/instances",
            new { networkId, name = "ManagedGW", listenPort = 51901, interfaceAddress = "10.91.0.1/24" });
        create.StatusCode.Should().Be(HttpStatusCode.Created);
        var created = (await create.Content.ReadFromJsonAsync<CreateInstanceDto>())!;
        var (instanceId, originalPublicKey) = (created.Id, created.PublicKey);

        (await api.PutAsJsonAsync($"/api/v1/wireguard/instances/{instanceId}/target",
            new { kind = "Agent", agentId = enrolled.AgentId, keyCustody = "AgentManaged" })).StatusCode
            .Should().Be(HttpStatusCode.NoContent);

        // Until the agent reports its key, the WireHQ key is still present → "agent key pending".
        var pending = (await api.GetFromJsonAsync<TargetDto>($"/api/v1/wireguard/instances/{instanceId}/target"))!;
        pending.KeyCustody.Should().Be("AgentManaged");
        pending.AgentKeyPending.Should().BeTrue();

        var deploy = await api.PostAsync($"/api/v1/wireguard/instances/{instanceId}/deploy", null);
        var jobId = (await deploy.Content.ReadFromJsonAsync<DeployResponse>())!.JobId;
        await PollJobStatusAsync(api, jobId, "Dispatched");

        // The bundle is signed but KEY-LESS — the server private key never leaves WireHQ (there isn't one).
        var jobs = (await agentClient.GetFromJsonAsync<List<AgentJob>>("/agent/v1/jobs"))!;
        var job = jobs.Should().ContainSingle(j => j.JobId == jobId).Subject;
        job.AgentManaged.Should().BeTrue();
        job.Bundle.Should().Contain("[Interface]");
        job.Bundle.Should().NotContain("PrivateKey");
        SignatureValid(enrolled.CaCertificatePem, job.Bundle, job.Signature).Should().BeTrue();

        // The agent generates its interface key locally and reports only the public key.
        var agentPublicKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        (await agentClient.PostAsJsonAsync($"/agent/v1/jobs/{jobId}/result",
            new { status = "Succeeded", appliedConfigHash = "deadbeef", interfacePublicKey = agentPublicKey })).StatusCode
            .Should().Be(HttpStatusCode.NoContent);
        await PollJobStatusAsync(api, jobId, "Succeeded");

        // WireHQ adopted the agent's public key (replacing its own) and cleared the pending flag.
        var detail = (await api.GetFromJsonAsync<InstanceDetailDto>($"/api/v1/wireguard/instances/{instanceId}"))!;
        detail.PublicKey.Should().Be(agentPublicKey).And.NotBe(originalPublicKey);
        var settled = (await api.GetFromJsonAsync<TargetDto>($"/api/v1/wireguard/instances/{instanceId}/target"))!;
        settled.AgentKeyPending.Should().BeFalse();

        // WireHQ holds no server private key, so the full server-config export is unavailable (409).
        (await api.GetAsync($"/api/v1/wireguard/instances/{instanceId}/config")).StatusCode
            .Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Agent_reported_drift_lands_on_the_instance_and_rolls_up_to_the_agent()
    {
        var api = _fixture.CreateApiClient();
        var email = await RegisterOwnerAsync(api);
        await _fixture.VerifyEmailAsync(email);

        var token = await MintTokenAsync(api);
        var (csrPem, key) = BuildCsr();
        using var noCertClient = _fixture.CreateAgentClient(null);
        var enrolled = (await (await noCertClient.PostAsJsonAsync("/agent/v1/enroll",
            new { token, csr = csrPem, name = "drifter" })).Content.ReadFromJsonAsync<EnrollResponse>())!;
        using var clientCert = BuildClientCertificate(enrolled.CertificatePem, key);
        using var agentClient = _fixture.CreateAgentClient(clientCert);

        var networkId = await CreatedId(api.PostAsJsonAsync("/api/v1/wireguard/networks",
            new { name = "DriftNet", cidr = "10.93.0.0/24" }));
        var instanceId = await CreatedId(api.PostAsJsonAsync("/api/v1/wireguard/instances",
            new { networkId, name = "DriftGW", listenPort = 51903, interfaceAddress = "10.93.0.1/24" }));
        (await api.PutAsJsonAsync($"/api/v1/wireguard/instances/{instanceId}/target",
            new { kind = "Agent", agentId = enrolled.AgentId })).StatusCode.Should().Be(HttpStatusCode.NoContent);

        // No drift initially.
        var before = (await api.GetFromJsonAsync<TargetStatusDto>($"/api/v1/wireguard/instances/{instanceId}/target"))!;
        before.HasDrift.Should().BeFalse();

        // The agent reports the deployed config drifted from what it applied.
        (await agentClient.PostAsJsonAsync("/agent/v1/status", new
        {
            instances = new[] { new { instanceId, configHash = "tampered", drift = true } },
        })).StatusCode.Should().Be(HttpStatusCode.NoContent);

        // It lands on the instance (the Deployment-panel drift badge) and rolls up on the agent's fleet row.
        var after = (await api.GetFromJsonAsync<TargetStatusDto>($"/api/v1/wireguard/instances/{instanceId}/target"))!;
        after.HasDrift.Should().BeTrue();

        var agent = (await api.GetFromJsonAsync<List<AgentDto>>("/api/v1/agents"))!.Single(a => a.Id == enrolled.AgentId);
        agent.ManagedInstances.Should().Be(1);
        agent.InstancesWithDrift.Should().Be(1);

        // A clean report clears it again.
        (await agentClient.PostAsJsonAsync("/agent/v1/status", new
        {
            instances = new[] { new { instanceId, configHash = "clean", drift = false } },
        })).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await api.GetFromJsonAsync<TargetStatusDto>($"/api/v1/wireguard/instances/{instanceId}/target"))!
            .HasDrift.Should().BeFalse();
    }

    [Fact]
    public async Task Agent_reported_drift_with_auto_reconverge_enqueues_a_single_redeploy()
    {
        var api = _fixture.CreateApiClient();
        var email = await RegisterOwnerAsync(api);
        await _fixture.VerifyEmailAsync(email);

        var token = await MintTokenAsync(api);
        var (csrPem, key) = BuildCsr();
        using var noCertClient = _fixture.CreateAgentClient(null);
        var enrolled = (await (await noCertClient.PostAsJsonAsync("/agent/v1/enroll",
            new { token, csr = csrPem, name = "reconverger" })).Content.ReadFromJsonAsync<EnrollResponse>())!;
        using var clientCert = BuildClientCertificate(enrolled.CertificatePem, key);
        using var agentClient = _fixture.CreateAgentClient(clientCert);

        var networkId = await CreatedId(api.PostAsJsonAsync("/api/v1/wireguard/networks",
            new { name = "ReconvergeNet", cidr = "10.95.0.0/24" }));
        var instanceId = await CreatedId(api.PostAsJsonAsync("/api/v1/wireguard/instances",
            new { networkId, name = "ReconvergeGW", listenPort = 51904, interfaceAddress = "10.95.0.1/24" }));

        // Bind with auto-re-converge ON.
        (await api.PutAsJsonAsync($"/api/v1/wireguard/instances/{instanceId}/target",
            new { kind = "Agent", agentId = enrolled.AgentId, autoReconverge = true })).StatusCode.Should().Be(HttpStatusCode.NoContent);

        // The agent reports drift → WireHQ auto-enqueues a redeploy.
        (await agentClient.PostAsJsonAsync("/agent/v1/status", new
        {
            instances = new[] { new { instanceId, configHash = "tampered", drift = true } },
        })).StatusCode.Should().Be(HttpStatusCode.NoContent);

        var afterFirst = (await api.GetFromJsonAsync<List<DeploymentSummaryDto>>($"/api/v1/wireguard/instances/{instanceId}/deployments"))!;
        afterFirst.Should().HaveCount(1, because: "a drifted auto-re-converge instance gets a redeploy queued");

        // A second drift report does NOT pile up another job (no-active-job + cooldown guard).
        (await agentClient.PostAsJsonAsync("/agent/v1/status", new
        {
            instances = new[] { new { instanceId, configHash = "tampered", drift = true } },
        })).StatusCode.Should().Be(HttpStatusCode.NoContent);

        var afterSecond = (await api.GetFromJsonAsync<List<DeploymentSummaryDto>>($"/api/v1/wireguard/instances/{instanceId}/deployments"))!;
        afterSecond.Should().HaveCount(1, because: "the cooldown / no-active-job guard prevents thrashing");
    }

    [Fact]
    public async Task Agent_reported_drift_without_auto_reconverge_does_not_redeploy()
    {
        var api = _fixture.CreateApiClient();
        var email = await RegisterOwnerAsync(api);
        await _fixture.VerifyEmailAsync(email);

        var token = await MintTokenAsync(api);
        var (csrPem, key) = BuildCsr();
        using var noCertClient = _fixture.CreateAgentClient(null);
        var enrolled = (await (await noCertClient.PostAsJsonAsync("/agent/v1/enroll",
            new { token, csr = csrPem, name = "manual" })).Content.ReadFromJsonAsync<EnrollResponse>())!;
        using var clientCert = BuildClientCertificate(enrolled.CertificatePem, key);
        using var agentClient = _fixture.CreateAgentClient(clientCert);

        var networkId = await CreatedId(api.PostAsJsonAsync("/api/v1/wireguard/networks",
            new { name = "ManualNet", cidr = "10.96.0.0/24" }));
        var instanceId = await CreatedId(api.PostAsJsonAsync("/api/v1/wireguard/instances",
            new { networkId, name = "ManualGW", listenPort = 51905, interfaceAddress = "10.96.0.1/24" }));
        (await api.PutAsJsonAsync($"/api/v1/wireguard/instances/{instanceId}/target",
            new { kind = "Agent", agentId = enrolled.AgentId, autoReconverge = false })).StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await agentClient.PostAsJsonAsync("/agent/v1/status", new
        {
            instances = new[] { new { instanceId, configHash = "tampered", drift = true } },
        })).StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await api.GetFromJsonAsync<List<DeploymentSummaryDto>>($"/api/v1/wireguard/instances/{instanceId}/deployments"))!
            .Should().BeEmpty(because: "auto-re-converge is off — drift waits for a manual Deploy");
    }

    [Fact]
    public async Task Agent_posts_step_telemetry_for_its_deploy_and_ownership_is_enforced()
    {
        var api = _fixture.CreateApiClient();
        var email = await RegisterOwnerAsync(api);
        await _fixture.VerifyEmailAsync(email);

        var token = await MintTokenAsync(api);
        var (csrPem, key) = BuildCsr();
        using var noCertClient = _fixture.CreateAgentClient(null);
        var enrolled = (await (await noCertClient.PostAsJsonAsync("/agent/v1/enroll",
            new { token, csr = csrPem, name = "telemetry" })).Content.ReadFromJsonAsync<EnrollResponse>())!;
        using var clientCert = BuildClientCertificate(enrolled.CertificatePem, key);
        using var agentClient = _fixture.CreateAgentClient(clientCert);

        var networkId = await CreatedId(api.PostAsJsonAsync("/api/v1/wireguard/networks",
            new { name = "TelemetryNet", cidr = "10.97.0.0/24" }));
        var instanceId = await CreatedId(api.PostAsJsonAsync("/api/v1/wireguard/instances",
            new { networkId, name = "TelemetryGW", listenPort = 51907, interfaceAddress = "10.97.0.1/24" }));
        (await api.PutAsJsonAsync($"/api/v1/wireguard/instances/{instanceId}/target",
            new { kind = "Agent", agentId = enrolled.AgentId })).StatusCode.Should().Be(HttpStatusCode.NoContent);

        var deploy = await api.PostAsync($"/api/v1/wireguard/instances/{instanceId}/deploy", null);
        var correlationId = deploy.Headers.GetValues("X-Correlation-Id").Single();
        var jobId = (await deploy.Content.ReadFromJsonAsync<DeployResponse>())!.JobId;

        // The agent reports its structured apply steps for this deploy — accepted (best-effort telemetry plane).
        var batch = new
        {
            jobId,
            instanceId,
            correlationId,
            events = new[]
            {
                new { name = "verify", atUtc = DateTimeOffset.UtcNow, durationMs = 2.0, level = "info", outcome = "ok", message = (string?)null },
                new { name = "apply", atUtc = DateTimeOffset.UtcNow, durationMs = 41.0, level = "info", outcome = "ok", message = (string?)null },
            },
        };
        (await agentClient.PostAsJsonAsync("/agent/v1/diagnostics", batch)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        // Ownership is enforced from the cert: a job this agent does not own is rejected (404), not silently tagged.
        var foreign = new { jobId = Guid.NewGuid(), instanceId = (Guid?)null, correlationId = (string?)null, events = batch.events };
        (await agentClient.PostAsJsonAsync("/agent/v1/diagnostics", foreign)).StatusCode.Should().Be(HttpStatusCode.NotFound);

        // No client certificate → unauthorized.
        (await noCertClient.PostAsJsonAsync("/agent/v1/diagnostics", batch)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    private static async Task<string> RegisterOwnerAsync(HttpClient api)
    {
        var email = $"gw-owner+{Guid.NewGuid():N}@wirehq.test";
        const string password = "Sup3rSecret!!";
        await api.PostAsJsonAsync("/api/v1/auth/register",
            new { email, password, firstName = "GW", lastName = "Owner", acceptTerms = true });
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

    private static bool SignatureValid(string caCertificatePem, string bundle, string signatureBase64)
    {
        using var caCertificate = X509Certificate2.CreateFromPem(caCertificatePem);
        using var publicKey = caCertificate.GetECDsaPublicKey()!;
        return publicKey.VerifyData(
            System.Text.Encoding.UTF8.GetBytes(bundle), Convert.FromBase64String(signatureBase64), HashAlgorithmName.SHA384);
    }

    private static async Task<string> MintTokenAsync(HttpClient api)
    {
        var response = await api.PostAsJsonAsync("/api/v1/agents/enroll-tokens", new { });
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<MintResponse>())!.Token;
    }

    private static (string CsrPem, ECDsa Key) BuildCsr()
    {
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var csr = new CertificateRequest("CN=agent", key, HashAlgorithmName.SHA256).CreateSigningRequestPem();
        return (csr, key);
    }

    private static X509Certificate2 BuildClientCertificate(string certificatePem, ECDsa key)
    {
        using (key)
        using (var publicOnly = X509Certificate2.CreateFromPem(certificatePem))
        using (var withKey = publicOnly.CopyWithPrivateKey(key))
        {
            // Round-trip through PKCS#12 so the key is usable in the TLS client handshake across platforms.
            return X509CertificateLoader.LoadPkcs12(withKey.Export(X509ContentType.Pfx), null);
        }
    }

    private sealed record EnrollResponse(Guid AgentId, string CertificatePem, string CaCertificatePem, DateTimeOffset NotAfterUtc);
    private sealed record MintResponse(Guid Id, string Token, DateTimeOffset ExpiresAtUtc);
    private sealed record LoginResponse(string AccessToken);
    private sealed record AgentDto(Guid Id, string Name, string Status, string? Platform, string? Version, int ManagedInstances, int InstancesWithDrift);
    private sealed record TargetStatusDto(bool HasDrift);
    private sealed record DeploymentSummaryDto(Guid Id, string Status);
    private sealed record IdResponse(Guid Id);
    private sealed record DeployResponse(Guid JobId, string Status);
    private sealed record JobStatusDto(string Status);
    private sealed record AgentJob(Guid JobId, Guid InstanceId, string InterfaceName, string Bundle, string Signature, bool AgentManaged, string? CorrelationId);
    private sealed record PeerDto(Guid Id, string PublicKey, DateTimeOffset? LastHandshakeAtUtc, long RxBytes, long TxBytes);
    private sealed record CreateInstanceDto(Guid Id, string Slug, string PublicKey, int ListenPort);
    private sealed record InstanceDetailDto(string PublicKey);
    private sealed record TargetDto(string Kind, string KeyCustody, bool AgentKeyPending);
}
