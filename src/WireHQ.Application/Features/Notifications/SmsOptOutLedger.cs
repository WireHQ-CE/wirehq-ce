using FluentValidation;
using Microsoft.EntityFrameworkCore;
using WireHQ.Application.Abstractions;
using WireHQ.Application.Abstractions.Persistence;
using WireHQ.Application.Authorization;
using WireHQ.Application.Common.Messaging;
using WireHQ.Domain.Auditing;
using WireHQ.Domain.Notifications;
using WireHQ.Domain.ValueObjects;
using WireHQ.Shared.Results;

namespace WireHQ.Application.Features.Notifications;

// The SMS opt-out console (docs/36 §4.3/§10 Slice D, Wave 4 W3) — the read surface over the append-only suppression
// ledger, plus the one write an operator needs: recording a revocation that arrived off-channel.
//
// Two shapes of this feature were deliberately NOT built:
//
//   * There is no un-suppress / clear endpoint. START clears a row, and START can only arrive from the subscriber's own
//     handset through the signed inbound webhook. An operator control that lifts a suppression would be a button whose
//     entire purpose is to resume texting someone who asked you to stop; the ledger's value is that it cannot be
//     quietly edited. A subscriber who wants back in opts back in.
//   * There is no partial / prefix search. The list renders numbers MASKED, so a `LIKE '%555%'` search would hand any
//     holder of notifications.manage a number-harvesting oracle over the masked display — type a prefix, page the
//     results, unmask the ledger four digits at a time. Search is EXACT-MATCH on the canonical E.164 form, so it can
//     only confirm a number the searcher already has. Pinned by a test.
//
// Gating: notifications.manage, and deliberately NOT the notifications.sms entitlement — the same reasoning that leaves
// W2's token revoke ungated. Reading your own compliance ledger, and recording an opt-out someone gave you by phone, are
// obligations that outlive a subscription; an org whose module lapsed still holds the legal duty not to text a person
// who revoked consent. Both are also strictly safety-increasing: neither can cause a message to be sent.

/// <summary>One row of the suppression ledger as the console shows it. The number is <b>masked</b> — the console proves
/// that a suppression exists and when, which is what an operator answering "why did this person stop getting alerts?"
/// needs; it is not a directory of the org's phone numbers.</summary>
public sealed record SmsOptOutDto(
    Guid Id,
    string PhoneMasked,
    string? Sender,
    string Source,
    string? Keyword,
    string? ClearedKeyword,
    string? Reason,
    DateTimeOffset SuppressedAtUtc,
    DateTimeOffset? ClearedAtUtc,
    bool IsActive);

/// <summary>
/// List the org's SMS suppression ledger, newest first, active suppressions before cleared ones.
/// <para><paramref name="Phone"/> is an <b>exact</b> lookup (canonicalised to E.164 first), never a partial match — see
/// the file header. An unparseable number is a client-input error, not an oracle: the ledger is org-scoped and the
/// caller already holds notifications.manage, so telling them their input was malformed reveals nothing.</para>
/// </summary>
public sealed record ListSmsOptOutsQuery(string? Phone = null, int PageSize = 50)
    : IQuery<IReadOnlyList<SmsOptOutDto>>, IAuthorizedRequest
{
    public IReadOnlyCollection<string> RequiredPermissions => [Permissions.Notifications.Manage];
}

public sealed class ListSmsOptOutsQueryHandler(IApplicationDbContext dbContext)
    : IQueryHandler<ListSmsOptOutsQuery, IReadOnlyList<SmsOptOutDto>>
{
    public async Task<Result<IReadOnlyList<SmsOptOutDto>>> Handle(
        ListSmsOptOutsQuery query, CancellationToken cancellationToken)
    {
        var pageSize = Math.Clamp(query.PageSize, 1, 200);

        // NOTE: no IgnoreQueryFilters here, unlike SmsSuppression — that helper serves the inbound webhook and the
        // dispatch drain, which run under a tenant BYPASS and so must scope the org by hand. This runs under the
        // caller's tenant connection, where the global filter is the isolation. Adding IgnoreQueryFilters would make
        // this a cross-tenant read of every org's ledger.
        var rows = dbContext.SmsOptOuts.AsQueryable();

        if (!string.IsNullOrWhiteSpace(query.Phone))
        {
            var phoneResult = PhoneNumber.Create(query.Phone);
            if (phoneResult.IsFailure)
            {
                return phoneResult.Error;
            }

            var exact = phoneResult.Value.Value;
            rows = rows.Where(o => o.PhoneE164 == exact);
        }

        // Masking happens after materialising — MaskFrom is a C# call EF cannot translate. The intermediate deliberately
        // is NOT an SmsOptOutDto with the raw number parked in its PhoneMasked slot: a DTO whose masked field transiently
        // holds plaintext is one early `return` away from shipping the whole ledger unmasked.
        var page = await rows
            // Active suppressions first — an operator opens this to answer "is this person opted out right now?".
            .OrderBy(o => o.ClearedAtUtc == null ? 0 : 1)
            .ThenByDescending(o => o.SuppressedAtUtc)
            .ThenByDescending(o => o.Id)
            .Take(pageSize)
            .Select(o => new
            {
                o.Id, o.PhoneE164, o.Sender, o.Source, o.Keyword, o.ClearedKeyword, o.Reason,
                o.SuppressedAtUtc, o.ClearedAtUtc,
            })
            .ToListAsync(cancellationToken);

        return page.Select(o => new SmsOptOutDto(
            o.Id,
            SmsProviderStatus.MaskFrom(o.PhoneE164),
            o.Sender,
            o.Source.ToString(),
            o.Keyword,
            o.ClearedKeyword,
            o.Reason,
            o.SuppressedAtUtc,
            o.ClearedAtUtc,
            o.ClearedAtUtc == null)).ToList();
    }
}

/// <summary>What a record-an-opt-out attempt did. <see cref="Appended"/> is false when the number was already actively
/// suppressed — a no-op the console reports honestly rather than claiming a new record was written.</summary>
public sealed record RecordSmsOptOutResult(bool Appended, string PhoneMasked);

/// <summary>
/// Record an opt-out that reached the operator by some means other than an SMS STOP — a phone call, an email, a support
/// ticket, a conversation. **FCC 47 CFR 64.1200(a)(10)**: a consumer may revoke consent "through any reasonable means",
/// and the sender may not dictate the channel. Without this, a revocation that arrived by phone could not be honoured at
/// all, because every other writer of this ledger is machine-driven (an inbound keyword or a carrier error).
/// <para>Appending is idempotent and strictly safety-increasing, so it carries no permission of its own beyond
/// <c>notifications.manage</c> — and, deliberately, no inverse.</para>
/// </summary>
public sealed record RecordSmsOptOutCommand(string Phone, string? Reason)
    : ICommand<RecordSmsOptOutResult>, IAuthorizedRequest
{
    public IReadOnlyCollection<string> RequiredPermissions => [Permissions.Notifications.Manage];
}

public sealed class RecordSmsOptOutCommandValidator : AbstractValidator<RecordSmsOptOutCommand>
{
    public RecordSmsOptOutCommandValidator()
    {
        RuleFor(x => x.Phone).NotEmpty().MaximumLength(PhoneNumber.MaxLength * 2);
        // 256 is the mapped column width (SmsOptOutConfiguration); reject over-length rather than truncating a record
        // that exists to be read back as evidence.
        RuleFor(x => x.Reason).MaximumLength(256);
    }
}

public sealed class RecordSmsOptOutCommandHandler(
    IApplicationDbContext dbContext, ITenantContext tenant, IDateTimeProvider clock, IAuditWriter audit)
    : ICommandHandler<RecordSmsOptOutCommand, RecordSmsOptOutResult>
{
    public async Task<Result<RecordSmsOptOutResult>> Handle(
        RecordSmsOptOutCommand command, CancellationToken cancellationToken)
    {
        if (tenant.OrganizationId is not { } organizationId)
        {
            return Error.Forbidden("auth.tenant_required", "No active organization.");
        }

        var phoneResult = PhoneNumber.Create(command.Phone);
        if (phoneResult.IsFailure)
        {
            return phoneResult.Error;
        }

        var phone = phoneResult.Value.Value;
        var reason = string.IsNullOrWhiteSpace(command.Reason) ? null : command.Reason.Trim();

        // Record the sender the suppression applies to, matching what the inbound path writes, so an operator-recorded
        // row is not a second-class ledger entry. Null when the org has no sender configured yet — which is legitimate:
        // a revocation can arrive before, or long after, a provider is connected.
        var sender = await dbContext.NotificationChannelConfigs
            .Where(c => c.ChannelKind == ChannelKind.Sms)
            .Select(c => c.FromValue)
            .FirstOrDefaultAsync(cancellationToken);

        var appended = await SmsSuppression.AppendIfNotActiveAsync(
            dbContext, organizationId, phone, sender, SmsOptOutSource.OperatorRequest, keyword: null, reason,
            clock.UtcNow, cancellationToken);

        var masked = SmsProviderStatus.MaskFrom(phone);
        if (!appended)
        {
            // Already suppressed. Not an error — the operator's intent (this number must not be texted) already holds,
            // and appending a duplicate active row would only make the ledger harder to read.
            return new RecordSmsOptOutResult(false, masked);
        }

        // Attribution lives here, not on the row: the audit trail is tamper-evident and records the acting user.
        var row = dbContext.SmsOptOuts.Local
            .FirstOrDefault(o => o.OrganizationId == organizationId && o.PhoneE164 == phone && o.ClearedAtUtc == null);
        audit.Record("notifications.sms.opt_out_recorded", AuditOutcome.Success, nameof(SmsOptOut),
            row?.Id.ToString(), new { Phone = masked, Reason = reason });

        return new RecordSmsOptOutResult(true, masked);
    }
}
