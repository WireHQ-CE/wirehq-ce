using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
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
/// Process an inbound SMS provider webhook — the STOP/HELP path that keeps WireHQ's suppression ledger authoritative
/// (docs/36 §4.3/§5, Wave 4 Slice D). Anonymous and cross-tenant (<see cref="ITenantUnscopedRequest"/> → RLS bypass):
/// there is no ambient user/org, so the org is resolved <b>explicitly</b> by hashing the inbound token and matching the
/// stored hash (never a credential decrypt first), and the suppression row is written with that explicit org id — never
/// ambient stamping, so an opt-out in org A can never suppress the number in org B.
/// <para>
/// <b>Fail-closed, no-oracle:</b> every rejection path (blank/unknown token, unverifiable signature, unconfigured
/// endpoint) returns <see cref="Result.Success"/> so the controller replies <c>204</c> with no body — a probe can't
/// distinguish the reasons and can't trigger a provider retry storm. The signature is verified against a <b>pinned
/// public URL</b> (config base + <c>Request.Path</c>), never <c>Request.Host</c>.
/// </para>
/// The audit actions are all <c>notifications.*</c>, which the route cache deny-lists, so an inbound STOP can never
/// itself trigger a notification rule (the self-loop guard).
/// </summary>
public sealed record ProcessInboundSmsCommand(
    string Token, string RequestPath, IReadOnlyDictionary<string, string> Form, string Signature)
    : ICommand, ITenantUnscopedRequest;

public sealed class ProcessInboundSmsCommandHandler(
    IApplicationDbContext dbContext,
    IEnumerable<ISmsProvider> providers,
    ISecretProtector secretProtector,
    SmsNotificationOptions smsOptions,
    IDateTimeProvider clock,
    IAuditWriter audit,
    ILogger<ProcessInboundSmsCommandHandler> logger)
    : ICommandHandler<ProcessInboundSmsCommand>
{
    public async Task<Result> Handle(ProcessInboundSmsCommand command, CancellationToken cancellationToken)
    {
        // No token, or the operator hasn't pinned a public webhook URL → nothing can be verified. Drop silently (204).
        if (string.IsNullOrWhiteSpace(command.Token) || string.IsNullOrWhiteSpace(smsOptions.InboundWebhookBaseUrl))
        {
            return Result.Success();
        }

        // Resolve the org by HASHING the presented token and matching the stored hash — a single indexed equality, never
        // a plaintext compare, and BEFORE any credential decrypt (docs/36 §5). Cross-tenant read → bypass + no filter.
        var tokenHash = SmsInboundToken.Hash(command.Token);
        // Also match a token that is mid-rotation. Accepting the previous hash through its grace window is what stops a
        // rotation dropping a STOP: the operator's provider keeps posting to the old URL until they repoint it, and a
        // STOP that arrives in that gap is exactly the one we cannot afford to lose.
        var config = await dbContext.NotificationChannelConfigs.IgnoreQueryFilters()
            .FirstOrDefaultAsync(
                c => c.ChannelKind == ChannelKind.Sms
                     && (c.InboundWebhookTokenHash == tokenHash || c.PreviousInboundWebhookTokenHash == tokenHash),
                cancellationToken);
        if (config is null || string.IsNullOrEmpty(config.CredentialCiphertext))
        {
            return Result.Success(); // unknown/incomplete token → drop silently, no oracle
        }

        // An expired previous token is not a valid credential — resolve, then reject on expiry, so the grace window is
        // enforced at read time rather than by a sweeper.
        var matchedPreviousToken = config.InboundWebhookTokenHash != tokenHash;
        if (matchedPreviousToken && !config.IsInboundTokenRotating(clock.UtcNow))
        {
            return Result.Success(); // the overlap has lapsed → drop silently, same as an unknown token
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

        // Reconstruct the exact URL the provider signed = the PINNED public base + the reliable Request.Path (the token
        // is a path segment, no query to reconcile) — never Request.Host, which the edge rewrites (docs/36 §5).
        // Composed by the SAME helper that builds the URL we showed the operator, so the two cannot drift.
        var url = SmsInboundEndpoint.ComposeVerificationUrl(smsOptions.InboundWebhookBaseUrl!, command.RequestPath);

        // Verify the provider signature against that pinned URL. Fail-closed: null ⇒ drop + audit.
        var verified = provider.VerifyAndParse(
            new SmsInboundRequest(url, command.Form, command.Signature, credential));
        if (verified is null)
        {
            // Count it. A rising rejection count against a RESOLVED config is the only signal that either someone is
            // probing the endpoint or the operator's provider is misconfigured — and both look identical to "quiet"
            // without it. (Pre-resolution drops are not counted: there is no org to attribute them to.)
            config.RecordInboundRejected(clock.UtcNow);
            audit.Record("notifications.sms.inbound_rejected", AuditOutcome.Failure,
                nameof(NotificationChannelConfig), config.Id.ToString(), organizationId: config.OrganizationId);
            await dbContext.SaveChangesAsync(cancellationToken);
            return Result.Success(); // bad signature → 204 drop + audit (no 4xx oracle, no retry storm)
        }

        // A correctly-signed request just arrived, so this org's webhook is demonstrably wired up. That is the readiness
        // proof the send path holds on: it turns "STOP is receivable" from a checklist tick into an observed fact.
        // Which token carried it matters — a request on the OLD token during a rotation proves the provider has not
        // been repointed yet, and must not satisfy the hold for the new one.
        if (matchedPreviousToken)
        {
            config.MarkInboundPreviousTokenSeen(clock.UtcNow);
        }
        else
        {
            config.MarkInboundCurrentTokenVerified(clock.UtcNow);
        }

        // Canonicalise the inbound number through the SAME value object the send path resolves recipients with, so the
        // ledger key and the match key are one by construction. Taking the provider's field verbatim only looks safe on
        // Twilio: Vonage's `msisdn` has no leading '+', so a STOP would be recorded under a key the send path can never
        // match — a console reporting "suppressed" while messages keep going out. Provider-agnostic on purpose: it is
        // done here, once, rather than in each adapter. Unparseable ⇒ fail closed, still 204.
        var fromResult = PhoneNumber.Create(verified.FromE164);
        if (fromResult.IsFailure)
        {
            audit.Record("notifications.sms.inbound_rejected", AuditOutcome.Failure,
                nameof(NotificationChannelConfig), config.Id.ToString(), organizationId: config.OrganizationId);
            return Result.Success();
        }

        var from = fromResult.Value.Value;
        var keyword = FirstToken(verified.Body);
        var now = clock.UtcNow;

        // Commit the ledger BEFORE replying, always. The suppression row is the load-bearing act and the confirmation
        // is an after-effect: replying first means that if the save then fails, the subscriber holds a timestamped
        // "you have unsubscribed" and we hold no record of it — the worst possible pair to explain to a regulator.
        // The reply is also sent only on a real STATE TRANSITION, so a redelivered STOP or a repeat START costs the
        // operator nothing. (HELP has no ledger effect and always answers — that is the obligation being met.)
        switch (SmsInboundKeywords.Classify(verified.Body))
        {
            case SmsInboundAction.Stop:
                var suppressed = await SuppressAsync(
                    config.OrganizationId, from, config.FromValue, SmsOptOutSource.StopKeyword, keyword, now, cancellationToken);
                await dbContext.SaveChangesAsync(cancellationToken);
                if (suppressed)
                {
                    await SendConfirmationAsync(provider, config, credential, from, StopConfirmation, cancellationToken);
                }

                break;

            case SmsInboundAction.Start:
                var cleared = await ClearAsync(config.OrganizationId, from, keyword, now, cancellationToken);
                await dbContext.SaveChangesAsync(cancellationToken);
                if (cleared)
                {
                    await SendConfirmationAsync(provider, config, credential, from, StartConfirmation, cancellationToken);
                }

                break;

            case SmsInboundAction.Help:
                // No ledger change — but the HELP obligation is met by WireHQ's own reply (docs/36 §4.3).
                await SendConfirmationAsync(provider, config, credential, from, HelpReply, cancellationToken);
                break;

            case SmsInboundAction.None:
            default:
                break; // an ordinary inbound reply — acknowledged, nothing to do
        }

        return Result.Success();
    }

    /// <summary>Append a suppression row via the shared ledger helper (append-only, idempotent under redelivery) and
    /// audit it only when a new row was actually written. Returns whether the state actually changed, so a redelivered
    /// STOP does not send (and bill for) a second confirmation.</summary>
    private async Task<bool> SuppressAsync(
        Guid organizationId, string phoneE164, string? sender, SmsOptOutSource source, string? keyword,
        DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        // reason: null — an inbound keyword IS its own reason; Reason exists for the off-channel operator path (W3).
        if (!await SmsSuppression.AppendIfNotActiveAsync(
                dbContext, organizationId, phoneE164, sender, source, keyword, reason: null, nowUtc, cancellationToken))
        {
            return false;
        }

        // The org is passed explicitly: this handler is ITenantUnscopedRequest, so without it the entry is written with
        // organization_id = NULL and never appears in the owning org's audit console — the compliance record would be
        // invisible to the only people who need it. The target is the ledger ROW, not the org.
        var appended = dbContext.SmsOptOuts.Local
            .FirstOrDefault(o => o.OrganizationId == organizationId && o.PhoneE164 == phoneE164 && o.ClearedAtUtc == null);
        audit.Record("notifications.sms.opted_out", AuditOutcome.Success, nameof(SmsOptOut),
            appended?.Id.ToString(), new { Phone = SmsProviderStatus.MaskFrom(phoneE164) },
            organizationId: organizationId);
        return true;
    }

    /// <summary>Clear every active suppression for this (org, number) on a START keyword — a state transition, never a
    /// delete (docs/36 §4.3). Returns whether anything was actually cleared.</summary>
    private async Task<bool> ClearAsync(
        Guid organizationId, string phoneE164, string? keyword, DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        var active = await dbContext.SmsOptOuts.IgnoreQueryFilters()
            .Where(o => o.OrganizationId == organizationId && o.PhoneE164 == phoneE164 && o.ClearedAtUtc == null)
            .ToListAsync(cancellationToken);
        if (active.Count == 0)
        {
            return false;
        }

        foreach (var row in active)
        {
            row.Clear(keyword, nowUtc);
        }

        audit.Record("notifications.sms.opted_in", AuditOutcome.Success, nameof(SmsOptOut),
            active[0].Id.ToString(), new { Phone = SmsProviderStatus.MaskFrom(phoneE164) },
            organizationId: organizationId);
        return true;
    }

    /// <summary>WireHQ's own STOP/START/HELP reply via the org's provider (docs/36 §4.3). <b>Respects the dark send-gate</b>
    /// (<see cref="SmsNotificationOptions.Enabled"/>) — while SMS is dark no reply is sent, so no external message goes
    /// out before go-live. A reply failure is swallowed (best-effort; the ledger write is the load-bearing part).</summary>
    private async Task SendConfirmationAsync(
        ISmsProvider provider, NotificationChannelConfig config, string credential, string toE164, string body,
        CancellationToken cancellationToken)
    {
        if (!smsOptions.Enabled || string.IsNullOrWhiteSpace(config.FromValue))
        {
            return; // dark, or no sender configured — the ledger write already happened
        }

        try
        {
            await provider.SendAsync(new SmsSendRequest(toE164, config.FromValue, body, credential), cancellationToken);
        }
        catch
        {
            // Best-effort — never fail the inbound handling (and thus never make the provider retry) for a reply error.
            logger.LogWarning("SMS inbound: confirmation reply could not be sent for config {ConfigId}.", config.Id);
        }
    }

    private static string? FirstToken(string? body) =>
        string.IsNullOrWhiteSpace(body)
            ? null
            : body.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)[0];

    // Single-segment, disclosure-bearing replies (docs/36 §4.2). Sender identity is carried; the STOP notice is implicit
    // in the message intent.
    private const string StopConfirmation =
        "WireHQ: You have unsubscribed and will receive no further alerts. Reply START to resubscribe.";

    private const string StartConfirmation =
        "WireHQ: You are resubscribed to critical alerts. Reply STOP to opt out or HELP for help.";

    private const string HelpReply =
        "WireHQ sends critical account/security alerts on behalf of your organisation. Msg & data rates may apply. Reply STOP to opt out.";
}
