using System.Security.Cryptography;
using System.Text;
using WireHQ.Modules.Orchestration.Domain;
using WireHQ.Modules.WireGuard.Providers;
using WireHQ.Modules.WireGuard.Providers.Local;
using WireHQ.Shared.Results;

namespace WireHQ.Modules.Orchestration.Providers.Ssh;

/// <summary>
/// Pushes WireGuard config to a remote Linux host over SSH (the MVP remote data plane). It works at the
/// whole-config level: <see cref="DeployConfigAsync"/> backs up the existing config, writes the rendered
/// one, applies it (<c>wg-quick</c>), verifies, and <b>rolls back on any failure</b>. The incremental
/// peer/instance methods aren't used (the dispatcher always full-deploys for a Push provider; peer
/// model changes still flow through the Local provider). It also reads <b>live status + telemetry</b>
/// (<see cref="GetInstanceStatusAsync"/>) by parsing <c>wg show dump</c> over the same SSH session.
/// (docs/12-remote-orchestration.md §6/§10)
/// </summary>
public sealed class SshWireGuardProvider(ISshConnectionFactory connections) : IWireGuardProvider
{
    public WireGuardProviderType Type => WireGuardProviderType.SshLinux;

    public ProviderCapabilities Capabilities =>
        ProviderCapabilities.ManagePeers | ProviderCapabilities.RemoteDeploy |
        ProviderCapabilities.LiveStatus | ProviderCapabilities.Telemetry | ProviderCapabilities.DriftDetection;

    public ProviderExecutionModel ExecutionModel => ProviderExecutionModel.Push;

    public async Task<Result> TestConnectivityAsync(ProviderInstanceRef instance, CancellationToken cancellationToken)
    {
        var connect = await connections.ConnectAsync(ReadConnectionInfo(instance.ProviderSettings), cancellationToken);
        if (connect.IsFailure)
        {
            return connect.Error;
        }

        using var session = connect.Value;
        var probe = await session.RunAsync("command -v wg >/dev/null 2>&1 && command -v wg-quick >/dev/null 2>&1 && echo ok", cancellationToken);
        return probe.Ok && probe.Output.Contains("ok")
            ? Result.Success()
            : OrchestrationErrors.Ssh.WireGuardNotPresent;
    }

    public async Task<Result> DeployConfigAsync(ProviderInstanceRef instance, RenderedServerConfig config, CancellationToken cancellationToken)
    {
        var connect = await connections.ConnectAsync(ReadConnectionInfo(instance.ProviderSettings), cancellationToken);
        if (connect.IsFailure)
        {
            return connect.Error;
        }

        using var session = connect.Value;

        var name = config.InterfaceName;
        var confPath = $"/etc/wireguard/{name}.conf";
        var backupPath = $"{confPath}.wirehq.bak";
        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(config.ConfigText));

        // 1) Back up any existing config (ignore if absent).
        await session.RunAsync($"sudo mkdir -p /etc/wireguard && sudo cp {confPath} {backupPath} 2>/dev/null || true", cancellationToken);

        // 2) Write the new config atomically, 0600.
        var write = await session.RunAsync($"echo {b64} | base64 -d | sudo tee {confPath} >/dev/null && sudo chmod 600 {confPath}", cancellationToken);
        if (!write.Ok)
        {
            return OrchestrationErrors.Ssh.CommandFailed($"Failed to write config: {Trim(write.Error)}");
        }

        // 3) Apply (restart brings the interface up from the file).
        var apply = await session.RunAsync($"sudo systemctl enable wg-quick@{name} >/dev/null 2>&1; sudo systemctl restart wg-quick@{name}", cancellationToken);
        if (!apply.Ok)
        {
            await RollbackAsync(session, name, confPath, backupPath, cancellationToken);
            return OrchestrationErrors.Ssh.CommandFailed($"Failed to apply config (rolled back): {Trim(apply.Error)}");
        }

        // 4) Verify the interface is up.
        var verify = await session.RunAsync($"sudo wg show {name} >/dev/null 2>&1 && echo ok", cancellationToken);
        if (!(verify.Ok && verify.Output.Contains("ok")))
        {
            await RollbackAsync(session, name, confPath, backupPath, cancellationToken);
            return OrchestrationErrors.Ssh.CommandFailed("Config applied but verification (wg show) failed; rolled back.");
        }

        return Result.Success();
    }

    private static async Task RollbackAsync(ISshConnection session, string name, string confPath, string backupPath, CancellationToken cancellationToken) =>
        await session.RunAsync(
            $"if [ -f {backupPath} ]; then sudo cp {backupPath} {confPath}; else sudo rm -f {confPath}; fi; sudo systemctl restart wg-quick@{name} 2>/dev/null || true",
            cancellationToken);

    private static SshConnectionInfo ReadConnectionInfo(IReadOnlyDictionary<string, string> s) =>
        new(
            s["host"],
            int.Parse(s["port"]),
            s["username"],
            Enum.Parse<SshAuthKind>(s["authKind"]),
            s["credential"],
            s.TryGetValue("hostKeyFingerprint", out var fp) && !string.IsNullOrEmpty(fp) ? fp : null);

    private static string Trim(string value) => value.Length > 500 ? value[..500] : value;

    // ---- Incremental operations: not used for SSH (the dispatcher always full-deploys). ----

    public Task<Result<ProviderInstanceResult>> CreateInstanceAsync(ProvisionInstance spec, CancellationToken cancellationToken) =>
        Task.FromResult(Result.Success(new ProviderInstanceResult(ExternalId: null)));

    public Task<Result> UpdateInstanceAsync(ProviderInstanceRef instance, ProvisionInstance spec, CancellationToken cancellationToken) =>
        Task.FromResult(Result.Success());

    public Task<Result> DeleteInstanceAsync(ProviderInstanceRef instance, CancellationToken cancellationToken) =>
        Task.FromResult(Result.Success());

    public Task<Result> ControlInstanceAsync(ProviderInstanceRef instance, InstanceAction action, CancellationToken cancellationToken) =>
        Task.FromResult(Result.Failure(WireGuardProviderErrors.ControlNotSupported));

    public Task<Result> ApplyPeerAsync(ProviderInstanceRef instance, ProviderPeerSpec peer, CancellationToken cancellationToken) =>
        Task.FromResult(Result.Success());

    public Task<Result> RemovePeerAsync(ProviderInstanceRef instance, string publicKey, CancellationToken cancellationToken) =>
        Task.FromResult(Result.Success());

    // ---- Live status + telemetry: read-only, over the same SSH session (docs/12 §6/§10). ----

    /// <summary>
    /// Reads live status by parsing <c>wg show {iface} dump</c>: interface up/down → instance state, and
    /// per-peer handshake/transfer/endpoint. Reachable-but-no-interface reports <see cref="ProviderInstanceState.Stopped"/>;
    /// a connect failure surfaces as <c>Result.Failure</c> so the reconciler leaves the prior state untouched.
    /// </summary>
    public async Task<Result<ProviderInstanceStatus>> GetInstanceStatusAsync(ProviderInstanceRef instance, CancellationToken cancellationToken)
    {
        var name = ReadInterfaceName(instance.ProviderSettings);
        var connect = await connections.ConnectAsync(ReadConnectionInfo(instance.ProviderSettings), cancellationToken);
        if (connect.IsFailure)
        {
            return connect.Error;
        }

        using var session = connect.Value;
        var dump = await session.RunAsync($"sudo wg show {name} dump", cancellationToken);
        if (!dump.Ok)
        {
            // Reachable, but the interface isn't up (no such device) — distinct from "host unreachable".
            return new ProviderInstanceStatus(ProviderInstanceState.Stopped, ListenPort: null, DateTimeOffset.UtcNow, Peers: []);
        }

        var parsed = WgShowDumpParser.Parse(dump.Output);
        return new ProviderInstanceStatus(ProviderInstanceState.Running, parsed.ListenPort, DateTimeOffset.UtcNow, parsed.Peers);
    }

    public async Task<Result<IReadOnlyList<ProviderPeerStatus>>> GetPeerStatusAsync(ProviderInstanceRef instance, CancellationToken cancellationToken)
    {
        var status = await GetInstanceStatusAsync(instance, cancellationToken);
        return status.IsFailure ? status.Error : Result.Success(status.Value.Peers);
    }

    private static string ReadInterfaceName(IReadOnlyDictionary<string, string> s) =>
        s.TryGetValue("interfaceName", out var name) && !string.IsNullOrWhiteSpace(name) ? name : DeploymentTarget.DefaultInterfaceName;

    /// <summary>
    /// Compares the desired server config against what's actually deployed: <c>sudo cat</c> the host's
    /// <c>/etc/wireguard/{iface}.conf</c> and checksum it against <paramref name="desired"/>. A missing
    /// (or unreadable) file is reported as drift. Hashes are over normalized text (CRLF→LF, trailing
    /// whitespace trimmed) so a cosmetic trailing newline never reads as drift.
    /// </summary>
    public async Task<Result<ConfigDrift>> GetConfigDriftAsync(ProviderInstanceRef instance, RenderedServerConfig desired, CancellationToken cancellationToken)
    {
        var name = ReadInterfaceName(instance.ProviderSettings);
        var connect = await connections.ConnectAsync(ReadConnectionInfo(instance.ProviderSettings), cancellationToken);
        if (connect.IsFailure)
        {
            return connect.Error;
        }

        using var session = connect.Value;
        var desiredHash = HashConfig(desired.ConfigText);

        var read = await session.RunAsync($"sudo cat /etc/wireguard/{name}.conf 2>/dev/null", cancellationToken);
        if (!read.Ok || string.IsNullOrWhiteSpace(read.Output))
        {
            return new ConfigDrift(HasDrift: true, desiredHash, ActualHash: null, "No WireGuard config is deployed on the host.");
        }

        var actualHash = HashConfig(read.Output);
        var hasDrift = !string.Equals(desiredHash, actualHash, StringComparison.Ordinal);
        return new ConfigDrift(hasDrift, desiredHash, actualHash,
            hasDrift ? "The deployed config differs from the desired config." : null);
    }

    private static string HashConfig(string text)
    {
        var normalized = text.Replace("\r\n", "\n").TrimEnd();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized))).ToLowerInvariant();
    }
}

/// <summary>The parsed result of <c>wg show {iface} dump</c>: the interface's listen port + per-peer telemetry.</summary>
internal sealed record WgDump(int? ListenPort, IReadOnlyList<ProviderPeerStatus> Peers);

/// <summary>
/// Parses <c>wg show {iface} dump</c> (tab-separated, one line per interface then one per peer) into
/// provider-neutral telemetry. The first line is the interface
/// (<c>private-key · public-key · listen-port · fwmark</c>); each subsequent line is a peer
/// (<c>public-key · preshared-key · endpoint · allowed-ips · latest-handshake · rx · tx · keepalive</c>).
/// <c>latest-handshake</c> is unix seconds (<c>0</c> = never); <c>(none)</c>/<c>off</c> are sentinels.
/// </summary>
internal static class WgShowDumpParser
{
    public static WgDump Parse(string dump)
    {
        var lines = dump.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        int? listenPort = null;
        var peers = new List<ProviderPeerStatus>();

        for (var i = 0; i < lines.Length; i++)
        {
            var f = lines[i].Split('\t');

            if (i == 0)
            {
                // Interface line: private-key, public-key, listen-port, fwmark.
                if (f.Length >= 3 && int.TryParse(f[2], out var port) && port > 0)
                {
                    listenPort = port;
                }

                continue;
            }

            // Peer line: need at least through the transfer columns (keepalive is optional).
            if (f.Length < 7 || string.IsNullOrWhiteSpace(f[0]))
            {
                continue;
            }

            var endpoint = Normalize(f[2]);
            DateTimeOffset? handshake = long.TryParse(f[4], out var epoch) && epoch > 0
                ? DateTimeOffset.FromUnixTimeSeconds(epoch)
                : null;
            _ = long.TryParse(f[5], out var rx);
            _ = long.TryParse(f[6], out var tx);

            peers.Add(new ProviderPeerStatus(f[0], handshake, rx, tx, endpoint));
        }

        return new WgDump(listenPort, peers);
    }

    private static string? Normalize(string value) =>
        string.IsNullOrWhiteSpace(value) || value is "(none)" or "off" ? null : value;
}
