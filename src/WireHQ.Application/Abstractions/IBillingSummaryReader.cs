namespace WireHQ.Application.Abstractions;

/// <summary>
/// Reads the active org's subscription state for display (the <c>/me</c> billing summary). A port so
/// the billing read is substitutive (docs/17-community-edition.md §5): the SaaS build replaces the
/// default with the Stripe-backed reader (<c>Infrastructure/Billing</c>); the Community Edition keeps
/// the <see cref="NullBillingSummaryReader"/> default — no billing plane, <c>/me</c> reports "None".
/// Deliberately NOT in <c>Abstractions/Billing</c> (that folder is SaaS-only and stripped).
/// </summary>
public interface IBillingSummaryReader
{
    /// <summary>The active org's subscription snapshot, or null when there is no billing plane / no subscription.</summary>
    Task<BillingSnapshot?> ReadAsync(CancellationToken cancellationToken);
}

/// <summary>A display-only subscription snapshot (status/trial/period/grace).</summary>
public sealed record BillingSnapshot(
    string Status,
    DateTimeOffset? TrialEndUtc,
    DateTimeOffset? CurrentPeriodEndUtc,
    DateTimeOffset? GraceEndsUtc);

/// <summary>The no-billing-plane default (the Community Edition posture).</summary>
public sealed class NullBillingSummaryReader : IBillingSummaryReader
{
    public Task<BillingSnapshot?> ReadAsync(CancellationToken cancellationToken) =>
        Task.FromResult<BillingSnapshot?>(null);
}
