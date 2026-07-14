using System.IO.Compression;
using System.Text;
using Microsoft.EntityFrameworkCore;
using WireHQ.Application.Abstractions;
using WireHQ.Application.Abstractions.Persistence;
using WireHQ.Application.Common.Messaging;
using WireHQ.Domain.Auditing;
using WireHQ.Modules.WireGuard.Authorization;
using WireHQ.Modules.WireGuard.Domain;
using WireHQ.Modules.WireGuard.Services;
using WireHQ.Shared.Results;

namespace WireHQ.Modules.WireGuard.Application.Enrollment;

/// <summary>
/// Builds a downloadable ZIP for an enrollment batch: one <c>.conf</c> + QR PNG per server-keyed peer,
/// plus a <c>manifest.csv</c>. Reveals private keys + preshared keys (re-rendered just-in-time, never
/// stored in clear), so it is a command — permissioned (<c>wg.peers.export</c>) and audited, mirroring
/// the per-peer config export. (docs/11-wireguard-module.md §7, step 8)
/// </summary>
public sealed record GenerateEnrollmentPackageCommand(Guid BatchId) : ICommand<EnrollmentPackageResult>, IAuthorizedRequest
{
    public IReadOnlyCollection<string> RequiredPermissions => [WireGuardPermissions.Peers.Export];
}

public sealed record EnrollmentPackageResult(byte[] Zip, string FileName);

public sealed class GenerateEnrollmentPackageCommandHandler(
    IApplicationDbContext dbContext,
    IKeyManagementService keys,
    IConfigurationService configuration,
    IQrCodeService qrCode,
    IAuditWriter audit)
    : ICommandHandler<GenerateEnrollmentPackageCommand, EnrollmentPackageResult>
{
    public async Task<Result<EnrollmentPackageResult>> Handle(GenerateEnrollmentPackageCommand command, CancellationToken cancellationToken)
    {
        var batch = await dbContext.Set<EnrollmentBatch>()
            .FirstOrDefaultAsync(b => b.Id == command.BatchId, cancellationToken);
        if (batch is null)
        {
            return WireGuardErrors.Enrollment.BatchNotFound;
        }

        var instance = await dbContext.Set<WireGuardInstance>()
            .FirstOrDefaultAsync(i => i.Id == batch.InstanceId, cancellationToken);
        if (instance is null)
        {
            return WireGuardErrors.Instance.NotFound;
        }

        var network = await dbContext.Set<WireGuardNetwork>()
            .FirstOrDefaultAsync(n => n.Id == instance.NetworkId, cancellationToken);
        var dns = network?.Dns.ToList() ?? [];

        var peers = await dbContext.Set<Peer>()
            .Where(p => p.EnrollmentBatchId == batch.Id && p.PrivateKeyId != null)
            .OrderBy(p => p.AssignedAddress)
            .ToListAsync(cancellationToken);

        var manifest = new StringBuilder("name,email,address,public_key,file\n");
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var peer in peers)
            {
                var privateKey = await keys.RevealAsync(peer.PrivateKeyId!.Value, cancellationToken);
                if (privateKey is null)
                {
                    continue;
                }

                var presharedKey = peer.PresharedKeyId is { } pskId ? await keys.RevealAsync(pskId, cancellationToken) : null;

                var config = configuration.RenderPeerConfig(new PeerConfigInput(
                    privateKey, peer.AssignedAddress, dns, instance.Mtu,
                    instance.PublicKey, presharedKey, instance.EndpointHost, peer.AllowedIps.ToList(), peer.PersistentKeepalive));

                var fileBase = $"{SafeName(peer.Name)}-{peer.Id.ToString()[..8]}";
                await WriteEntryAsync(archive, $"{fileBase}.conf", Encoding.UTF8.GetBytes(config), cancellationToken);
                await WriteEntryAsync(archive, $"{fileBase}.png", qrCode.GeneratePng(config), cancellationToken);

                manifest.Append(Csv(peer.Name)).Append(',').Append(Csv(peer.Email ?? string.Empty)).Append(',')
                    .Append(Csv(peer.AssignedAddress)).Append(',').Append(Csv(peer.PublicKey)).Append(',')
                    .Append(Csv($"{fileBase}.conf")).Append('\n');
            }

            await WriteEntryAsync(archive, "manifest.csv", Encoding.UTF8.GetBytes(manifest.ToString()), cancellationToken);
        }

        audit.Record("wg.enrollment.package_exported", AuditOutcome.Success, nameof(EnrollmentBatch), batch.Id.ToString(),
            new { peerCount = peers.Count });

        var zipName = $"{instance.Slug.Value}-enrollment-{batch.Id.ToString()[..8]}.zip";
        return new EnrollmentPackageResult(buffer.ToArray(), zipName);
    }

    private static async Task WriteEntryAsync(ZipArchive archive, string name, byte[] content, CancellationToken cancellationToken)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        await using var stream = entry.Open();
        await stream.WriteAsync(content, cancellationToken);
    }

    private static string SafeName(string name)
    {
        var slug = new string(name.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-').ToArray())
            .Trim('-')
            .ToLowerInvariant();
        return string.IsNullOrEmpty(slug) ? "peer" : slug;
    }

    private static string Csv(string value) =>
        value.Contains(',') || value.Contains('"') || value.Contains('\n')
            ? $"\"{value.Replace("\"", "\"\"")}\""
            : value;
}
