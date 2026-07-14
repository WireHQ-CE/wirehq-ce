using Microsoft.EntityFrameworkCore;
using WireHQ.Domain.Notifications;

namespace WireHQ.Application.Abstractions.Persistence;

/// <summary>
/// The notifications slice of the persistence port (docs/35-notifications.md §4.2). The routing rules + the capture
/// job + the delivery outbox + the durable per-day usage counter. Kept-core — the dispatch spine ships in <b>every</b>
/// edition so a licence just flips the channel entitlement (a stripped feature could only be code-delivered). Tenant-
/// owned in the reused <c>identity</c> schema (RLS for free — every table carries <c>organization_id</c>).
/// </summary>
public partial interface IApplicationDbContext
{
    DbSet<NotificationRule> NotificationRules { get; }

    DbSet<NotificationJob> NotificationJobs { get; }

    DbSet<NotificationDelivery> NotificationDeliveries { get; }

    DbSet<NotificationChannelConfig> NotificationChannelConfigs { get; }

    DbSet<NotificationChannelUsage> NotificationChannelUsage { get; }
}
