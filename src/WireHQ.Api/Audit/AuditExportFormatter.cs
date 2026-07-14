using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using WireHQ.Application.Features.Audit.ExportAuditLogs;

namespace WireHQ.Api.Audit;

/// <summary>
/// Serialises an audit export (a list of <see cref="AuditExportRow"/>) to a downloadable CSV or JSON document.
/// CSV is RFC-4180-escaped; JSON nests the <c>changes</c> diff as real JSON (not a double-encoded string).
/// (docs/15 §5/§11)
/// </summary>
public static class AuditExportFormatter
{
    public const string CsvContentType = "text/csv";
    public const string JsonContentType = "application/json";

    private static readonly string[] Headers =
    [
        "occurredAtUtc", "action", "actorEmail", "actorType", "outcome",
        "targetType", "targetId", "ipAddress", "correlationId", "changes",
    ];

    public static byte[] ToCsv(IReadOnlyList<AuditExportRow> rows)
    {
        var sb = new StringBuilder();
        sb.Append(string.Join(',', Headers)).Append("\r\n");

        foreach (var r in rows)
        {
            sb
                .Append(Field(r.OccurredAtUtc.UtcDateTime.ToString("o"))).Append(',')
                .Append(Field(r.Action)).Append(',')
                .Append(Field(r.ActorEmail)).Append(',')
                .Append(Field(r.ActorType)).Append(',')
                .Append(Field(r.Outcome)).Append(',')
                .Append(Field(r.TargetType)).Append(',')
                .Append(Field(r.TargetId)).Append(',')
                .Append(Field(r.IpAddress)).Append(',')
                .Append(Field(r.CorrelationId)).Append(',')
                .Append(Field(r.Changes)).Append("\r\n");
        }

        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    public static byte[] ToJson(IReadOnlyList<AuditExportRow> rows)
    {
        var array = new JsonArray();
        foreach (var r in rows)
        {
            array.Add(new JsonObject
            {
                ["occurredAtUtc"] = r.OccurredAtUtc.UtcDateTime.ToString("o"),
                ["action"] = r.Action,
                ["actorEmail"] = r.ActorEmail,
                ["actorType"] = r.ActorType,
                ["outcome"] = r.Outcome,
                ["targetType"] = r.TargetType,
                ["targetId"] = r.TargetId,
                ["ipAddress"] = r.IpAddress,
                ["correlationId"] = r.CorrelationId,
                // Embed the diff as real JSON when it parses; otherwise keep the raw text.
                ["changes"] = ParseOrNull(r.Changes),
            });
        }

        return JsonSerializer.SerializeToUtf8Bytes(array, new JsonSerializerOptions { WriteIndented = true });
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

    // RFC 4180: quote a field containing a comma, quote or line break; escape embedded quotes by doubling.
    private static string Field(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        if (value.IndexOfAny([',', '"', '\n', '\r']) < 0)
        {
            return value;
        }

        return $"\"{value.Replace("\"", "\"\"")}\"";
    }
}
