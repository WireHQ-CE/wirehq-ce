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

    /// <summary>The append-only SMS suppression ledger (docs/36 §4.3, Wave 4 Slice D). STOP appends; START clears (never
    /// deletes). Read as a hard precondition of every SMS send + the OTP send; written by the inbound STOP webhook under
    /// a tenant bypass with an explicit org, and by the carrier-21610 self-heal.</summary>
    DbSet<SmsOptOut> SmsOptOuts { get; }

    /// <summary>Per-(org, user) SMS consent (docs/36 §4.1, Wave 4 Slice D precondition D-3) — the queryable consent state
    /// (sender-specific: consent in org A never authorises org B). Replaces the old global
    /// <c>NotificationPreferences.SmsAlerts</c> flag; the immutable consent audit event remains the legal proof.</summary>
    DbSet<SmsConsent> SmsConsents { get; }
}
