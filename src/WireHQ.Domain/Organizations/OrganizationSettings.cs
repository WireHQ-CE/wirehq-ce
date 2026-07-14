using WireHQ.Domain.Common;

namespace WireHQ.Domain.Organizations;

/// <summary>
/// Per-tenant configuration, security policy, and entitlements. <c>IModule.IsEnabled</c>
/// consults <see cref="EnabledModules"/> + the org edition so the same binary lights up
/// different capabilities per tenant — the basis for SaaS plans and paid add-ons.
/// (docs/02-architecture.md, docs/03-multi-tenancy.md)
/// </summary>
public sealed class OrganizationSettings : Entity, ITenantOwned
{
    // EF Core
    private OrganizationSettings()
    {
    }

    private OrganizationSettings(Guid id, Guid organizationId)
        : base(id)
    {
        OrganizationId = organizationId;
    }

    public Guid OrganizationId { get; private set; }

    /// <summary>When true, members must complete MFA before accessing org resources.</summary>
    public bool RequireMfa { get; private set; }

    public int SessionIdleTimeoutMinutes { get; private set; } = 60 * 12;

    /// <summary>Licensed/enabled feature modules for this tenant (module names).</summary>
    public IReadOnlyCollection<string> EnabledModules { get; private set; } = [];

    /// <summary>Free-form settings bag (persisted as jsonb).</summary>
    public IReadOnlyDictionary<string, string> Flags { get; private set; } = new Dictionary<string, string>();

    public static OrganizationSettings CreateDefault(Guid organizationId) =>
        new(Guid.CreateVersion7(), organizationId);

    public void SetRequireMfa(bool required) => RequireMfa = required;

    public void SetEnabledModules(IEnumerable<string> modules) => EnabledModules = modules.ToArray();
}
