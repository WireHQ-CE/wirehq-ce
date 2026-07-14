using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WireHQ.Api.Audit;
using WireHQ.Api.Controllers;
using WireHQ.Application.Features.Audit.ExportAuditLogs;
using WireHQ.Application.Features.Audit.ListAuditLogs;
using WireHQ.Application.Features.Audit.VerifyAuditChain;

namespace WireHQ.Api.Controllers.V1;

[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/audit-logs")]
[Authorize]
public sealed class AuditLogsController : ApiControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] DateTimeOffset? from,
        [FromQuery] DateTimeOffset? to,
        [FromQuery] string? action,
        [FromQuery] string? category,
        [FromQuery] string? actor,
        [FromQuery] string? target,
        [FromQuery] string? outcome,
        [FromQuery] string? q,
        [FromQuery] string? cursor,
        [FromQuery] int pageSize = 50,
        CancellationToken cancellationToken = default) =>
        Ok(await Sender.Send(
            new ListAuditLogsQuery(from, to, action, category, actor, target, outcome, q, cursor, pageSize),
            cancellationToken));

    /// <summary>
    /// Export the (filtered, edition-clamped) audit feed as a downloadable CSV or JSON file. Enterprise only
    /// (gated by the <c>audit.export</c> entitlement); a lesser plan gets <c>plan.upgrade_required</c>. The export
    /// audits itself (an <c>audit.export</c> entry — audit-the-auditor, docs/15 §19.4).
    /// </summary>
    [HttpGet("export")]
    public async Task<IActionResult> Export(
        [FromQuery] DateTimeOffset? from,
        [FromQuery] DateTimeOffset? to,
        [FromQuery] string? action,
        [FromQuery] string? category,
        [FromQuery] string? actor,
        [FromQuery] string? target,
        [FromQuery] string? outcome,
        [FromQuery] string? q,
        [FromQuery] string format = "csv",
        CancellationToken cancellationToken = default)
    {
        var result = await Sender.Send(
            new ExportAuditLogsCommand(format, from, to, action, category, actor, target, outcome, q), cancellationToken);
        if (result.IsFailure)
        {
            return Problem(result.Error);
        }

        var rows = result.Value;
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
        return format.Equals("json", StringComparison.OrdinalIgnoreCase)
            ? File(AuditExportFormatter.ToJson(rows), AuditExportFormatter.JsonContentType, $"audit-logs-{stamp}.json")
            : File(AuditExportFormatter.ToCsv(rows), AuditExportFormatter.CsvContentType, $"audit-logs-{stamp}.csv");
    }

    /// <summary>
    /// Export the (filtered, edition-clamped) audit feed as a SIEM-ingestible feed — <c>format=ocsf</c> (OCSF
    /// newline-delimited JSON, the default) or <c>format=cef</c> (ArcSight CEF). Enterprise only (the same
    /// <c>audit.export</c> entitlement); audits itself like <see cref="Export"/>. (docs/15 §11/§16, Phase 7)
    /// </summary>
    [HttpGet("siem")]
    public async Task<IActionResult> Siem(
        [FromQuery] DateTimeOffset? from,
        [FromQuery] DateTimeOffset? to,
        [FromQuery] string? action,
        [FromQuery] string? category,
        [FromQuery] string? actor,
        [FromQuery] string? target,
        [FromQuery] string? outcome,
        [FromQuery] string? q,
        [FromQuery] string format = "ocsf",
        CancellationToken cancellationToken = default)
    {
        var result = await Sender.Send(
            new ExportAuditLogsCommand(format, from, to, action, category, actor, target, outcome, q), cancellationToken);
        if (result.IsFailure)
        {
            return Problem(result.Error);
        }

        var rows = result.Value;
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
        return format.Equals("cef", StringComparison.OrdinalIgnoreCase)
            ? File(AuditSiemFormatter.ToCef(rows), AuditSiemFormatter.CefContentType, $"audit-siem-{stamp}.cef")
            : File(AuditSiemFormatter.ToOcsf(rows), AuditSiemFormatter.OcsfContentType, $"audit-siem-{stamp}.ocsf.json");
    }

    /// <summary>Tamper-evidence: re-derive this tenant's audit hash chain and report whether it's intact.</summary>
    [HttpGet("verify")]
    public async Task<IActionResult> Verify(CancellationToken cancellationToken = default) =>
        Ok(await Sender.Send(new VerifyAuditChainQuery(), cancellationToken));
}
