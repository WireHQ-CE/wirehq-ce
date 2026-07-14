using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WireHQ.Domain.Onboarding;

namespace WireHQ.Infrastructure.Persistence.Configurations;

/// <summary>
/// The optional onboarding answers for an organization — one row per org (unique <c>organization_id</c>).
/// Tenant-owned, so the global query filter scopes it to the active org automatically.
/// </summary>
public sealed class OnboardingProfileConfiguration : IEntityTypeConfiguration<OnboardingProfile>
{
    public void Configure(EntityTypeBuilder<OnboardingProfile> builder)
    {
        builder.ToTable("onboarding_profiles", "core");
        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).ValueGeneratedNever();

        builder.HasIndex(p => p.OrganizationId).IsUnique();

        builder.Property(p => p.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(p => p.UseCase).HasConversion<string>().HasMaxLength(32).IsRequired();

        builder.Property(p => p.CompanyName).HasMaxLength(OnboardingProfile.MaxText);
        builder.Property(p => p.CompanyWebsite).HasMaxLength(OnboardingProfile.MaxText);
        builder.Property(p => p.Industry).HasMaxLength(OnboardingProfile.MaxText);
        builder.Property(p => p.TeamSize).HasMaxLength(OnboardingProfile.MaxText);
        builder.Property(p => p.VpnUsers).HasMaxLength(OnboardingProfile.MaxText);
        builder.Property(p => p.CurrentVpnSolution).HasMaxLength(OnboardingProfile.MaxText);
    }
}
