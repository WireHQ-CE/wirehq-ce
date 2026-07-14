using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WireHQ.Domain.Platform;

namespace WireHQ.Infrastructure.Persistence.Configurations;

/// <summary>
/// The single platform-settings row (site-wide config). Not tenant-owned — it lives outside the
/// org hierarchy and is only ever touched by the Super Admin or the boot seeder. (docs/04-security.md)
/// </summary>
public sealed class PlatformSettingsConfiguration : IEntityTypeConfiguration<PlatformSettings>
{
    public void Configure(EntityTypeBuilder<PlatformSettings> builder)
    {
        builder.ToTable("platform_settings", "core");
        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).ValueGeneratedNever();

        builder.Property(p => p.TurnstileEnabled).IsRequired();
        builder.Property(p => p.TurnstileSiteKey).HasMaxLength(128);
        builder.Property(p => p.TurnstileSecretCiphertext).HasMaxLength(4096);

        builder.Property(p => p.SmtpEnabled).IsRequired();
        builder.Property(p => p.SmtpHost).HasMaxLength(256);
        builder.Property(p => p.SmtpPort).IsRequired();
        builder.Property(p => p.SmtpUsername).HasMaxLength(256);
        builder.Property(p => p.SmtpPasswordCiphertext).HasMaxLength(4096);
        builder.Property(p => p.SmtpFromEmail).HasMaxLength(320);
        builder.Property(p => p.SmtpFromName).HasMaxLength(128);
        builder.Property(p => p.SmtpUseSsl).IsRequired();

        builder.Property(p => p.AnalyticsEnabled).IsRequired();
        builder.Property(p => p.MatomoUrl).HasMaxLength(512);
        builder.Property(p => p.MatomoSiteId).HasMaxLength(32);

        builder.Property(p => p.StripeEnabled).IsRequired().HasDefaultValue(false);
        builder.Property(p => p.StripePublishableKey).HasMaxLength(256);
        builder.Property(p => p.StripeSecretCiphertext).HasMaxLength(4096);
        builder.Property(p => p.StripeWebhookSecretCiphertext).HasMaxLength(4096);
        builder.Property(p => p.StripeProMonthlyPriceId).HasMaxLength(128);
        builder.Property(p => p.StripeProAnnualPriceId).HasMaxLength(128);

        builder.Property(p => p.PricingCurrency).HasMaxLength(3).IsRequired().HasDefaultValue("GBP");
        builder.Property(p => p.ProMonthlyPrice).HasColumnType("numeric(10,2)").HasDefaultValue(29m);
        builder.Property(p => p.ProAnnualPrice).HasColumnType("numeric(10,2)").HasDefaultValue(290m);

        // Branding (docs/34) — the operator's own product name / colour / image refs; null ⇒ the shipped WireHQ brand.
        builder.Property(p => p.ProductName).HasMaxLength(64);
        builder.Property(p => p.BrandColor).HasMaxLength(7);
        builder.Property(p => p.BrandRevision).IsRequired().HasDefaultValue(0);
    }
}
