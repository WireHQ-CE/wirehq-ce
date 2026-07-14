using WireHQ.Modules.Orchestration.Domain;
using WireHQ.Shared.Results;

namespace WireHQ.Modules.Orchestration.Providers.Ssh;

/// <summary>Everything needed to open one SSH session, with the credential already decrypted in memory.</summary>
public sealed record SshConnectionInfo(
    string Host,
    int Port,
    string Username,
    SshAuthKind AuthKind,
    string Credential,
    string? PinnedHostKeyFingerprint);

/// <summary>The outcome of a remote command.</summary>
public sealed record SshCommandResult(int ExitCode, string Output, string Error)
{
    public bool Ok => ExitCode == 0;
}

/// <summary>
/// A live SSH session. Abstracted so the provider's deploy orchestration (backup → upload → apply →
/// verify → rollback) is unit-testable with a fake, while the real transport is SSH.NET.
/// </summary>
public interface ISshConnection : IDisposable
{
    /// <summary>The host-key fingerprint observed on connect (<c>SHA256:…</c>) — for pinning/discovery.</summary>
    string HostKeyFingerprint { get; }

    Task<SshCommandResult> RunAsync(string command, CancellationToken cancellationToken);
}

/// <summary>Opens SSH sessions, verifying the pinned host key (or recording it on first contact).</summary>
public interface ISshConnectionFactory
{
    Task<Result<ISshConnection>> ConnectAsync(SshConnectionInfo info, CancellationToken cancellationToken);
}
