using System.Diagnostics.Metrics;

namespace WireHQ.Application.Common.Observability;

/// <summary>
/// Business counters for the marketplace commerce flow (docs/15 §7/§19, docs/19 §4) — on top of the
/// automatic per-use-case RED (<see cref="ApplicationMetrics"/>) that already covers the checkout/webhook
/// handlers. Registered on the metrics SDK in the host (<c>AddObservability</c> → <c>AddMeter</c>), so these
/// export over the same OTLP pipeline. Cardinality-safe: the only dimension is <c>module</c> (a bounded set
/// of module slugs), never an order/licence id.
///
/// Defined in the core layer (not the SaaS-only <c>Features/Marketplace</c> tree) so the host's
/// <c>AddMeter</c> line compiles in the Community Edition, where the meter simply stays idle — the marketplace
/// handlers that increment it are stripped there.
/// </summary>
public static class MarketplaceMetrics
{
    public const string MeterName = "WireHQ.Marketplace";

    public static readonly Meter Meter = new(MeterName);

    /// <summary>A one-off Checkout session was created (a buyer clicked Buy), tagged by <c>module</c>.</summary>
    public static readonly Counter<long> CheckoutsCreated = Meter.CreateCounter<long>(
        "wirehq.marketplace.checkouts_created",
        unit: "{checkout}",
        description: "Marketplace one-off Checkout sessions created, by module.");

    /// <summary>A paid order was fulfilled and a licence auto-issued, tagged by <c>module</c>.</summary>
    public static readonly Counter<long> OrdersPaid = Meter.CreateCounter<long>(
        "wirehq.marketplace.orders_paid",
        unit: "{order}",
        description: "Marketplace orders paid and fulfilled, by module.");

    /// <summary>A paid order was refunded or disputed and its licence auto-revoked, tagged by <c>module</c> and <c>reason</c> (refund/dispute).</summary>
    public static readonly Counter<long> OrdersReversed = Meter.CreateCounter<long>(
        "wirehq.marketplace.orders_reversed",
        unit: "{order}",
        description: "Marketplace orders refunded or disputed (licence auto-revoked), by module and reason.");

    /// <summary>A reconciliation pass completed, tagged by <c>source</c> (scheduled/on-demand) — the watchdog liveness signal.</summary>
    public static readonly Counter<long> ReconciliationRuns = Meter.CreateCounter<long>(
        "wirehq.marketplace.reconciliation_runs",
        unit: "{run}",
        description: "Marketplace reconciliation passes completed, by source.");

    /// <summary>Discrepancies found by a reconciliation pass, tagged by <c>kind</c> (missing_order/missing_payment/amount_mismatch) — alert on this.</summary>
    public static readonly Counter<long> ReconciliationDiscrepancies = Meter.CreateCounter<long>(
        "wirehq.marketplace.reconciliation_discrepancies",
        unit: "{discrepancy}",
        description: "Marketplace reconciliation discrepancies found, by kind.");

    // --- The licensing activation service (wave 4, docs/19 §5/§6). Same meter; the wirehq.licensing.*
    //     namespace is the docs/19 §6 RED contract. Outcomes are a bounded set — never an id.

    /// <summary>Activation attempts, tagged by <c>outcome</c> (activated/reactivated/slot_taken/revoked/invalid).</summary>
    public static readonly Counter<long> Activations = Meter.CreateCounter<long>(
        "wirehq.licensing.activations",
        unit: "{attempt}",
        description: "Licence activation attempts, by outcome.");

    /// <summary>Call-home verifications, tagged by <c>outcome</c> (ok/revoked_delivered/expired/invalid).</summary>
    public static readonly Counter<long> Verifications = Meter.CreateCounter<long>(
        "wirehq.licensing.verifications",
        unit: "{attempt}",
        description: "Licence call-home verifications, by outcome.");

    /// <summary>Self-serve deactivations, tagged by <c>outcome</c> (ok/move_limit/invalid).</summary>
    public static readonly Counter<long> Deactivations = Meter.CreateCounter<long>(
        "wirehq.licensing.deactivations",
        unit: "{attempt}",
        description: "Licence self-serve deactivations, by outcome.");
}
