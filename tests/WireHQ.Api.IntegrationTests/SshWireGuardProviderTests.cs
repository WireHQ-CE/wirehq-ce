using FluentAssertions;
using WireHQ.Modules.Orchestration.Providers.Ssh;
using WireHQ.Modules.WireGuard.Providers;
using WireHQ.Shared.Results;
using Xunit;

namespace WireHQ.Api.IntegrationTests;

/// <summary>
/// Unit tests for the SSH provider's deploy orchestration (backup → write → apply → verify → rollback),
/// driven by a fake SSH session so the command sequence + failure handling are covered without a live
/// host. (Real SSH.NET transport is covered by the dispatcher routing test.) (docs/12 §6)
/// </summary>
public sealed class SshWireGuardProviderTests
{
    private static readonly ProviderInstanceRef Ref = new(Guid.NewGuid(), null, new Dictionary<string, string>
    {
        ["host"] = "vpn.test",
        ["port"] = "22",
        ["username"] = "deploy",
        ["authKind"] = "Password",
        ["credential"] = "secret",
    });

    private static readonly RenderedServerConfig Config = new("wg0", "[Interface]\nPrivateKey = x\n");

    // A representative `wg show wg0 dump`: interface line, a connected peer, an idle peer, a junk line.
    private const string SampleDump =
        "PRIVKEY\tSRVPUBKEY\t51820\toff\n" +
        "peerAAA=\t(none)\t203.0.113.5:51820\t10.0.0.2/32\t1718200000\t1024\t2048\t25\n" +
        "peerBBB=\t(none)\t(none)\t10.0.0.3/32\t0\t0\t0\toff\n" +
        "garbage-short-line\n";

    [Fact]
    public async Task Deploy_succeeds_and_does_not_roll_back_when_every_step_passes()
    {
        var session = new FakeSshConnection(_ => Ok("ok"));
        var provider = new SshWireGuardProvider(new FakeFactory(session));

        var result = await provider.DeployConfigAsync(Ref, Config, default);

        result.IsSuccess.Should().BeTrue();
        session.Commands.Should().Contain(c => c.Contains("base64 -d") && c.Contains("tee /etc/wireguard/wg0.conf"));
        session.Commands.Should().Contain(c => c.Contains("systemctl restart wg-quick@wg0"));
        session.Commands.Should().Contain(c => c.Contains("wg show wg0"));
        // No rollback restore (the `if [ -f …bak ]` conditional) on the happy path.
        session.Commands.Should().NotContain(c => c.Contains("if [ -f"));
    }

    [Fact]
    public async Task Deploy_rolls_back_when_apply_fails()
    {
        var session = new FakeSshConnection(cmd => cmd.Contains("systemctl restart") ? Fail() : Ok("ok"));
        var provider = new SshWireGuardProvider(new FakeFactory(session));

        var result = await provider.DeployConfigAsync(Ref, Config, default);

        result.IsFailure.Should().BeTrue();
        result.Error.Description.Should().Contain("rolled back");
        session.Commands.Should().Contain(c => c.Contains("if [ -f"));
    }

    [Fact]
    public async Task Deploy_rolls_back_when_verification_fails()
    {
        var session = new FakeSshConnection(cmd => cmd.Contains("wg show") ? Ok(string.Empty) : Ok("ok"));
        var provider = new SshWireGuardProvider(new FakeFactory(session));

        var result = await provider.DeployConfigAsync(Ref, Config, default);

        result.IsFailure.Should().BeTrue();
        result.Error.Description.Should().Contain("verification");
        session.Commands.Should().Contain(c => c.Contains("if [ -f"));
    }

    [Fact]
    public async Task Deploy_fails_when_the_connection_fails()
    {
        var provider = new SshWireGuardProvider(new FakeFactory(Error.Failure("orch.ssh.connect_failed", "boom")));

        var result = await provider.DeployConfigAsync(Ref, Config, default);

        result.IsFailure.Should().BeTrue();
    }

    [Fact]
    public async Task TestConnectivity_reports_wireguard_present_and_absent()
    {
        var present = new SshWireGuardProvider(new FakeFactory(new FakeSshConnection(_ => Ok("ok"))));
        (await present.TestConnectivityAsync(Ref, default)).IsSuccess.Should().BeTrue();

        var absent = new SshWireGuardProvider(new FakeFactory(new FakeSshConnection(_ => Ok(string.Empty))));
        (await absent.TestConnectivityAsync(Ref, default)).IsFailure.Should().BeTrue();
    }

    [Fact]
    public async Task GetInstanceStatus_parses_wg_show_dump_into_state_and_peer_telemetry()
    {
        var session = new FakeSshConnection(cmd => cmd.Contains("wg show wg0 dump") ? Ok(SampleDump) : Ok("ok"));
        var provider = new SshWireGuardProvider(new FakeFactory(session));

        var result = await provider.GetInstanceStatusAsync(Ref, default);

        result.IsSuccess.Should().BeTrue();
        result.Value.State.Should().Be(ProviderInstanceState.Running);
        result.Value.ListenPort.Should().Be(51820);
        result.Value.Peers.Should().HaveCount(2, because: "the interface line and the junk line are not peers");

        var connected = result.Value.Peers.Single(p => p.PublicKey == "peerAAA=");
        connected.LastHandshakeAt.Should().Be(DateTimeOffset.FromUnixTimeSeconds(1718200000));
        connected.RxBytes.Should().Be(1024);
        connected.TxBytes.Should().Be(2048);
        connected.Endpoint.Should().Be("203.0.113.5:51820");

        var idle = result.Value.Peers.Single(p => p.PublicKey == "peerBBB=");
        idle.LastHandshakeAt.Should().BeNull(because: "a 0 handshake means never");
        idle.Endpoint.Should().BeNull(because: "(none) is the no-endpoint sentinel");
        idle.RxBytes.Should().Be(0);
    }

    [Fact]
    public async Task GetInstanceStatus_reports_stopped_when_the_interface_is_down()
    {
        var session = new FakeSshConnection(_ => Fail());
        var provider = new SshWireGuardProvider(new FakeFactory(session));

        var result = await provider.GetInstanceStatusAsync(Ref, default);

        result.IsSuccess.Should().BeTrue();
        result.Value.State.Should().Be(ProviderInstanceState.Stopped);
        result.Value.Peers.Should().BeEmpty();
    }

    [Fact]
    public async Task GetInstanceStatus_fails_when_the_connection_fails()
    {
        var provider = new SshWireGuardProvider(new FakeFactory(Error.Failure("orch.ssh.connect_failed", "boom")));

        (await provider.GetInstanceStatusAsync(Ref, default)).IsFailure.Should().BeTrue();
    }

    [Fact]
    public async Task GetConfigDrift_reports_no_drift_when_the_deployed_config_matches_desired()
    {
        // The host returns exactly the desired config (with a cosmetic trailing newline → normalized away).
        var session = new FakeSshConnection(cmd => cmd.Contains("cat /etc/wireguard/wg0.conf") ? Ok(Config.ConfigText + "\n") : Ok("ok"));
        var provider = new SshWireGuardProvider(new FakeFactory(session));

        var result = await provider.GetConfigDriftAsync(Ref, Config, default);

        result.IsSuccess.Should().BeTrue();
        result.Value.HasDrift.Should().BeFalse();
        result.Value.DesiredHash.Should().Be(result.Value.ActualHash);
    }

    [Fact]
    public async Task GetConfigDrift_reports_drift_when_the_deployed_config_differs()
    {
        var session = new FakeSshConnection(cmd => cmd.Contains("cat /etc/wireguard/wg0.conf") ? Ok("[Interface]\nPrivateKey = something-else\n") : Ok("ok"));
        var provider = new SshWireGuardProvider(new FakeFactory(session));

        var result = await provider.GetConfigDriftAsync(Ref, Config, default);

        result.Value.HasDrift.Should().BeTrue();
        result.Value.DesiredHash.Should().NotBe(result.Value.ActualHash);
    }

    [Fact]
    public async Task GetConfigDrift_reports_drift_when_no_config_is_deployed()
    {
        var session = new FakeSshConnection(_ => Ok(string.Empty));
        var provider = new SshWireGuardProvider(new FakeFactory(session));

        var result = await provider.GetConfigDriftAsync(Ref, Config, default);

        result.Value.HasDrift.Should().BeTrue();
        result.Value.ActualHash.Should().BeNull();
    }

    private static SshCommandResult Ok(string output) => new(0, output, string.Empty);

    private static SshCommandResult Fail() => new(1, string.Empty, "error");

    private sealed class FakeSshConnection(Func<string, SshCommandResult> responder) : ISshConnection
    {
        public List<string> Commands { get; } = [];

        public string HostKeyFingerprint => "SHA256:fake";

        public Task<SshCommandResult> RunAsync(string command, CancellationToken cancellationToken)
        {
            Commands.Add(command);
            return Task.FromResult(responder(command));
        }

        public void Dispose()
        {
        }
    }

    private sealed class FakeFactory : ISshConnectionFactory
    {
        private readonly Result<ISshConnection> _result;

        public FakeFactory(ISshConnection connection) => _result = Result.Success(connection);

        public FakeFactory(Error error) => _result = error;

        public Task<Result<ISshConnection>> ConnectAsync(SshConnectionInfo info, CancellationToken cancellationToken) =>
            Task.FromResult(_result);
    }
}
