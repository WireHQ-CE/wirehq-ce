using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Microsoft.EntityFrameworkCore;
using WireHQ.Application.Abstractions.Persistence;
using WireHQ.Modules.WireGuard.Domain;
using WireHQ.Shared.Results;

namespace WireHQ.Modules.WireGuard.Services;

/// <summary>
/// Allocates the next free host address (/32) in an instance's network. Race safety is backstopped
/// by the unique <c>(instance_id, assigned_address)</c> index — a concurrent collision fails the save
/// with a conflict rather than double-assigning. IPv4 for Phase 1. (docs/11-wireguard-module.md §9)
/// </summary>
public interface IAddressAllocator
{
    Task<Result<string>> AllocateAsync(Guid instanceId, string interfaceAddress, string networkCidr, CancellationToken cancellationToken);

    /// <summary>
    /// Allocates <paramref name="count"/> distinct free /32s in one pass. Used by bulk enrollment:
    /// because the whole batch saves in a single unit of work, freshly-allocated addresses are not yet
    /// in the database, so callers pass them (plus any explicit ones) via <paramref name="alreadyReserved"/>
    /// to avoid handing out the same address twice within the batch.
    /// </summary>
    Task<Result<IReadOnlyList<string>>> AllocateManyAsync(
        Guid instanceId, string interfaceAddress, string networkCidr, int count,
        IReadOnlyCollection<string> alreadyReserved, CancellationToken cancellationToken);
}

public sealed class AddressAllocator(IApplicationDbContext dbContext) : IAddressAllocator
{
    public async Task<Result<string>> AllocateAsync(Guid instanceId, string interfaceAddress, string networkCidr, CancellationToken cancellationToken)
    {
        var result = await AllocateManyAsync(instanceId, interfaceAddress, networkCidr, 1, [], cancellationToken);
        return result.IsFailure ? result.Error : result.Value[0];
    }

    public async Task<Result<IReadOnlyList<string>>> AllocateManyAsync(
        Guid instanceId, string interfaceAddress, string networkCidr, int count,
        IReadOnlyCollection<string> alreadyReserved, CancellationToken cancellationToken)
    {
        if (count <= 0)
        {
            return Result.Success<IReadOnlyList<string>>([]);
        }

        IPNetwork network;
        try
        {
            network = IPNetwork.Parse(networkCidr);
        }
        catch (FormatException)
        {
            return WireGuardErrors.Network.InvalidCidr;
        }

        if (network.BaseAddress.AddressFamily != AddressFamily.InterNetwork)
        {
            return WireGuardErrors.Network.InvalidCidr; // IPv4 only for now
        }

        var assigned = await dbContext.Set<Peer>()
            .Where(p => p.InstanceId == instanceId)
            .Select(p => p.AssignedAddress)
            .ToListAsync(cancellationToken);

        var used = new HashSet<uint>();
        foreach (var address in assigned.Append(interfaceAddress).Concat(alreadyReserved))
        {
            if (TryParseHost(address, out var value))
            {
                used.Add(value);
            }
        }

        var baseValue = ToUInt32(network.BaseAddress);
        var prefix = network.PrefixLength;
        var total = prefix >= 31 ? 0u : 1u << (32 - prefix);

        var allocated = new List<string>(count);

        // Skip the network address (i=0) and broadcast (i=total-1).
        for (var i = 1u; i < (total == 0 ? 0 : total - 1) && allocated.Count < count; i++)
        {
            var candidate = baseValue + i;
            if (used.Add(candidate))
            {
                allocated.Add($"{ToIp(candidate)}/32");
            }
        }

        return allocated.Count < count
            ? WireGuardErrors.Network.Exhausted
            : Result.Success<IReadOnlyList<string>>(allocated);
    }

    private static bool TryParseHost(string addressWithMaybePrefix, out uint value)
    {
        value = 0;
        var host = addressWithMaybePrefix.Split('/')[0];
        if (!IPAddress.TryParse(host, out var ip) || ip.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }

        value = ToUInt32(ip);
        return true;
    }

    private static uint ToUInt32(IPAddress ip) => BinaryPrimitives.ReadUInt32BigEndian(ip.GetAddressBytes());

    private static IPAddress ToIp(uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        return new IPAddress(bytes);
    }
}
