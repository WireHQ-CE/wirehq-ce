namespace WireHQ.Application.Features.Notifications;

/// <summary>
/// The pure eligibility filter for the SMS phone audience (docs/36-notifications.md §10, SMS Wave 4, Slice B). A number
/// is dispatched to ONLY when the user has BOTH proven ownership (<c>PhoneVerified</c> + a non-null <c>PhoneE164</c>)
/// AND consented (<c>SmsAlerts</c>, default OFF) — ownership and consent are separate facts (docs/36 §4). Extracted from
/// the DB-bound scheduler so this load-bearing filter (a wrong predicate would SMS a non-consenting or unverified user)
/// is unit-tested directly. Org-scoping is applied by the caller's explicit <c>OrganizationId</c> predicate (the same
/// B-2 pattern as the email resolver), so the candidates fed here are already the rule's org.
/// </summary>
internal static class SmsAudience
{
    /// <summary>Keep only verified + consented candidates with a real number; de-duplicate exact E.164 strings.</summary>
    public static IReadOnlyList<string> Select(IEnumerable<SmsAudienceCandidate> candidates) =>
        candidates
            .Where(IsEligible)
            .Select(c => c.PhoneE164!)
            .Distinct(StringComparer.Ordinal)
            .ToList();

    /// <summary>A candidate is eligible only if the number is proven-owned AND consented (docs/36 §4).</summary>
    public static bool IsEligible(SmsAudienceCandidate candidate) =>
        candidate.PhoneVerified
        && candidate.SmsAlerts
        && !string.IsNullOrWhiteSpace(candidate.PhoneE164);
}

/// <summary>One resolved user's SMS-relevant facts: their verified number, whether it is verified, and whether they
/// consented. A user with no preferences row has <c>SmsAlerts = false</c> (default-OFF), so they are never included.</summary>
internal readonly record struct SmsAudienceCandidate(string? PhoneE164, bool PhoneVerified, bool SmsAlerts);
