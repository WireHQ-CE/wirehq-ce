namespace WireHQ.Domain.Organizations;

/// <summary>Lifecycle state of a tenant.</summary>
public enum OrganizationStatus
{
    Active = 0,
    Suspended = 1,
    PendingDeletion = 2,
}

/// <summary>
/// The licensed edition (commercial plan) of a tenant — drives feature entitlements + quotas via the plan
/// catalog (docs/commercial.md), and which modules <c>IModule.IsEnabled</c> activates
/// (docs/02-architecture.md). The same binary serves every edition. Stored as a string, so ordering is
/// presentational only. (Stripe linkage lands in the billing slice; today the edition is the plan.)
/// </summary>
public enum OrganizationEdition
{
    Community = 0,
    Pro = 1,
    Enterprise = 2,

    /// <summary>The self-hosted Community Edition base plan: the SAME lean free core as SaaS
    /// <see cref="Community"/> (an empty gated-feature set), but <b>uncapped</b> — a self-hoster runs their own
    /// hardware. Premium capability is added on top by activating Marketplace module licences (the entitlement
    /// union in <c>EntitlementService</c>). CE provisions orgs on this edition. Stored as a string, so this
    /// value is cosmetic. (docs/29-ce-marketplace-modules.md M-2)</summary>
    CommunityEdition = 3,
}

/// <summary>
/// The state of an org's billing <see cref="Subscription"/>, mirrored from Stripe (webhooks are the source
/// of truth) or set app-side for a no-card trial. It <em>drives</em> the org's <see cref="Organization.Edition"/>
/// but is never read by entitlement gating itself — gating reads the edition, so a plan gates identically
/// whether Stripe, a trial, or the operator set it. Stored as a string. (docs/commercial.md §6/§7)
/// </summary>
public enum SubscriptionStatus
{
    /// <summary>No billing relationship — a free Community org (the default).</summary>
    None = 0,

    /// <summary>In a trial (app-side or Stripe); full plan entitlements hold until <c>TrialEndUtc</c>.</summary>
    Trialing = 1,

    /// <summary>Paid and current.</summary>
    Active = 2,

    /// <summary>A payment failed; entitlements are retained until <c>GraceEndsUtc</c>, then drop to Community.</summary>
    PastDue = 3,

    /// <summary>The subscription ended (cancel/expire); the org has dropped to Community.</summary>
    Canceled = 4,
}
