namespace WireHQ.Domain.Identity;

/// <summary>
/// A platform-wide operator role, independent of (and above) any organization membership. This is the
/// SaaS hierarchy's top tier: a <see cref="SuperAdmin"/> manages every customer organization and can
/// impersonate their admins; a <see cref="Support"/> operator is a read-mostly diagnostics tier that can
/// inspect customer activity (e.g. cross-tenant audit) without the ability to mutate or impersonate. Most
/// users are <see cref="None"/>. Persisted as the enum name (string), so values may be reordered safely.
/// (docs/03-multi-tenancy.md, docs/15 §10, ADR-032)
/// </summary>
public enum PlatformRole
{
    /// <summary>A normal user — no platform-operator capabilities.</summary>
    None = 0,

    /// <summary>The all-seeing operator: manages customer orgs and can act as their admins.</summary>
    SuperAdmin = 1,

    /// <summary>
    /// A platform Support operator: a read-mostly diagnostics tier below <see cref="SuperAdmin"/>. Can run
    /// cross-tenant diagnostic reads (e.g. the platform audit search) — every such read is itself audited
    /// (audit-the-auditor) — but cannot mutate platform or tenant state, and cannot impersonate. (ADR-032)
    /// </summary>
    Support = 2,
}

public enum UserStatus
{
    /// <summary>Created but email not yet verified.</summary>
    PendingVerification = 0,
    Active = 1,
    /// <summary>Temporarily blocked (e.g. by an admin); cannot authenticate.</summary>
    Suspended = 2,
    /// <summary>Locked out after too many failed sign-in attempts; time-limited.</summary>
    Locked = 3,
}
