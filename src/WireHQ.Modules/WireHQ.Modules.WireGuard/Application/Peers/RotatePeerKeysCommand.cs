using Microsoft.EntityFrameworkCore;
using WireHQ.Application.Abstractions;
using WireHQ.Application.Abstractions.Persistence;
using WireHQ.Application.Common.Messaging;
using WireHQ.Domain.Auditing;
using WireHQ.Modules.WireGuard.Authorization;
using WireHQ.Modules.WireGuard.Domain;
using WireHQ.Modules.WireGuard.Providers;
using WireHQ.Modules.WireGuard.Services;
using WireHQ.Shared.Results;

namespace WireHQ.Modules.WireGuard.Application.Peers;

/// <summary>Rotates a peer's keypair: revokes the old, generates a new one, re-applies, and returns a fresh config.</summary>
public sealed record RotatePeerKeysCommand(Guid PeerId) : ICommand<RotatePeerKeysResponse>, IAuthorizedRequest
{
    public IReadOnlyCollection<string> RequiredPermissions => [WireGuardPermissions.Keys.Manage];
}

public sealed record RotatePeerKeysResponse(string PublicKey, string? Config, string? QrCodePngBase64);

public sealed class RotatePeerKeysCommandHandler(
    IApplicationDbContext dbContext,
    ITenantContext tenant,
    IKeyManagementService keys,
    IConfigurationService configuration,
    IQrCodeService qrCode,
    IWireGuardProviderFactory providerFactory,
    IDateTimeProvider clock,
    IConfigVersionWriter configVersions,
    IAuditWriter audit)
    : ICommandHandler<RotatePeerKeysCommand, RotatePeerKeysResponse>
{
    public async Task<Result<RotatePeerKeysResponse>> Handle(RotatePeerKeysCommand command, CancellationToken cancellationToken)
    {
        if (tenant.OrganizationId is not { } organizationId)
        {
            return Error.Forbidden("auth.tenant_required", "No active organization.");
        }

        var peer = await dbContext.Set<Peer>().FirstOrDefaultAsync(p => p.Id == command.PeerId, cancellationToken);
        if (peer is null)
        {
            return WireGuardErrors.Peer.NotFound;
        }

        var instance = await dbContext.Set<WireGuardInstance>().FirstOrDefaultAsync(i => i.Id == peer.InstanceId, cancellationToken);
        if (instance is null)
        {
            return WireGuardErrors.Instance.NotFound;
        }

        var network = await dbContext.Set<WireGuardNetwork>().FirstOrDefaultAsync(n => n.Id == instance.NetworkId, cancellationToken);

        var hadPsk = peer.PresharedKeyId is not null;
        await keys.RevokeForOwnerAsync(KeyOwnerType.Peer, peer.Id, clock.UtcNow, cancellationToken);

        var newKey = keys.GenerateAndStoreKeyPair(organizationId, KeyOwnerType.Peer, peer.Id);
        string? presharedPlaintext = null;
        Guid? presharedKeyId = null;
        if (hadPsk)
        {
            var psk = keys.GenerateAndStorePresharedKey(organizationId, KeyOwnerType.Peer, peer.Id);
            presharedKeyId = psk.KeyMaterialId;
            presharedPlaintext = psk.Secret;
        }

        peer.ReplaceKeys(newKey.PublicKey, newKey.KeyMaterialId, presharedKeyId);

        var provider = providerFactory.Resolve(instance.ProviderType);
        var providerRef = new ProviderInstanceRef(instance.Id, instance.ExternalId,
            instance.ProviderSettings.ToDictionary(kv => kv.Key, kv => kv.Value));
        await provider.ApplyPeerAsync(providerRef,
            new ProviderPeerSpec(newKey.PublicKey, presharedPlaintext, peer.AllowedIps.ToList(), null, peer.PersistentKeepalive), cancellationToken);

        var config = configuration.RenderPeerConfig(new PeerConfigInput(
            newKey.PrivateKey, peer.AssignedAddress, network?.Dns.ToList() ?? [], instance.Mtu,
            instance.PublicKey, presharedPlaintext, instance.EndpointHost, peer.AllowedIps.ToList(), peer.PersistentKeepalive));
        var qr = Convert.ToBase64String(qrCode.GeneratePng(config));

        await configVersions.WriteAsync(ConfigTargetType.Peer, peer.Id, config, "keys rotated", cancellationToken);

        audit.Record("wg.peer.keys_rotated", AuditOutcome.Success, nameof(Peer), peer.Id.ToString());

        return new RotatePeerKeysResponse(newKey.PublicKey, config, qr);
    }
}
