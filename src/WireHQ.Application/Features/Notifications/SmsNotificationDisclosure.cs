namespace WireHQ.Application.Features.Notifications;

/// <summary>
/// The SMS compliance disclosures + the disclosure-budget body renderer (docs/36-notifications.md §4.2, SMS Wave 4).
/// Two non-negotiable properties live here:
/// <list type="bullet">
/// <item><b>Consent disclosure</b> — the exact text + <see cref="DisclosureVersion"/> shown at the SMS opt-in step is
/// recorded verbatim on the immutable consent audit event (docs/36 §4.1), so the disclosure a user agreed to is
/// provable later. Bump <see cref="DisclosureVersion"/> whenever <see cref="DisclosureText"/> changes.</item>
/// <item><b>Reserved body budget</b> — every rendered message reserves room for the sender identity + the STOP notice
/// so the opt-out instruction is never crowded out by truncation (docs/36 §4.2). The variable body is truncated; the
/// disclosure is not.</item>
/// </list>
/// This is a pure renderer (no I/O), so the budget rules are unit-tested directly.
/// </summary>
public static class SmsNotificationDisclosure
{
    /// <summary>Version stamp for <see cref="DisclosureText"/>. Recorded on every consent event so the precise wording
    /// a user agreed to is auditable. BUMP THIS whenever the text below changes.</summary>
    public const string DisclosureVersion = "2026-07-19";

    /// <summary>The consent disclosure shown at the SMS opt-in step and recorded verbatim on the consent audit event
    /// (docs/36 §4.1). Covers the "Msg&amp;data rates may apply" + STOP/HELP requirements.</summary>
    public const string DisclosureText =
        "By enabling SMS alerts you consent to receive automated security and account text messages from WireHQ on " +
        "behalf of your organisation. Message frequency varies. Msg & data rates may apply. Reply STOP to opt out or " +
        "HELP for help.";

    /// <summary>The opt-out instruction that must appear in the message body (carrier + TCPA expectation, docs/36 §4.2).</summary>
    public const string StopNotice = "Reply STOP to opt out.";

    /// <summary>The sender identity prefix carried on every message (docs/36 §4.2 — WireHQ never sends an unbranded body).</summary>
    public const string SenderIdentity = "WireHQ";

    /// <summary>A single GSM-7 SMS segment. The renderer keeps the rendered body within one segment where possible so a
    /// disclosure-bearing alert is a single billable message (docs/36 §7 spend safety).</summary>
    public const int SingleSegmentChars = 160;

    /// <summary>Render the phone-verification (OTP) message. It is by definition the first message to a number, so it
    /// carries the sender identity + the STOP notice; the fixed-width code always fits within one segment.</summary>
    public static string RenderVerificationBody(string code) =>
        $"{SenderIdentity}: your verification code is {code}. {StopNotice}";

    /// <summary>
    /// Render an alert body within the reserved-disclosure budget (docs/36 §4.2): sender identity is prepended and the
    /// STOP notice appended (at least on the first message to a number); the variable <paramref name="body"/> is
    /// truncated with an ellipsis so it can never crowd out the disclosure. The disclosure text is fixed and never
    /// truncated.
    /// </summary>
    public static string RenderAlertBody(string body, bool includeStopNotice)
    {
        var prefix = $"{SenderIdentity}: ";
        var suffix = includeStopNotice ? $" {StopNotice}" : string.Empty;

        // Reserve the full prefix + suffix out of the single-segment budget; whatever remains is the body's allowance.
        var budgetForBody = SingleSegmentChars - prefix.Length - suffix.Length;
        var trimmed = (body ?? string.Empty).Trim();
        if (budgetForBody > 0 && trimmed.Length > budgetForBody)
        {
            // Truncate the VARIABLE body only — the disclosure suffix is preserved in full. The marker is an ASCII "..."
            // (three chars, reserved from the budget), NOT a "…" (U+2026): the ellipsis glyph is outside GSM-7, which
            // would force the whole message to UCS-2 (70-char segments) and quietly double the billable segment count —
            // defeating the single-segment spend guarantee this budget exists to keep (docs/36 §7).
            const string ellipsis = "...";
            var keep = Math.Max(0, budgetForBody - ellipsis.Length);
            trimmed = trimmed[..keep].TrimEnd() + ellipsis;
        }

        return prefix + trimmed + suffix;
    }
}
