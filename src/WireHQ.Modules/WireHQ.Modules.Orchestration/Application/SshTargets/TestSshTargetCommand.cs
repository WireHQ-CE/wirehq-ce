using Microsoft.EntityFrameworkCore;
using WireHQ.Application.Abstractions;
using WireHQ.Application.Abstractions.Persistence;
using WireHQ.Application.Abstractions.Security;
using WireHQ.Application.Common.Messaging;
using WireHQ.Domain.Auditing;
using WireHQ.Modules.Orchestration.Authorization;
using WireHQ.Modules.Orchestration.Domain;
using WireHQ.Modules.Orchestration.Providers.Ssh;
using WireHQ.Shared.Results;

namespace WireHQ.Modules.Orchestration.Application.SshTargets;

/// <summary>
/// Probes an SSH target: connects, reports reachability, whether wireguard-tools are present, and the
/// observed host-key fingerprint (so the operator can pin it). Audited (an outbound connection).
/// (docs/12-remote-orchestration.md §6/§8)
/// </summary>
public sealed record TestSshTargetCommand(Guid Id) : ICommand<SshTargetTestResult>, IAuthorizedRequest
{
    public IReadOnlyCollection<string> RequiredPermissions => [OrchestrationPermissions.Targets.Manage];
}

public sealed record SshTargetTestResult(bool Reachable, bool WireGuardPresent, string? HostKeyFingerprint, string? Error);

public sealed class TestSshTargetCommandHandler(
    IApplicationDbContext dbContext,
    ISecretProtector secretProtector,
    ISshConnectionFactory connections,
    IAuditWriter audit)
    : ICommandHandler<TestSshTargetCommand, SshTargetTestResult>
{
    public async Task<Result<SshTargetTestResult>> Handle(TestSshTargetCommand command, CancellationToken cancellationToken)
    {
        var target = await dbContext.Set<SshTarget>().FirstOrDefaultAsync(t => t.Id == command.Id, cancellationToken);
        if (target is null)
        {
            return OrchestrationErrors.SshTarget.NotFound;
        }

        var info = new SshConnectionInfo(
            target.Host, target.Port, target.Username, target.AuthKind,
            secretProtector.Unprotect(target.CredentialCiphertext), target.HostKeyFingerprint);

        var connect = await connections.ConnectAsync(info, cancellationToken);
        if (connect.IsFailure)
        {
            audit.Record("orch.ssh_target.tested", AuditOutcome.Failure, nameof(SshTarget), target.Id.ToString());
            return new SshTargetTestResult(Reachable: false, WireGuardPresent: false, HostKeyFingerprint: null, Error: connect.Error.Description);
        }

        using var session = connect.Value;
        var probe = await session.RunAsync("command -v wg >/dev/null 2>&1 && command -v wg-quick >/dev/null 2>&1 && echo ok", cancellationToken);
        var wireGuardPresent = probe.Ok && probe.Output.Contains("ok");

        audit.Record("orch.ssh_target.tested", AuditOutcome.Success, nameof(SshTarget), target.Id.ToString(),
            new { reachable = true, wireGuardPresent });

        return new SshTargetTestResult(Reachable: true, wireGuardPresent, session.HostKeyFingerprint, Error: null);
    }
}
