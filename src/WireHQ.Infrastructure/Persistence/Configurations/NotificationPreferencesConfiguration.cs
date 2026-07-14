using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WireHQ.Domain.Identity;

namespace WireHQ.Infrastructure.Persistence.Configurations;

/// <summary>Per-user notification opt-ins, one row per user (unique <c>user_id</c>).</summary>
public sealed class NotificationPreferencesConfiguration : IEntityTypeConfiguration<NotificationPreferences>
{
    public void Configure(EntityTypeBuilder<NotificationPreferences> builder)
    {
        builder.ToTable("notification_preferences", "identity");
        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).ValueGeneratedNever();

        builder.HasIndex(p => p.UserId).IsUnique();
        builder.Property(p => p.SecurityAlerts).IsRequired();
        builder.Property(p => p.VpnStatusAlerts).IsRequired();
        builder.Property(p => p.ProductAnnouncements).IsRequired();
        builder.Property(p => p.BillingNotifications).IsRequired();
        builder.Property(p => p.MarketingEmails).IsRequired();
    }
}
