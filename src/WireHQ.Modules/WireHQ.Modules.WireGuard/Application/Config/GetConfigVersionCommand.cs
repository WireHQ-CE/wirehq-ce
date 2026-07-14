using Microsoft.EntityFrameworkCore;
using WireHQ.Application.Abstractions;
using WireHQ.Application.Abstractions.Persistence;
using WireHQ.Application.Abstractions.Security;
using WireHQ.Application.Common.Messaging;
using WireHQ.Domain.Auditing;
using WireHQ.Modules.WireGuard.Authorization;
using WireHQ.Modules.WireGuard.Domain;
using WireHQ.Shared.Results;

namespace WireHQ.Modules.WireGuard.Application.Config;

/// <summary>
/// Reveals one historical config version's plaintext (decrypts it). A command (not a query) because
/// revealing stored config is security-sensitive and must be audited — the audit entry only persists
/// inside the command transaction. (mirrors GeneratePeerConfigCommand; docs/11 §5/§6)
/// </summary>
public sealed record GetConfigVersionCommand(ConfigTargetType TargetType, Guid TargetId, int Version)
    : ICommand<ConfigVersionContent>, IAuthorizedRequest
{
    public IReadOnlyCollection<string> RequiredPermissions =>
        TargetType == ConfigTargetType.Peer ? [WireGuardPermissions.Peers.Export] : [WireGuardPermissions.Instances.Export];
}

public sealed record ConfigVersionContent(int Version, string Format, string Content, string Checksum, DateTimeOffset CreatedAtUtc);

public sealed class GetConfigVersionCommandHandler(
    IApplicationDbContext dbContext,
    ISecretProtector secretProtector,
    IAuditWriter audit)
    : ICommandHandler<GetConfigVersionCommand, ConfigVersionContent>
{
    public async Task<Result<ConfigVersionContent>> Handle(GetConfigVersionCommand command, CancellationToken cancellationToken)
    {
        var version = await dbContext.Set<ConfigVersion>()
            .FirstOrDefaultAsync(
                c => c.TargetType == command.TargetType && c.TargetId == command.TargetId && c.Version == command.Version,
                cancellationToken);

        if (version is null)
        {
            return WireGuardErrors.Config.VersionNotFound;
        }

        var content = secretProtector.Unprotect(version.ContentEncrypted);

        audit.Record("wg.config.version_revealed", AuditOutcome.Success, nameof(ConfigVersion), version.Id.ToString(),
            new { command.TargetType, command.TargetId, command.Version });

        return new ConfigVersionContent(version.Version, version.Format, content, version.Checksum, version.CreatedAtUtc);
    }
}
