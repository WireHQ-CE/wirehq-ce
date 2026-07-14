using System.Text.Json;
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

namespace WireHQ.Modules.WireGuard.Application.Enrollment;

/// <summary>
/// Imports a validated CSV in one unit of work: for every Create row it generates a keypair + preshared
/// key, allocates an address (batch-safe), creates the identity-bound peer, applies it via the provider,
/// renders + versions its config, and records the whole run as an <see cref="EnrollmentBatch"/> with a
/// per-row outcome summary. Duplicates are skipped (v1 policy). The UnitOfWork behavior commits the
/// peers, key material, config versions, batch, and audit row atomically. (docs/11-wireguard-module.md §7)
/// </summary>
public sealed record ExecuteEnrollmentCommand(Guid InstanceId, string FileName, string CsvText)
    : ICommand<EnrollmentResult>, IAuthorizedRequest, IRequiresFeature
{
    public IReadOnlyCollection<string> RequiredPermissions => [WireGuardPermissions.Enrollment.Manage];

    public string RequiredFeature => PlanFeatures.BulkEnrollment;
}

public sealed record EnrollmentResult(
    Guid BatchId,
    int TotalRows,
    int Created,
    int Skipped,
    int Failed,
    IReadOnlyList<EnrollmentResultRow> Results);

public sealed record EnrollmentResultRow(
    int RowNumber,
    string? Name,
    string? Email,
    string Outcome,
    string? AssignedAddress,
    Guid? PeerId,
    string? Reason);

public sealed class ExecuteEnrollmentCommandHandler(
    IApplicationDbContext dbContext,
    ITenantContext tenant,
    ICurrentUser currentUser,
    IDateTimeProvider clock,
    IEnrollmentService enrollment,
    IAddressAllocator addressAllocator,
    IKeyManagementService keys,
    IConfigurationService configuration,
    IWireGuardProviderFactory providerFactory,
    IConfigVersionWriter configVersions,
    IAuditWriter audit)
    : ICommandHandler<ExecuteEnrollmentCommand, EnrollmentResult>
{
    public async Task<Result<EnrollmentResult>> Handle(ExecuteEnrollmentCommand command, CancellationToken cancellationToken)
    {
        if (tenant.OrganizationId is not { } organizationId)
        {
            return Error.Forbidden("auth.tenant_required", "No active organization.");
        }

        var context = await EnrollmentContext.LoadAsync(dbContext, command.InstanceId, cancellationToken);
        if (context.IsFailure)
        {
            return context.Error;
        }

        var (instance, network, existingEmails, existingAddressHosts) = context.Value;

        var parsed = enrollment.Parse(command.CsvText);
        if (parsed.IsFailure)
        {
            return parsed.Error;
        }

        var plan = await EnrollmentPlanner.PlanAsync(
            parsed.Value, network.Cidr, existingEmails, existingAddressHosts, enrollment,
            (count, reserved, ct) => addressAllocator.AllocateManyAsync(instance.Id, instance.InterfaceAddress, network.Cidr, count, reserved, ct),
            cancellationToken);

        if (plan.CreateCount == 0)
        {
            return WireGuardErrors.Enrollment.NothingToImport;
        }

        var batch = EnrollmentBatch.Start(organizationId, instance.Id, command.FileName);
        dbContext.Set<EnrollmentBatch>().Add(batch);

        var provider = providerFactory.Resolve(instance.ProviderType);
        var providerRef = new ProviderInstanceRef(instance.Id, instance.ExternalId,
            instance.ProviderSettings.ToDictionary(kv => kv.Key, kv => kv.Value));

        var results = new List<EnrollmentResultRow>(plan.Rows.Count);
        var created = 0;

        foreach (var planned in plan.Rows)
        {
            if (planned.Outcome != EnrollmentOutcome.Create)
            {
                results.Add(new EnrollmentResultRow(
                    planned.RowNumber, planned.Row.Name, planned.Row.Email, planned.Outcome.ToString(), null, null, planned.Reason));
                continue;
            }

            var peerId = Guid.CreateVersion7();
            var keyPair = keys.GenerateAndStoreKeyPair(organizationId, KeyOwnerType.Peer, peerId);
            var psk = keys.GenerateAndStorePresharedKey(organizationId, KeyOwnerType.Peer, peerId);

            var assignedAddress = planned.AssignedAddress!;
            var allowedIps = planned.Row.AllowedIps is { Count: > 0 } ? planned.Row.AllowedIps : network.DefaultAllowedIps.ToList();

            var peerResult = Peer.Create(peerId, organizationId, instance.Id, planned.Row.Name!, planned.Row.Email,
                keyPair.PublicKey, assignedAddress, keyPair.KeyMaterialId, psk.KeyMaterialId, currentUser.MembershipId);
            if (peerResult.IsFailure)
            {
                results.Add(new EnrollmentResultRow(
                    planned.RowNumber, planned.Row.Name, planned.Row.Email, EnrollmentOutcome.Error.ToString(), null, null, peerResult.Error.Description));
                continue;
            }

            var peer = peerResult.Value;
            peer.SetProfile(planned.Row.Department, planned.Row.DeviceType);
            peer.SetAllowedIps(allowedIps);
            peer.SetKeepalive(KeepaliveFor(planned.Row.DeviceType));
            peer.SetEnrollmentBatch(batch.Id);
            dbContext.Set<Peer>().Add(peer);

            var apply = await provider.ApplyPeerAsync(providerRef,
                new ProviderPeerSpec(keyPair.PublicKey, psk.Secret, allowedIps, null, peer.PersistentKeepalive), cancellationToken);
            if (apply.IsFailure)
            {
                // A provider failure mid-batch aborts the whole import (nothing is saved — the UoW only
                // commits on success), so the run is all-or-nothing rather than partially applied.
                return apply.Error;
            }

            var config = configuration.RenderPeerConfig(new PeerConfigInput(
                keyPair.PrivateKey, assignedAddress, network.Dns.ToList(), instance.Mtu,
                instance.PublicKey, psk.Secret, instance.EndpointHost, allowedIps, peer.PersistentKeepalive));
            await configVersions.WriteAsync(ConfigTargetType.Peer, peer.Id, config, "enrolled", cancellationToken);

            created++;
            results.Add(new EnrollmentResultRow(
                planned.RowNumber, planned.Row.Name, planned.Row.Email, EnrollmentOutcome.Create.ToString(), assignedAddress, peer.Id, null));
        }

        var total = plan.Rows.Count;
        batch.Complete(total, created, total - created, JsonSerializer.Serialize(results), clock.UtcNow);

        audit.Record("wg.enrollment.executed", AuditOutcome.Success, nameof(EnrollmentBatch), batch.Id.ToString(),
            new { command.FileName, total, created, skipped = plan.SkipCount, failed = plan.ErrorCount });

        return new EnrollmentResult(batch.Id, total, created, plan.SkipCount, plan.ErrorCount, results);
    }

    // DeviceType drives the keepalive default: mobile/phone devices behind NAT benefit from a 25s
    // persistent keepalive; fixed devices don't need it. (docs/11-wireguard-module.md §7)
    private static int? KeepaliveFor(string? deviceType) =>
        deviceType?.Trim().ToLowerInvariant() is "mobile" or "phone" ? 25 : null;
}
