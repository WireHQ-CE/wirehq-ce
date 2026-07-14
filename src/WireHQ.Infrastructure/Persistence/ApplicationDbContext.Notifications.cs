using Microsoft.EntityFrameworkCore;
using WireHQ.Domain.Notifications;

namespace WireHQ.Infrastructure.Persistence;

/// <summary>The notifications slice of the concrete context (docs/35-notifications.md §4.2). Kept-core — the dispatch
/// spine ships in every edition so a licence just flips the channel entitlement.</summary>
public sealed partial class ApplicationDbContext
{
    public DbSet<NotificationRule> NotificationRules => Set<NotificationRule>();

    public DbSet<NotificationJob> NotificationJobs => Set<NotificationJob>();

    public DbSet<NotificationDelivery> NotificationDeliveries => Set<NotificationDelivery>();

    public DbSet<NotificationChannelConfig> NotificationChannelConfigs => Set<NotificationChannelConfig>();

    public DbSet<NotificationChannelUsage> NotificationChannelUsage => Set<NotificationChannelUsage>();
}
