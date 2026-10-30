using Microsoft.EntityFrameworkCore;
using WireHQ.Application.Abstractions.Persistence;
using WireHQ.Domain.Notifications;

namespace WireHQ.Application.Features.Notifications;

/// <summary>
/// Reads + appends the append-only SMS suppression ledger (<see cref="SmsOptOut"/>, docs/36 §4.3, Wave 4 Slice D) — the
/// single place the "is this (org, number) currently opted out?" predicate lives, so the four call sites (the inbound
/// STOP webhook write, the resolve-time pre-filter, the send-time authoritative check, and the OTP-send precondition)
/// can never diverge. "Currently suppressed" is an <b>active</b> row (<see cref="SmsOptOut.ClearedAtUtc"/> is null),
/// never mere row existence — a re-opted-in number is not suppressed. Every query scopes the org EXPLICITLY (callers run
/// under either the tenant connection or the drain's bypass), and uses <c>IgnoreQueryFilters</c> so it is correct in both.
/// </summary>
public static class SmsSuppression
{
    /// <summary>True when this (org, number) has an active suppression — the authoritative opt-out check.</summary>
    public static Task<bool> IsSuppressedAsync(
        IApplicationDbContext dbContext, Guid organizationId, string phoneE164, CancellationToken cancellationToken) =>
        dbContext.SmsOptOuts.IgnoreQueryFilters()
            .AnyAsync(o => o.OrganizationId == organizationId && o.PhoneE164 == phoneE164 && o.ClearedAtUtc == null, cancellationToken);

    /// <summary>The subset of <paramref name="phones"/> that are currently suppressed for the org — a single query for a
    /// batch resolve-time pre-filter (docs/36 §4.3).</summary>
    public static async Task<HashSet<string>> ActiveAmongAsync(
        IApplicationDbContext dbContext, Guid organizationId, IReadOnlyCollection<string> phones, CancellationToken cancellationToken)
    {
        if (phones.Count == 0)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        var suppressed = await dbContext.SmsOptOuts.IgnoreQueryFilters()
            .Where(o => o.OrganizationId == organizationId && o.ClearedAtUtc == null && phones.Contains(o.PhoneE164))
            .Select(o => o.PhoneE164)
            .ToListAsync(cancellationToken);
        return suppressed.ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>Append a suppression row iff this (org, number) is not already actively suppressed. Returns true when a
    /// row was appended (the caller then audits). Does NOT save — the append rides the caller's unit of work.
    /// <para>De-dupes against BOTH the persisted rows (the DB check) AND rows already added-but-unsaved in this unit of
    /// work (the change-tracker <c>Local</c> check) — so a repeat 21610 within one drain batch, or a redelivered STOP
    /// within one request, can't append a second active row. Two genuinely <b>concurrent</b> transactions could still
    /// each append (the index is intentionally non-unique so a redelivery never faults the business save); that is
    /// benign — the reads treat any ≥1 active row as suppressed and START clears them all, so it can never
    /// under-suppress, only leave a duplicate ledger row.</para></summary>
    public static async Task<bool> AppendIfNotActiveAsync(
        IApplicationDbContext dbContext, Guid organizationId, string phoneE164, string? sender,
        SmsOptOutSource source, string? keyword, string? reason, DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        // A pending (unsaved) active row in THIS unit of work is invisible to the DB query — check the tracker too.
        var pendingLocally = dbContext.SmsOptOuts.Local
            .Any(o => o.OrganizationId == organizationId && o.PhoneE164 == phoneE164 && o.ClearedAtUtc is null);
        if (pendingLocally || await IsSuppressedAsync(dbContext, organizationId, phoneE164, cancellationToken))
        {
            return false;
        }

        dbContext.SmsOptOuts.Add(SmsOptOut.Suppress(organizationId, phoneE164, sender, source, keyword, reason, nowUtc));
        return true;
    }
}
