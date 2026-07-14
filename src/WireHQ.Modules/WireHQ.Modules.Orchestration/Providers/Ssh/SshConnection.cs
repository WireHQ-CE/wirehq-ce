using System.Text;
using Microsoft.Extensions.Logging;
using Renci.SshNet;
using WireHQ.Modules.Orchestration.Domain;
using WireHQ.Shared.Results;

namespace WireHQ.Modules.Orchestration.Providers.Ssh;

/// <summary>SSH.NET-backed session. Runs commands; the provider uploads files via <c>base64 | tee</c>.</summary>
public sealed class SshConnection(SshClient client, string hostKeyFingerprint) : ISshConnection
{
    public string HostKeyFingerprint => hostKeyFingerprint;

    public Task<SshCommandResult> RunAsync(string command, CancellationToken cancellationToken)
    {
        using var cmd = client.CreateCommand(command);
        cmd.Execute();
        return Task.FromResult(new SshCommandResult(cmd.ExitStatus ?? -1, cmd.Result ?? string.Empty, cmd.Error ?? string.Empty));
    }

    public void Dispose()
    {
        if (client.IsConnected)
        {
            client.Disconnect();
        }

        client.Dispose();
    }
}

/// <summary>
/// Opens real SSH sessions with SSH.NET. Verifies the pinned host-key fingerprint (refusing a
/// mismatch); when no fingerprint is pinned it trusts on first use and records the observed one for the
/// operator to pin (a documented MVP posture). (docs/12-remote-orchestration.md §6/§9)
/// </summary>
public sealed class SshConnectionFactory(ILogger<SshConnectionFactory> logger) : ISshConnectionFactory
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(15);

    public Task<Result<ISshConnection>> ConnectAsync(SshConnectionInfo info, CancellationToken cancellationToken)
    {
        try
        {
            var auth = BuildAuth(info);
            var connectionInfo = new ConnectionInfo(info.Host, info.Port, info.Username, auth)
            {
                Timeout = ConnectTimeout,
            };

            var client = new SshClient(connectionInfo);
            var observedFingerprint = string.Empty;
            var trusted = true;

            client.HostKeyReceived += (_, e) =>
            {
                observedFingerprint = "SHA256:" + e.FingerPrintSHA256;
                if (!string.IsNullOrWhiteSpace(info.PinnedHostKeyFingerprint))
                {
                    trusted = FingerprintsMatch(info.PinnedHostKeyFingerprint, observedFingerprint);
                    e.CanTrust = trusted;
                }
                else
                {
                    logger.LogWarning("SSH host key for {Host} not pinned; trusting on first use ({Fingerprint}).", info.Host, observedFingerprint);
                }
            };

            client.Connect();

            if (!trusted)
            {
                client.Dispose();
                return Task.FromResult<Result<ISshConnection>>(OrchestrationErrors.Ssh.HostKeyMismatch);
            }

            return Task.FromResult<Result<ISshConnection>>(new SshConnection(client, observedFingerprint));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "SSH connect to {Host}:{Port} failed", info.Host, info.Port);
            return Task.FromResult<Result<ISshConnection>>(OrchestrationErrors.Ssh.ConnectFailed(ex.Message));
        }
    }

    private static AuthenticationMethod BuildAuth(SshConnectionInfo info)
    {
        if (info.AuthKind == SshAuthKind.PrivateKey)
        {
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(info.Credential));
            return new PrivateKeyAuthenticationMethod(info.Username, new PrivateKeyFile(stream));
        }

        return new PasswordAuthenticationMethod(info.Username, info.Credential);
    }

    private static bool FingerprintsMatch(string pinned, string observed) =>
        string.Equals(Normalize(pinned), Normalize(observed), StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string fingerprint) =>
        fingerprint.Trim().Replace("SHA256:", string.Empty, StringComparison.OrdinalIgnoreCase).TrimEnd('=');
}
