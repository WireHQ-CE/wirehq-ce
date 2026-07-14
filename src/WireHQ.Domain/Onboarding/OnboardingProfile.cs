using WireHQ.Domain.Common;

namespace WireHQ.Domain.Onboarding;

/// <summary>Where the org is in the post-signup "Tell us about your deployment" wizard.</summary>
public enum OnboardingStatus
{
    Pending = 0,
    Completed = 1,
    Skipped = 2,
}

/// <summary>The primary way the customer intends to use WireHQ (drives segmentation).</summary>
public enum OnboardingUseCase
{
    Unspecified = 0,
    BusinessVpn = 1,
    Msp = 2,
    Consultant = 3,
    Homelab = 4,
    Education = 5,
    Healthcare = 6,
    Government = 7,
    SoftwareCompany = 8,
    Other = 9,
}

/// <summary>
/// The optional, post-signup onboarding answers for an organization — one row per org. Captured by the
/// skippable Welcome Wizard to organise and segment customers from the start (Product/Sales analytics).
/// Tenant-owned; never blocks the user (the whole wizard is skippable). Team size, VPN-user count and the
/// current-VPN solution are stored as free strings (the UI offers option lists) so the taxonomy can change
/// without a migration.
/// </summary>
public sealed class OnboardingProfile : Entity, ITenantOwned, IAuditable
{
    public const int MaxText = 200;

    // EF Core
    private OnboardingProfile()
    {
    }

    private OnboardingProfile(Guid id, Guid organizationId)
        : base(id)
    {
        OrganizationId = organizationId;
        Status = OnboardingStatus.Pending;
    }

    public Guid OrganizationId { get; private set; }

    public OnboardingStatus Status { get; private set; }

    public string? CompanyName { get; private set; }
    public string? CompanyWebsite { get; private set; }
    public string? Industry { get; private set; }
    public string? TeamSize { get; private set; }
    public string? VpnUsers { get; private set; }
    public string? CurrentVpnSolution { get; private set; }
    public OnboardingUseCase UseCase { get; private set; }

    public DateTimeOffset? CompletedAtUtc { get; private set; }
    public DateTimeOffset? SkippedAtUtc { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }
    public Guid? CreatedBy { get; private set; }
    public DateTimeOffset? UpdatedAtUtc { get; private set; }
    public Guid? UpdatedBy { get; private set; }

    /// <summary>True while the wizard should still be offered (neither completed nor skipped).</summary>
    public bool IsPending => Status == OnboardingStatus.Pending;

    public static OnboardingProfile CreatePending(Guid organizationId) => new(Guid.CreateVersion7(), organizationId);

    /// <summary>Records the wizard answers and marks it complete.</summary>
    public void Complete(
        string? companyName,
        string? companyWebsite,
        string? industry,
        string? teamSize,
        string? vpnUsers,
        string? currentVpnSolution,
        OnboardingUseCase useCase,
        DateTimeOffset nowUtc)
    {
        CompanyName = Trim(companyName);
        CompanyWebsite = Trim(companyWebsite);
        Industry = Trim(industry);
        TeamSize = Trim(teamSize);
        VpnUsers = Trim(vpnUsers);
        CurrentVpnSolution = Trim(currentVpnSolution);
        UseCase = useCase;
        Status = OnboardingStatus.Completed;
        CompletedAtUtc = nowUtc;
        SkippedAtUtc = null;
    }

    /// <summary>Dismisses the wizard without answering.</summary>
    public void Skip(DateTimeOffset nowUtc)
    {
        Status = OnboardingStatus.Skipped;
        SkippedAtUtc = nowUtc;
    }

    private static string? Trim(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return trimmed.Length > MaxText ? trimmed[..MaxText] : trimmed;
    }
}
