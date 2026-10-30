using Microsoft.EntityFrameworkCore;
using WireHQ.Application.Abstractions;
using WireHQ.Application.Abstractions.Notifications;
using WireHQ.Application.Abstractions.Persistence;
using WireHQ.Application.Abstractions.Security;
using WireHQ.Application.Common.Messaging;
using WireHQ.Domain.Auditing;
using WireHQ.Domain.Notifications;
using WireHQ.Domain.ValueObjects;
using WireHQ.Shared.Results;

namespace WireHQ.Application.Features.Notifications;

/// <summary>
/// Process a provider <b>delivery status callback</b> — the half of send-idempotency that turns "we do not know" back
/// into a fact (docs/36 §7/§12, Wave 4 W4).
/// <para>
/// W4's safety property is that a send whose response was lost is <b>never blind-resent</b>: it is marked
/// <see cref="NotificationDeliveryStatus.Unresolved"/> and left alone. That is correct but unsatisfying on its own — the
/// operator is left with a row that says "possibly sent" forever. This closes that loop: the provider tells us what
/// actually happened, and the row resolves to Delivered or Failed with the real message id attached.
/// </para>
/// <para>
/// <b>Anonymous and cross-tenant</b> (<see cref="ITenantUnscopedRequest"/> → RLS bypass), so the same posture as the
/// inbound STOP webhook applies: the delivery is resolved <b>explicitly</b> by the id in the URL path, the signature is
/// verified with <b>that delivery's own organisation's</b> credential — so org A's credential can never authenticate a
/// callback about org B's delivery — and every outcome returns <c>204</c> with no body, so a probe learns nothing and a
/// provider retry storm is impossible.
/// </para>
/// <para>
/// It only ever resolves an <c>Unresolved</c> row (<see cref="NotificationDelivery.ResolveFromProvider"/>), which makes
/// a replayed callback a no-op and stops a late callback rewriting an outcome we observed for ourselves.
/// </para>
/// </summary>
public sealed record ProcessSmsStatusCallbackCommand(
    Guid DeliveryId, string RequestPath, IReadOnlyDictionary<string, string> Form, string Signature)
    : ICommand, ITenantUnscopedRequest;

public sealed class ProcessSmsStatusCallbackCommandHandler(
    IApplicationDbContext dbContext,
    IEnumerable<ISmsProvider> providers,
    ISecretProtector secretProtector,
    SmsNotificationOptions smsOptions,
    IDateTimeProvider clock,
    IAuditWriter audit)
    : ICommandHandler<ProcessSmsStatusCallbackCommand>
{
    // Twilio's terminal MessageStatus values. "delivered" is the only success; the rest are terminal failures. Anything
    // else (queued/sending/sent) is an interim update we deliberately ignore — only a final state resolves a row.
    private static readonly HashSet<string> DeliveredStatuses =
        new(StringComparer.OrdinalIgnoreCase) { "delivered" };

    private static readonly HashSet<string> FailedStatuses =
        new(StringComparer.OrdinalIgnoreCase) { "undelivered", "failed", "canceled", "cancelled" };

    // The carrier/recipient opt-out code, which can arrive on the callback as well as on the send response. Same
    // meaning, same self-heal (docs/36 §4.3/§7).
    private const int OptedOutErrorCode = 21610;

    public async Task<Result> Handle(ProcessSmsStatusCallbackCommand command, CancellationToken cancellationToken)
    {
        // No pinned public URL ⇒ nothing can be verified. Drop silently (204), same as the inbound path.
        if (command.DeliveryId == Guid.Empty || string.IsNullOrWhiteSpace(smsOptions.InboundWebhookBaseUrl))
        {
            return Result.Success();
        }

        // Cross-tenant read → bypass + no filter. The delivery id is what names the organisation here; there is no
        // ambient tenant on an anonymous provider callback.
        var delivery = await dbContext.NotificationDeliveries.IgnoreQueryFilters()
            .FirstOrDefaultAsync(d => d.Id == command.DeliveryId && d.ChannelKind == ChannelKind.Sms, cancellationToken);
        if (delivery is null)
        {
            return Result.Success(); // unknown or already pruned → drop silently, no oracle
        }

        var config = await dbContext.NotificationChannelConfigs.IgnoreQueryFilters()
            .FirstOrDefaultAsync(
                c => c.OrganizationId == delivery.OrganizationId && c.ChannelKind == ChannelKind.Sms, cancellationToken);
        if (config is null || string.IsNullOrEmpty(config.CredentialCiphertext))
        {
            return Result.Success();
        }

        var provider = providers.FirstOrDefault(p => p.Provider == config.ProviderKind);
        if (provider is null)
        {
            return Result.Success();
        }

        string credential;
        try
        {
            credential = secretProtector.Unprotect(config.CredentialCiphertext);
        }
        catch
        {
            return Result.Success(); // an unreadable credential can't verify a signature — drop
        }

        // Reconstruct the exact URL the provider signed = the PINNED public base + the arriving path, composed by the
        // SAME helper that built the callback URL we handed over, so the two cannot drift (docs/36 §5).
        var url = SmsStatusEndpoint.ComposeVerificationUrl(smsOptions.InboundWebhookBaseUrl!, command.RequestPath);

        // Twilio signs every webhook it sends the same way, so this reuses the provider's verifier rather than growing
        // a second implementation of the same HMAC. Deliberately VerifySignature and NOT VerifyAndParse: the latter also
        // insists on a parseable inbound MESSAGE, which would make a status callback's authenticity depend on fields the
        // parse happens to require — so a payload-shape change would be indistinguishable from a forged signature.
        if (!provider.VerifySignature(new SmsInboundRequest(url, command.Form, command.Signature, credential)))
        {
            // Deliberately NOT RecordInboundRejected: that counter is displayed in the console as "requests failed
            // signature checks" for the INBOUND STOP webhook, and it is what an operator reads to decide whether their
            // provider is pointed at the right URL. Folding status-callback rejections into it would answer a question
            // about one endpoint with evidence from another.
            audit.Record("notifications.sms.status_rejected", AuditOutcome.Failure,
                nameof(NotificationDelivery), delivery.Id.ToString(), organizationId: delivery.OrganizationId);
            return Result.Success(); // bad signature → 204 drop + audit (no 4xx oracle, no retry storm)
        }

        var status = command.Form.TryGetValue("MessageStatus", out var s) ? s : string.Empty;
        var messageSid = command.Form.TryGetValue("MessageSid", out var sid) ? sid : null;
        var errorCode = command.Form.TryGetValue("ErrorCode", out var ec) && int.TryParse(ec, out var parsed)
            ? parsed
            : (int?)null;

        // A carrier opt-out can surface here as well as on the send response. Self-heal the ledger either way — the
        // whole point of the 21610 backstop is that it must not depend on WHICH path told us (docs/36 §4.3).
        if (errorCode == OptedOutErrorCode)
        {
            var recipient = PhoneNumber.Create(delivery.Recipient);
            if (recipient.IsSuccess)
            {
                await SmsSuppression.AppendIfNotActiveAsync(
                    dbContext, delivery.OrganizationId, recipient.Value.Value, config.FromValue,
                    SmsOptOutSource.CarrierError, keyword: null, reason: null, clock.UtcNow, cancellationToken);
            }
        }

        var delivered = DeliveredStatuses.Contains(status);
        if (!delivered && !FailedStatuses.Contains(status))
        {
            // An interim state (queued/sending/sent). Nothing is final yet, so resolving on it would replace one
            // uncertainty with a different wrong answer. Persist any suppression the same tick and stop.
            await dbContext.SaveChangesAsync(cancellationToken);
            return Result.Success();
        }

        // Only ever resolves an Unresolved row: a replay is a no-op, and a late callback cannot rewrite an outcome we
        // observed ourselves. The reason carries the provider's error CODE, never its message — a provider string is
        // exactly the sort of thing that arrives carrying a phone number (docs/36 §5).
        if (delivery.ResolveFromProvider(
                delivered, messageSid,
                errorCode is { } code ? $"The provider reported delivery failure (code {code})." : "The provider reported delivery failure.",
                clock.UtcNow))
        {
            audit.Record(
                delivered ? "notifications.sms.status_delivered" : "notifications.sms.status_failed",
                delivered ? AuditOutcome.Success : AuditOutcome.Failure,
                nameof(NotificationDelivery), delivery.Id.ToString(),
                new { Status = status, ErrorCode = errorCode },
                organizationId: delivery.OrganizationId);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }
}
