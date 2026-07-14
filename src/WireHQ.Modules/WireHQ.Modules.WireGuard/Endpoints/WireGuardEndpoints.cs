using System.Text;
using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using WireHQ.Modules.WireGuard.Application.Config;
using WireHQ.Modules.WireGuard.Application.Dashboard;
using WireHQ.Modules.WireGuard.Application.Enrollment;
using WireHQ.Modules.WireGuard.Application.Instances;
using WireHQ.Modules.WireGuard.Application.Networks;
using WireHQ.Modules.WireGuard.Application.Peers;
using WireHQ.Modules.WireGuard.Domain;
using WireHQ.Modules.WireGuard.Services;
using WireHQ.Shared.Results;

namespace WireHQ.Modules.WireGuard.Endpoints;

/// <summary>
/// The module's HTTP surface, mapped by <c>IModule.MapEndpoints</c> under <c>/api/v1/wireguard</c>.
/// Thin: each endpoint dispatches a command/query and maps the Result to HTTP. All require auth;
/// fine-grained permissions are enforced in the pipeline (IAuthorizedRequest).
/// </summary>
public static class WireGuardEndpoints
{
    public static IEndpointRouteBuilder MapWireGuardEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints
            .MapGroup("/api/v1/wireguard")
            .RequireAuthorization()
            .WithTags("WireGuard");

        group.MapGet("/overview", async (ISender sender, CancellationToken ct) =>
            (await sender.Send(new GetWireGuardOverviewQuery(), ct)).ToHttpResult());

        group.MapGet("/networks", async (ISender sender, CancellationToken ct) =>
            (await sender.Send(new ListNetworksQuery(), ct)).ToHttpResult());

        group.MapPost("/networks", async (CreateNetworkRequest request, ISender sender, CancellationToken ct) =>
            (await sender.Send(new CreateNetworkCommand(request.Name, request.Cidr, request.Dns), ct))
                .ToHttpResult(StatusCodes.Status201Created));

        group.MapGet("/networks/{id:guid}", async (Guid id, ISender sender, CancellationToken ct) =>
            (await sender.Send(new GetNetworkQuery(id), ct)).ToHttpResult());

        group.MapPatch("/networks/{id:guid}", async (Guid id, UpdateNetworkRequest request, ISender sender, CancellationToken ct) =>
            (await sender.Send(new UpdateNetworkCommand(id, request.Name, request.Dns, request.DefaultAllowedIps), ct))
                .ToHttpResult());

        group.MapDelete("/networks/{id:guid}", async (Guid id, ISender sender, CancellationToken ct) =>
            (await sender.Send(new DeleteNetworkCommand(id), ct)).ToHttpResult());

        group.MapGet("/instances", async (ISender sender, CancellationToken ct) =>
            (await sender.Send(new ListInstancesQuery(), ct)).ToHttpResult());

        group.MapGet("/instances/{id:guid}", async (Guid id, ISender sender, CancellationToken ct) =>
            (await sender.Send(new GetInstanceQuery(id), ct)).ToHttpResult());

        group.MapPost("/instances", async (CreateInstanceRequest request, ISender sender, CancellationToken ct) =>
            (await sender.Send(new CreateInstanceCommand(
                request.NetworkId, request.Name, request.ListenPort, request.InterfaceAddress,
                request.EndpointHost, request.Dns, request.Mtu, request.Slug), ct))
                .ToHttpResult(StatusCodes.Status201Created));

        group.MapPatch("/instances/{id:guid}", async (Guid id, UpdateInstanceRequest request, ISender sender, CancellationToken ct) =>
            (await sender.Send(new UpdateInstanceCommand(
                id, request.Name, request.Description, request.EndpointHost, request.Dns, request.Mtu), ct))
                .ToHttpResult());

        group.MapDelete("/instances/{id:guid}", async (Guid id, ISender sender, CancellationToken ct) =>
            (await sender.Send(new DeleteInstanceCommand(id), ct)).ToHttpResult());

        group.MapPost("/instances/{id:guid}/control", async (Guid id, ControlInstanceRequest request, ISender sender, CancellationToken ct) =>
            (await sender.Send(new ControlInstanceCommand(id, request.Action), ct)).ToHttpResult());

        // Full server config: [Interface] + a [Peer] block per active peer (reveals keys; audited).
        group.MapGet("/instances/{id:guid}/config", async (Guid id, ISender sender, CancellationToken ct) =>
            (await sender.Send(new GenerateInstanceConfigCommand(id), ct))
                .ToHttpResult(r => Results.File(Encoding.UTF8.GetBytes(r.Config), "application/octet-stream", r.FileName)));

        // ---- Peers ----
        group.MapGet("/instances/{id:guid}/peers", async (Guid id, ISender sender, CancellationToken ct) =>
            (await sender.Send(new ListPeersQuery(id), ct)).ToHttpResult());

        group.MapPost("/instances/{id:guid}/peers", async (Guid id, CreatePeerRequest request, ISender sender, CancellationToken ct) =>
            (await sender.Send(new CreatePeerCommand(
                id, request.Name, request.Email, request.Department, request.DeviceType,
                request.GenerateKeypair ?? true, request.PublicKey, request.UsePresharedKey ?? true,
                request.AssignedAddress, request.AllowedIps, request.PersistentKeepalive), ct))
                .ToHttpResult(StatusCodes.Status201Created));

        group.MapPatch("/peers/{peerId:guid}", async (Guid peerId, UpdatePeerRequest request, ISender sender, CancellationToken ct) =>
            (await sender.Send(new UpdatePeerCommand(
                peerId, request.Name, request.Department, request.DeviceType, request.AllowedIps, request.PersistentKeepalive), ct))
                .ToHttpResult());

        group.MapPost("/peers/{peerId:guid}/enable", async (Guid peerId, ISender sender, CancellationToken ct) =>
            (await sender.Send(new EnablePeerCommand(peerId), ct)).ToHttpResult());

        group.MapPost("/peers/{peerId:guid}/disable", async (Guid peerId, ISender sender, CancellationToken ct) =>
            (await sender.Send(new DisablePeerCommand(peerId), ct)).ToHttpResult());

        group.MapDelete("/peers/{peerId:guid}", async (Guid peerId, ISender sender, CancellationToken ct) =>
            (await sender.Send(new DeletePeerCommand(peerId), ct)).ToHttpResult());

        group.MapPost("/peers/{peerId:guid}/keys/rotate", async (Guid peerId, ISender sender, CancellationToken ct) =>
            (await sender.Send(new RotatePeerKeysCommand(peerId), ct)).ToHttpResult());

        // Config download (.conf) + QR PNG.
        group.MapGet("/peers/{peerId:guid}/config", async (Guid peerId, ISender sender, CancellationToken ct) =>
            (await sender.Send(new GeneratePeerConfigCommand(peerId), ct))
                .ToHttpResult(r => Results.File(Encoding.UTF8.GetBytes(r.Config), "application/octet-stream", r.FileName)));

        group.MapGet("/peers/{peerId:guid}/config/qr", async (Guid peerId, ISender sender, IQrCodeService qr, CancellationToken ct) =>
            (await sender.Send(new GeneratePeerConfigCommand(peerId), ct))
                .ToHttpResult(r => Results.File(qr.GeneratePng(r.Config), "image/png")));

        // ---- Config version history ----
        group.MapGet("/peers/{peerId:guid}/config/versions", async (Guid peerId, ISender sender, CancellationToken ct) =>
            (await sender.Send(new ListConfigVersionsQuery(ConfigTargetType.Peer, peerId), ct)).ToHttpResult());

        group.MapGet("/peers/{peerId:guid}/config/versions/{version:int}", async (Guid peerId, int version, ISender sender, CancellationToken ct) =>
            (await sender.Send(new GetConfigVersionCommand(ConfigTargetType.Peer, peerId, version), ct)).ToHttpResult());

        group.MapGet("/instances/{id:guid}/config/versions", async (Guid id, ISender sender, CancellationToken ct) =>
            (await sender.Send(new ListConfigVersionsQuery(ConfigTargetType.Instance, id), ct)).ToHttpResult());

        group.MapGet("/instances/{id:guid}/config/versions/{version:int}", async (Guid id, int version, ISender sender, CancellationToken ct) =>
            (await sender.Send(new GetConfigVersionCommand(ConfigTargetType.Instance, id, version), ct)).ToHttpResult());

        // ---- Bulk enrollment (CSV) ----
        // Upload endpoints bind a multipart file. The API is bearer-authenticated (no cookie/CSRF
        // surface) and runs no antiforgery middleware, so antiforgery is disabled explicitly.
        group.MapPost("/instances/{id:guid}/enrollments/validate", async (Guid id, IFormFile file, IEnrollmentService enrollment, ISender sender, CancellationToken ct) =>
        {
            var csv = await ReadCsvAsync(file, enrollment.MaxBytes, ct);
            return csv.IsFailure
                ? csv.ToHttpResult()
                : (await sender.Send(new ValidateEnrollmentQuery(id, csv.Value), ct)).ToHttpResult();
        }).DisableAntiforgery();

        group.MapPost("/instances/{id:guid}/enrollments/execute", async (Guid id, IFormFile file, IEnrollmentService enrollment, ISender sender, CancellationToken ct) =>
        {
            var csv = await ReadCsvAsync(file, enrollment.MaxBytes, ct);
            return csv.IsFailure
                ? csv.ToHttpResult()
                : (await sender.Send(new ExecuteEnrollmentCommand(id, file.FileName, csv.Value), ct)).ToHttpResult();
        }).DisableAntiforgery();

        group.MapGet("/enrollments/{batchId:guid}", async (Guid batchId, ISender sender, CancellationToken ct) =>
            (await sender.Send(new GetEnrollmentBatchQuery(batchId), ct)).ToHttpResult());

        // ZIP of one .conf + QR per enrolled peer, plus a manifest (reveals keys; audited).
        group.MapGet("/enrollments/{batchId:guid}/package", async (Guid batchId, ISender sender, CancellationToken ct) =>
            (await sender.Send(new GenerateEnrollmentPackageCommand(batchId), ct))
                .ToHttpResult(r => Results.File(r.Zip, "application/zip", r.FileName)));

        return endpoints;
    }

    /// <summary>Reads an uploaded CSV into UTF-8 text, enforcing presence and the size cap.</summary>
    private static async Task<Result<string>> ReadCsvAsync(IFormFile? file, int maxBytes, CancellationToken cancellationToken)
    {
        if (file is null || file.Length == 0)
        {
            return WireGuardErrors.Enrollment.EmptyFile;
        }

        if (file.Length > maxBytes)
        {
            return WireGuardErrors.Enrollment.FileTooLarge;
        }

        using var reader = new StreamReader(file.OpenReadStream(), Encoding.UTF8);
        return await reader.ReadToEndAsync(cancellationToken);
    }
}

public sealed record CreateNetworkRequest(string Name, string Cidr, IReadOnlyList<string>? Dns);

public sealed record CreateInstanceRequest(
    Guid NetworkId, string Name, int ListenPort, string InterfaceAddress,
    string? EndpointHost, IReadOnlyList<string>? Dns, int? Mtu, string? Slug);

public sealed record UpdateInstanceRequest(
    string? Name, string? Description, string? EndpointHost, IReadOnlyList<string>? Dns, int? Mtu);

public sealed record ControlInstanceRequest(string Action);

public sealed record CreatePeerRequest(
    string Name,
    string? Email,
    string? Department,
    string? DeviceType,
    bool? GenerateKeypair,
    string? PublicKey,
    bool? UsePresharedKey,
    string? AssignedAddress,
    IReadOnlyList<string>? AllowedIps,
    int? PersistentKeepalive);

public sealed record UpdateNetworkRequest(string? Name, IReadOnlyList<string>? Dns, IReadOnlyList<string>? DefaultAllowedIps);

public sealed record UpdatePeerRequest(
    string? Name, string? Department, string? DeviceType, IReadOnlyList<string>? AllowedIps, int? PersistentKeepalive);
