using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using WireHQ.Api.Observability;
using WireHQ.Application.Features.Audit.ExportAuditLogs;

namespace WireHQ.Api.Audit;

/// <summary>
/// Serialises an audit export (a list of <see cref="AuditExportRow"/>) to a SIEM-ingestible feed (docs/15
/// §11/§16, Phase 7): <b>OCSF</b> as newline-delimited JSON (one event object per line — the shape SIEMs ingest)
/// or ArcSight <b>CEF</b> (one event per line). Both are built from the same filtered, edition-clamped rows as the
/// CSV/JSON export and gated to Enterprise (the <c>audit.export</c> entitlement). Action keys are classified via
/// <see cref="SiemActionMap"/>; OCSF carries the full <c>changes</c> diff under <c>unmapped</c>, CEF stays lean
/// (header + standard keys) since it is the lossy compatibility format.
/// </summary>
public static class AuditSiemFormatter
{
    public const string OcsfContentType = "application/x-ndjson";
    public const string CefContentType = "text/plain";

    private const string Vendor = "WireHQ";
    private const string Product = "WireHQ";
    private const string OcsfVersion = "1.3.0";

    /// <summary>OCSF events as newline-delimited JSON (NDJSON) — one event object per line.</summary>
    public static byte[] ToOcsf(IReadOnlyList<AuditExportRow> rows)
    {
        var sb = new StringBuilder();
        foreach (var r in rows)
        {
            sb.Append(BuildOcsfEvent(r).ToJsonString()).Append('\n');
        }

        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    /// <summary>CEF events — one line per event (CEF:0|vendor|product|version|sig|name|sev|extension).</summary>
    public static byte[] ToCef(IReadOnlyList<AuditExportRow> rows)
    {
        var sb = new StringBuilder();
        foreach (var r in rows)
        {
            sb.Append(BuildCefLine(r)).Append('\n');
        }

        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    private static JsonObject BuildOcsfEvent(AuditExportRow r)
    {
        var map = SiemActionMap.Map(r.Action);
        var failure = string.Equals(r.Outcome, "Failure", StringComparison.OrdinalIgnoreCase);
        var (statusId, status) = failure ? (2, "Failure") : (1, "Success");
        var (severityId, severity) = failure ? (4, "High") : (1, "Informational");

        var actor = new JsonObject();
        if (!string.IsNullOrEmpty(r.ActorEmail) || !string.IsNullOrEmpty(r.ActorType))
        {
            var user = new JsonObject();
            if (!string.IsNullOrEmpty(r.ActorEmail))
            {
                user["email_addr"] = r.ActorEmail;
            }

            if (!string.IsNullOrEmpty(r.ActorType))
            {
                user["type"] = r.ActorType;
            }

            actor["user"] = user;
        }

        var evt = new JsonObject
        {
            ["metadata"] = new JsonObject
            {
                ["version"] = OcsfVersion,
                ["log_provider"] = Product,
                ["correlation_uid"] = r.CorrelationId,
                ["product"] = new JsonObject
                {
                    ["name"] = Product,
                    ["vendor_name"] = Vendor,
                    ["version"] = ObservabilityResource.Version,
                },
            },
            ["category_uid"] = map.CategoryUid,
            ["category_name"] = map.CategoryName,
            ["class_uid"] = map.ClassUid,
            ["class_name"] = map.ClassName,
            ["activity_id"] = map.ActivityId,
            ["activity_name"] = map.ActivityName,
            ["type_uid"] = map.TypeUid,
            ["time"] = r.OccurredAtUtc.ToUnixTimeMilliseconds(),
            ["severity_id"] = severityId,
            ["severity"] = severity,
            ["status_id"] = statusId,
            ["status"] = status,
            ["message"] = r.Action,
            ["actor"] = actor,
            // OCSF best practice: vendor-specific fields live under `unmapped`. The full EF before/after diff
            // rides here as real JSON so the SIEM keeps fidelity the standard schema doesn't model.
            ["unmapped"] = new JsonObject
            {
                ["wirehq.action"] = r.Action,
                ["wirehq.target_type"] = r.TargetType,
                ["wirehq.target_id"] = r.TargetId,
                ["wirehq.correlation_ref"] = r.CorrelationId,
                ["wirehq.changes"] = ParseOrNull(r.Changes),
            },
        };

        if (!string.IsNullOrEmpty(r.IpAddress))
        {
            evt["src_endpoint"] = new JsonObject { ["ip"] = r.IpAddress };
        }

        return evt;
    }

    private static string BuildCefLine(AuditExportRow r)
    {
        var map = SiemActionMap.Map(r.Action);
        var failure = string.Equals(r.Outcome, "Failure", StringComparison.OrdinalIgnoreCase);
        var severity = failure ? 8 : 3;

        // CEF:Version|Device Vendor|Device Product|Device Version|Signature ID|Name|Severity|Extension
        var header = string.Join(
            '|',
            "CEF:0",
            CefHeader(Vendor),
            CefHeader(Product),
            CefHeader(ObservabilityResource.Version),
            CefHeader(r.Action),
            CefHeader($"{map.ClassName}: {map.ActivityName}"),
            severity.ToString(CultureInfo.InvariantCulture));

        var ext = new StringBuilder();
        AppendExt(ext, "rt", r.OccurredAtUtc.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture));
        AppendExt(ext, "act", r.Action);
        AppendExt(ext, "outcome", r.Outcome);
        AppendExt(ext, "suser", r.ActorEmail);
        AppendExt(ext, "src", r.IpAddress);
        AppendExt(ext, "cs1Label", string.IsNullOrEmpty(r.CorrelationId) ? null : "correlationRef");
        AppendExt(ext, "cs1", r.CorrelationId);
        AppendExt(ext, "cs2Label", string.IsNullOrEmpty(r.TargetType) ? null : "targetType");
        AppendExt(ext, "cs2", r.TargetType);
        AppendExt(ext, "cs3Label", string.IsNullOrEmpty(r.TargetId) ? null : "targetId");
        AppendExt(ext, "cs3", r.TargetId);

        return $"{header}|{ext.ToString().TrimEnd()}";
    }

    private static JsonNode? ParseOrNull(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            return json;
        }
    }

    // CEF header fields escape backslash + pipe; newlines are illegal in a header so collapse them.
    private static string CefHeader(string? value) =>
        (value ?? string.Empty)
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("|", "\\|", StringComparison.Ordinal)
            .Replace('\n', ' ')
            .Replace('\r', ' ');

    // CEF extension values escape backslash, equals, and newlines.
    private static string CefExtValue(string value) =>
        value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("=", "\\=", StringComparison.Ordinal)
            .Replace("\r", string.Empty, StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);

    private static void AppendExt(StringBuilder sb, string key, string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return;
        }

        sb.Append(key).Append('=').Append(CefExtValue(value)).Append(' ');
    }
}
