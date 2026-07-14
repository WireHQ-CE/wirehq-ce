using Microsoft.EntityFrameworkCore;
using WireHQ.Application.Abstractions;
using WireHQ.Application.Abstractions.Persistence;
using WireHQ.Application.Abstractions.Security;
using WireHQ.Modules.WireGuard.Domain;

namespace WireHQ.Modules.WireGuard.Services;

/// <summary>
/// Default <see cref="IConfigVersionWriter"/>: assigns the next monotonic version per target,
/// SHA-256 checksums the plaintext, and stores the content encrypted via <see cref="ISecretProtector"/>.
/// Tenant and author come from the ambient request context. No SaveChanges — the UnitOfWork behavior
/// persists the appended row atomically with the calling command.
/// </summary>
public sealed class ConfigVersionWriter(
    IApplicationDbContext dbContext,
    ISecretProtector secretProtector,
    IConfigurationService configuration,
    ITenantContext tenant,
    ICurrentUser currentUser) : IConfigVersionWriter
{
    public async Task<int> WriteAsync(ConfigTargetType targetType, Guid targetId, string plaintextConfig, string? note, CancellationToken cancellationToken)
    {
        if (tenant.OrganizationId is not { } organizationId)
        {
            // Config versions are only written from already tenant-scoped, authorized commands.
            throw new InvalidOperationException("Cannot write a config version without an active organization.");
        }

        // Tenant query filter scopes this to the active org; TargetId is globally unique regardless.
        var currentMax = await dbContext.Set<ConfigVersion>()
            .Where(c => c.TargetType == targetType && c.TargetId == targetId)
            .MaxAsync(c => (int?)c.Version, cancellationToken) ?? 0;

        var version = ConfigVersion.Create(
            organizationId,
            targetType,
            targetId,
            currentMax + 1,
            secretProtector.Protect(plaintextConfig),
            configuration.Checksum(plaintextConfig),
            currentUser.UserId,
            note);

        dbContext.Set<ConfigVersion>().Add(version);
        return version.Version;
    }
}
