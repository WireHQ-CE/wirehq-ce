using Microsoft.EntityFrameworkCore;
using WireHQ.Application.Abstractions.Persistence;
using WireHQ.Modules.WireGuard.Domain;
using WireHQ.Shared.Results;

namespace WireHQ.Modules.WireGuard.Application.Enrollment;

/// <summary>
/// The shared data an enrollment needs: the target instance + its network, plus the set of emails and
/// address hosts already taken on that instance (for duplicate detection). Loaded identically by the
/// preview and the execute so they dedup against the same baseline.
/// </summary>
public sealed record EnrollmentContext(
    WireGuardInstance Instance,
    WireGuardNetwork Network,
    IReadOnlyList<string> ExistingEmails,
    IReadOnlyList<string> ExistingAddressHosts)
{
    public static async Task<Result<EnrollmentContext>> LoadAsync(
        IApplicationDbContext dbContext, Guid instanceId, CancellationToken cancellationToken)
    {
        var instance = await dbContext.Set<WireGuardInstance>()
            .FirstOrDefaultAsync(i => i.Id == instanceId, cancellationToken);
        if (instance is null)
        {
            return WireGuardErrors.Instance.NotFound;
        }

        var network = await dbContext.Set<WireGuardNetwork>()
            .FirstOrDefaultAsync(n => n.Id == instance.NetworkId, cancellationToken);
        if (network is null)
        {
            return WireGuardErrors.Network.NotFound;
        }

        var peers = await dbContext.Set<Peer>()
            .Where(p => p.InstanceId == instanceId)
            .Select(p => new { p.Email, p.AssignedAddress })
            .ToListAsync(cancellationToken);

        var existingEmails = peers
            .Where(p => !string.IsNullOrWhiteSpace(p.Email))
            .Select(p => p.Email!)
            .ToList();

        // The interface's own address is reserved too, so an explicit row can't claim the gateway IP.
        var existingAddressHosts = peers
            .Select(p => EnrollmentPlanner.HostOf(p.AssignedAddress))
            .Append(EnrollmentPlanner.HostOf(instance.InterfaceAddress))
            .ToList();

        return new EnrollmentContext(instance, network, existingEmails, existingAddressHosts);
    }
}
