using FluentValidation;
using Microsoft.EntityFrameworkCore;
using WireHQ.Application.Abstractions;
using WireHQ.Application.Abstractions.Persistence;
using WireHQ.Application.Common.Messaging;
using WireHQ.Application.Entitlements;
using WireHQ.Domain.Auditing;
using WireHQ.Modules.WireGuard.Authorization;
using WireHQ.Modules.WireGuard.Domain;
using WireHQ.Modules.WireGuard.Providers;
using WireHQ.Modules.WireGuard.Services;
using WireHQ.Shared.Results;

namespace WireHQ.Modules.WireGuard.Application.Peers;

/// <summary>
/// Creates a peer on an instance: generates its keypair (or accepts a client public key) + optional
/// preshared key, auto-allocates an address, binds it to the caller's identity, applies it via the
/// provider, and returns a ready-to-use client config + QR (when the server holds the key).
/// </summary>
public sealed record CreatePeerCommand(
    Guid InstanceId,
    string Name,
    string? Email,
    string? Department,
    string? DeviceType,
    bool GenerateKeypair,
    string? PublicKey,
    bool UsePresharedKey,
    string? AssignedAddress,
    IReadOnlyList<string>? AllowedIps,
    int? PersistentKeepalive) : ICommand<CreatePeerResponse>, IAuthorizedRequest, IRequiresVerifiedEmail
{
    public IReadOnlyCollection<string> RequiredPermissions => [WireGuardPermissions.Peers.Manage];
}

public sealed record CreatePeerResponse(
    Guid Id,
    string PublicKey,
    string AssignedAddress,
    string? Config,
    string? QrCodePngBase64);

public sealed class CreatePeerCommandValidator : AbstractValidator<CreatePeerCommand>
{
    public CreatePeerCommandValidator()
    {
        RuleFor(x => x.InstanceId).NotEmpty();
        RuleFor(x => x.Name).NotEmpty().MaximumLength(Peer.MaxNameLength);
        RuleFor(x => x.Email).EmailAddress().When(x => !string.IsNullOrWhiteSpace(x.Email));
        RuleFor(x => x.PublicKey).NotEmpty().When(x => !x.GenerateKeypair)
            .WithMessage("A public key is required when not generating a keypair.");
    }
}

public sealed class CreatePeerCommandHandler(
    IApplicationDbContext dbContext,
    ITenantContext tenant,
    ICurrentUser currentUser,
    IKeyManagementService keys,
    IAddressAllocator addressAllocator,
    IConfigurationService configuration,
    IQrCodeService qrCode,
    IWireGuardProviderFactory providerFactory,
    IConfigVersionWriter configVersions,
    IEntitlementService entitlements,
    IAuditWriter audit)
    : ICommandHandler<CreatePeerCommand, CreatePeerResponse>
{
    public async Task<Result<CreatePeerResponse>> Handle(CreatePeerCommand command, CancellationToken cancellationToken)
    {
        if (tenant.OrganizationId is not { } organizationId)
        {
            return Error.Forbidden("auth.tenant_required", "No active organization.");
        }

        // Plan quota: the org's plan caps how many peers (devices) it can enroll.
        var peerCount = await dbContext.Set<Peer>().CountAsync(cancellationToken);
        var withinQuota = await entitlements.EnsureCanAddAsync(PlanResource.Peers, peerCount, cancellationToken);
        if (withinQuota.IsFailure)
        {
            return withinQuota.Error;
        }

        var instance = await dbContext.Set<WireGuardInstance>()
            .FirstOrDefaultAsync(i => i.Id == command.InstanceId, cancellationToken);
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

        var peerId = Guid.CreateVersion7();

        // Keypair: server-generated (we hold the private key, can render the full config) or client-supplied.
        string publicKey;
        Guid? privateKeyId = null;
        string? privateKeyPlaintext = null;
        if (command.GenerateKeypair)
        {
            var pair = keys.GenerateAndStoreKeyPair(organizationId, KeyOwnerType.Peer, peerId);
            publicKey = pair.PublicKey;
            privateKeyId = pair.KeyMaterialId;
            privateKeyPlaintext = pair.PrivateKey;
        }
        else
        {
            publicKey = command.PublicKey!.Trim();
        }

        // Preshared key (recommended; on by default).
        Guid? presharedKeyId = null;
        string? presharedKeyPlaintext = null;
        if (command.UsePresharedKey)
        {
            var psk = keys.GenerateAndStorePresharedKey(organizationId, KeyOwnerType.Peer, peerId);
            presharedKeyId = psk.KeyMaterialId;
            presharedKeyPlaintext = psk.Secret;
        }

        // Address: explicit or auto-allocated from the network.
        string assignedAddress;
        if (!string.IsNullOrWhiteSpace(command.AssignedAddress))
        {
            assignedAddress = command.AssignedAddress.Contains('/') ? command.AssignedAddress : $"{command.AssignedAddress}/32";
        }
        else
        {
            var allocation = await addressAllocator.AllocateAsync(instance.Id, instance.InterfaceAddress, network.Cidr, cancellationToken);
            if (allocation.IsFailure)
            {
                return allocation.Error;
            }

            assignedAddress = allocation.Value;
        }

        var allowedIps = command.AllowedIps is { Count: > 0 } ? command.AllowedIps : network.DefaultAllowedIps.ToList();

        var peerResult = Peer.Create(peerId, organizationId, instance.Id, command.Name, command.Email,
            publicKey, assignedAddress, privateKeyId, presharedKeyId, currentUser.MembershipId);
        if (peerResult.IsFailure)
        {
            return peerResult.Error;
        }

        var peer = peerResult.Value;
        peer.SetProfile(command.Department, command.DeviceType);
        peer.SetAllowedIps(allowedIps);
        peer.SetKeepalive(command.PersistentKeepalive);
        dbContext.Set<Peer>().Add(peer);

        // Apply desired peer state to the provider (no-op for config-only).
        var provider = providerFactory.Resolve(instance.ProviderType);
        var providerRef = new ProviderInstanceRef(instance.Id, instance.ExternalId,
            instance.ProviderSettings.ToDictionary(kv => kv.Key, kv => kv.Value));
        var apply = await provider.ApplyPeerAsync(providerRef,
            new ProviderPeerSpec(publicKey, presharedKeyPlaintext, allowedIps, null, peer.PersistentKeepalive), cancellationToken);
        if (apply.IsFailure)
        {
            return apply.Error;
        }

        // Render the client config + QR now (only possible when we hold the private key).
        string? config = null;
        string? qr = null;
        if (privateKeyPlaintext is not null)
        {
            config = configuration.RenderPeerConfig(new PeerConfigInput(
                privateKeyPlaintext, assignedAddress, network.Dns.ToList(), instance.Mtu,
                instance.PublicKey, presharedKeyPlaintext, instance.EndpointHost, allowedIps, peer.PersistentKeepalive));
            qr = Convert.ToBase64String(qrCode.GeneratePng(config));
            await configVersions.WriteAsync(ConfigTargetType.Peer, peer.Id, config, "created", cancellationToken);
        }

        audit.Record("wg.peer.created", AuditOutcome.Success, nameof(Peer), peer.Id.ToString(),
            new { peer.Name, assignedAddress, serverGeneratedKey = command.GenerateKeypair });

        return new CreatePeerResponse(peer.Id, publicKey, assignedAddress, config, qr);
    }
}
