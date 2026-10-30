using System.Text.Json.Serialization;
using WireHQ.Domain.Common;

namespace WireHQ.Domain.Notifications;

/// <summary>
/// One entry in the <b>append-only SMS suppression ledger</b> (docs/36-notifications.md §4.3/§8, SMS Wave 4 Slice D) —
/// the durable, carrier/TCPA-grade record that a number opted out of SMS from an organisation's sender.
/// <para>
/// <b>STOP appends; START never deletes.</b> A STOP keyword (or a carrier opt-out, Twilio error 21610) appends a row
/// with <see cref="SuppressedAtUtc"/> set and <see cref="ClearedAtUtc"/> null; a START keyword <b>clears</b> the row
/// (<see cref="Clear"/> sets <see cref="ClearedAtUtc"/>) — a state transition, not a delete, so the proof of the prior
/// opt-out survives. "<b>Currently suppressed</b>" for an (org, number) is therefore the existence of a row with
/// <see cref="ClearedAtUtc"/> == null (see <see cref="IsActive"/>), NOT mere row existence. The durable ledger is
/// retained independently of the 30-day delivery-history prune.
/// </para>
/// <para>
/// Tenant-owned in the reused <c>identity</c> schema (carries <c>organization_id</c> ⇒ RLS-covered automatically). The
/// inbound STOP webhook writes it under a tenant bypass with an EXPLICITLY-resolved org (never ambient), so an opt-out
/// in org A can never suppress that number in org B (docs/36 §5).
/// </para>
/// </summary>
public sealed class SmsOptOut : Entity, ITenantOwned
{
    // EF Core
    private SmsOptOut()
    {
    }

    private SmsOptOut(
        Guid id, Guid organizationId, string phoneE164, string? sender, SmsOptOutSource source, string? keyword,
        string? reason, DateTimeOffset suppressedAtUtc)
        : base(id)
    {
        OrganizationId = organizationId;
        PhoneE164 = phoneE164;
        Sender = sender;
        Source = source;
        Keyword = keyword;
        Reason = reason;
        SuppressedAtUtc = suppressedAtUtc;
    }

    public Guid OrganizationId { get; private set; }

    /// <summary>The suppressed number in E.164. The plaintext number is needed to match the send-time recipient; it is
    /// never surfaced by a query beyond the opt-out console and masked in logs (docs/36 §5).</summary>
    public string PhoneE164 { get; private set; } = null!;

    /// <summary>The org's sender number / Messaging Service the opt-out applies to, recorded for the audit trail. WireHQ
    /// uses one dedicated sender per org, so suppression is matched on (org, number); this preserves the sender-scoped
    /// record the design calls for (docs/36 §4.3) without splitting the match key.</summary>
    public string? Sender { get; private set; }

    /// <summary>What caused the suppression — an inbound STOP keyword, a START clear, or a carrier opt-out backstop
    /// (Twilio 21610). START rows are represented as a <see cref="Clear"/> of the active STOP/CarrierError row.</summary>
    public SmsOptOutSource Source { get; private set; }

    /// <summary>The raw inbound keyword that drove the transition (e.g. <c>STOP</c>, <c>UNSTOP</c>), for the audit trail.</summary>
    public string? Keyword { get; private set; }

    /// <summary>The keyword that CLEARED this suppression (START/YES/UNSTOP), kept separate from the one that caused
    /// it — overwriting <see cref="Keyword"/> would make the ledger misstate why the subscriber opted out.</summary>
    public string? ClearedKeyword { get; private set; }

    /// <summary>Free-text justification, for a suppression an operator recorded on the subscriber's behalf
    /// (<see cref="SmsOptOutSource.OperatorRequest"/>) — e.g. "opted out by phone, ticket 1234". It records *how the
    /// revocation reached the operator*, not *who recorded it*: attribution is the audit trail's job
    /// (<c>notifications.sms.opt_out_recorded</c> carries the acting user), which is tamper-evident, whereas this column
    /// is free text.</summary>
    public string? Reason { get; private set; }

    public DateTimeOffset SuppressedAtUtc { get; private set; }

    /// <summary>When a START keyword cleared this suppression (a state transition — the row is never deleted). Null while
    /// the number is still opted out.</summary>
    public DateTimeOffset? ClearedAtUtc { get; private set; }

    /// <summary>True while this row represents an <b>active</b> suppression — set but not yet cleared. The single
    /// authority for "is this (org, number) currently opted out?".</summary>
    public bool IsActive => ClearedAtUtc is null;

    /// <summary>Append a new suppression (an inbound STOP keyword, a carrier 21610 self-heal, or an operator recording a
    /// revocation that arrived off-channel). The caller has already confirmed there is no active row for this
    /// (org, number); appending a fresh row keeps the ledger append-only.
    /// <para><paramref name="reason"/> is required at the call site rather than defaulted: the machine-driven sources
    /// genuinely have none (the keyword IS the reason), and an <see cref="SmsOptOutSource.OperatorRequest"/> row without
    /// one is a ledger entry no regulator could interpret. A default would make forgetting it silent.</para></summary>
    public static SmsOptOut Suppress(
        Guid organizationId, string phoneE164, string? sender, SmsOptOutSource source, string? keyword, string? reason,
        DateTimeOffset nowUtc) =>
        new(Guid.CreateVersion7(), organizationId, phoneE164, sender, source, keyword, reason, nowUtc);

    /// <summary>Clear an active suppression on a START keyword — a state transition (never a delete). Idempotent: a row
    /// already cleared is left untouched. The <see cref="Keyword"/> that cleared it is recorded for the audit trail.</summary>
    public void Clear(string? keyword, DateTimeOffset nowUtc)
    {
        if (ClearedAtUtc is not null)
        {
            return;
        }

        ClearedAtUtc = nowUtc;

        // Record the clearing keyword SEPARATELY. This used to do `Keyword = keyword ?? Keyword`, permanently
        // overwriting the keyword that caused the opt-out — so a cleared row reported "UNSTOP" as the reason the
        // subscriber had opted out. In a ledger whose whole justification is durable TCPA/carrier proof, that is a
        // false statement in the record.
        ClearedKeyword = keyword;
    }
}

/// <summary>What drove an <see cref="SmsOptOut"/> transition (docs/36 §8). Serialized as a string on any API boundary
/// (the opt-out console) per the house convention.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SmsOptOutSource
{
    /// <summary>An inbound STOP-family keyword (STOP, STOPALL, UNSUBSCRIBE, CANCEL, END, QUIT).</summary>
    StopKeyword = 0,

    /// <summary>An inbound START-family keyword (START, YES, UNSTOP) — represented as a <see cref="SmsOptOut.Clear"/>.</summary>
    StartKeyword = 1,

    /// <summary>A carrier/provider opt-out surfaced on a send (Twilio error 21610) — the self-heal backstop that keeps the
    /// app ledger authoritative even if the inbound-forwarding path is momentarily missed (docs/36 §4.3/§7).</summary>
    CarrierError = 2,

    /// <summary>An operator recorded the opt-out on the subscriber's behalf — someone who revoked consent by phone,
    /// email or in person. FCC 47 CFR 64.1200(a)(10) treats revocation by any reasonable means as valid, so there has
    /// to be a way to honour one that never arrived as an SMS. Adding a suppression is strictly safety-increasing, so
    /// it needs no new permission; REMOVING one is deliberately not offered at all.
    /// (Serialised as a string — "OperatorRequest" is 15 chars and fits the existing varchar(16), no widening.)</summary>
    OperatorRequest = 3,
}
