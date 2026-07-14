using System.Text.Json;
using WireHQ.Domain.Auditing;

namespace WireHQ.Infrastructure.Webhooks;

/// <summary>
/// Serializes the audit entry that triggered a webhook into the delivery body (docs/26-api-keys-webhooks.md §8).
/// Built <b>at capture time</b> and stored on the <c>WebhookDelivery</c>, so the body is fixed even if the audit row
/// is later pruned. The same JSON is what the sender HMAC-signs and POSTs.
/// </summary>
public static class WebhookPayload
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static string Serialize(AuditLog audit)
    {
        var payload = new
        {
            id = audit.Id,
            type = audit.Action,
            occurredAt = audit.OccurredAtUtc,
            organizationId = audit.OrganizationId,
            outcome = audit.Outcome.ToString(),
            actor = new
            {
                type = audit.ActorType,
                userId = audit.ActorUserId,
                email = audit.ActorEmail,
            },
            target = audit.TargetType is null && audit.TargetId is null
                ? null
                : new { type = audit.TargetType, id = audit.TargetId },
            changes = ParseChanges(audit.Changes),
        };

        return JsonSerializer.Serialize(payload, Options);
    }

    // The audit `changes` column is already a JSON string — embed it as a nested object, not an escaped string.
    private static JsonElement? ParseChanges(string? changes)
    {
        if (string.IsNullOrEmpty(changes))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(changes);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
