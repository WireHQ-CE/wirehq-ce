using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WireHQ.Application.Abstractions;
using WireHQ.Application.Abstractions.Notifications;
using WireHQ.Application.Abstractions.Persistence;
using WireHQ.Application.Entitlements;
using WireHQ.Domain.Auditing;
using WireHQ.Domain.Memberships;
using WireHQ.Domain.Notifications;

namespace WireHQ.Application.Features.Notifications;

/// <summary>
/// Drains the notification spine (docs/35-notifications.md §4.1/§4.5). Each tick, in a cross-tenant bypass scope:
/// <list type="number">
/// <item><b>Expand</b> — turn each <c>Pending</c> <see cref="NotificationJob"/> into per-recipient
/// <see cref="NotificationDelivery"/> rows. Audience resolution is <b>scoped to the job's org</b> (never a
/// cross-tenant <c>IgnoreQueryFilters</c> join — blocker B-2) and de-duplicated per recipient.</item>
/// <item><b>Send</b> — dispatch each due delivery via its channel adapter, enforcing the <b>durable</b> per-org
/// email cap (B-6) and re-checking the live entitlement union for gated channels (MM-14; free-core Email skips it),
/// with exponential backoff.</item>
/// <item><b>Prune</b> — delete terminal rows older than the retention window so the outbox stays bounded.</item>
/// </list>
/// Driven by the Api host's notification sender service; also invoked directly by tests. Singleton.
/// </summary>
public sealed class NotificationDispatchScheduler(IServiceScopeFactory scopeFactory, ILogger<NotificationDispatchScheduler> logger)
{
    private const int JobBatchSize = 100;
    private const int DeliveryBatchSize = 200;
    private const int EmailDailyCapPerOrg = 500;
    private static readonly TimeSpan HistoryRetention = TimeSpan.FromDays(30);

    public async Task RunDueAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        scope.ServiceProvider.GetRequiredService<ISettableTenantContext>().SetBypass();
        var dbContext = scope.ServiceProvider.GetRequiredService<IApplicationDbContext>();
        var now = scope.ServiceProvider.GetRequiredService<IDateTimeProvider>().UtcNow;
        var channels = scope.ServiceProvider.GetServices<INotificationChannel>().ToDictionary(c => c.Kind);
        var effectiveFeatures = scope.ServiceProvider.GetRequiredService<IEffectiveFeatureSet>();

        await ExpandAsync(dbContext, now, cancellationToken);
        await SendAsync(dbContext, channels, effectiveFeatures, now, cancellationToken);

        await dbContext.NotificationDeliveries
            .IgnoreQueryFilters()
            .Where(d => (d.Status == NotificationDeliveryStatus.Delivered
                         || d.Status == NotificationDeliveryStatus.Failed
                         || d.Status == NotificationDeliveryStatus.Cancelled)
                        && d.CreatedAtUtc < now - HistoryRetention)
            .ExecuteDeleteAsync(cancellationToken);
    }

    // --- Expand: NotificationJob -> per-recipient NotificationDelivery rows (off the business save path) ---
    private async Task ExpandAsync(IApplicationDbContext dbContext, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var jobs = await dbContext.NotificationJobs
            .IgnoreQueryFilters()
            .Where(j => j.Status == NotificationJobStatus.Pending)
            .OrderBy(j => j.Id)
            .Take(JobBatchSize)
            .ToListAsync(cancellationToken);

        if (jobs.Count == 0)
        {
            return;
        }

        var ruleIds = jobs.Select(j => j.RuleId).Distinct().ToList();
        var rules = await dbContext.NotificationRules
            .IgnoreQueryFilters()
            .Where(r => ruleIds.Contains(r.Id))
            .ToDictionaryAsync(r => r.Id, cancellationToken);

        foreach (var job in jobs)
        {
            if (!rules.TryGetValue(job.RuleId, out var rule) || !rule.Enabled || rule.OrganizationId != job.OrganizationId)
            {
                job.MarkSkipped();
                continue;
            }

            // Email fans out to per-user recipients; a chat channel is a single shared destination (one delivery per
            // event, not per user) — a shared channel post must not be multiplied by the audience size.
            IReadOnlyList<string> recipients = rule.ChannelKind == ChannelKind.Email
                ? await ResolveRecipientsAsync(dbContext, rule, cancellationToken)
                : [$"{rule.ChannelKind} channel"];

            foreach (var recipient in recipients)
            {
                var dedup = DedupValue(job, recipient, rule.ChannelKind);
                dbContext.NotificationDeliveries.Add(NotificationDelivery.Create(
                    rule.OrganizationId, rule.Id, job.Id, rule.ChannelKind, rule.RequiredFeature,
                    recipient, $"WireHQ: {rule.Name}", job.SummarySnapshot, dedup, now));
            }

            job.MarkExpanded();
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Resolve a rule's audience — <b>org-scoped</b>, de-duplicated, verified-email addresses only.</summary>
    private static async Task<IReadOnlyList<string>> ResolveRecipientsAsync(
        IApplicationDbContext dbContext, NotificationRule rule, CancellationToken cancellationToken)
    {
        // Members of the rule's org (scoped explicitly to rule.OrganizationId — NOT a cross-tenant join).
        var memberQuery = dbContext.Memberships
            .IgnoreQueryFilters()
            .Where(m => m.OrganizationId == rule.OrganizationId && m.Status == MembershipStatus.Active && !m.IsDeleted);

        if (rule.Audience == NotificationAudience.Role && rule.AudienceRef is { } roleId)
        {
            memberQuery = memberQuery.Where(m => m.Roles.Any(r => r.RoleId == roleId));
        }

        var userIds = await memberQuery.Select(m => m.UserId).Distinct().ToListAsync(cancellationToken);
        if (userIds.Count == 0)
        {
            return [];
        }

        // OptedInUsers honours the per-user preference (Wave 1 maps it to the security-alerts opt-in, default on).
        if (rule.Audience == NotificationAudience.OptedInUsers)
        {
            var optedOut = await dbContext.NotificationPreferences
                .IgnoreQueryFilters()
                .Where(p => userIds.Contains(p.UserId) && !p.SecurityAlerts)
                .Select(p => p.UserId)
                .ToListAsync(cancellationToken);
            userIds = userIds.Except(optedOut).ToList();
        }

        var users = await dbContext.Users
            .IgnoreQueryFilters()
            .Where(u => userIds.Contains(u.Id) && !u.IsDeleted && u.EmailVerified)
            .ToListAsync(cancellationToken);

        return users
            .Select(u => u.Email.Value)
            .Where(e => !string.IsNullOrWhiteSpace(e))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // --- Send: dispatch due deliveries via channel adapters, re-checking entitlement + loading channel config ---
    private async Task SendAsync(
        IApplicationDbContext dbContext, IReadOnlyDictionary<ChannelKind, INotificationChannel> channels,
        IEffectiveFeatureSet effectiveFeatures, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var due = await dbContext.NotificationDeliveries
            .IgnoreQueryFilters()
            .Where(d => d.Status == NotificationDeliveryStatus.Pending && d.NextAttemptAtUtc <= now)
            .OrderBy(d => d.NextAttemptAtUtc)
            .Take(DeliveryBatchSize)
            .ToListAsync(cancellationToken);

        if (due.Count == 0)
        {
            return;
        }

        // MM-14 data-plane deactivation guard (docs/35 §4.4): a gated delivery must still be entitled at SEND time —
        // a deactivated Chat/SMS/routing module stops sends even for already-queued rows. Shared with the webhook +
        // directory drains via BackgroundEntitlementResolver (docs/33 §5.4): resolve each gated org's edition once and
        // cache the union per distinct edition (install-global, so it depends only on edition).
        var gatedOrgIds = due.Where(d => d.RequiredFeature is not null).Select(d => d.OrganizationId).Distinct().ToList();
        var entitlements = new BackgroundEntitlementResolver(dbContext, effectiveFeatures);
        await entitlements.LoadEditionsAsync(gatedOrgIds, cancellationToken);

        // Per-org channel config (destination/credential) for non-Email deliveries.
        var configOrgIds = due.Where(d => d.ChannelKind != ChannelKind.Email).Select(d => d.OrganizationId).Distinct().ToList();
        var configs = new Dictionary<(Guid, ChannelKind), NotificationChannelConfig>();
        if (configOrgIds.Count > 0)
        {
            var loaded = await dbContext.NotificationChannelConfigs
                .IgnoreQueryFilters()
                .Where(c => configOrgIds.Contains(c.OrganizationId))
                .ToListAsync(cancellationToken);
            foreach (var config in loaded)
            {
                configs[(config.OrganizationId, config.ChannelKind)] = config;
            }
        }

        var today = DateOnly.FromDateTime(now.UtcDateTime);
        var usageCache = new Dictionary<(Guid, ChannelKind), NotificationChannelUsage>();

        foreach (var delivery in due)
        {
            if (delivery.RequiredFeature is { } feature
                && !await entitlements.IsEntitledAsync(delivery.OrganizationId, feature, cancellationToken))
            {
                delivery.Cancel("Channel module is not active for this organisation");
                continue;
            }

            if (!channels.TryGetValue(delivery.ChannelKind, out var channel))
            {
                delivery.Cancel($"No adapter registered for channel {delivery.ChannelKind}");
                continue;
            }

            NotificationChannelConfig? channelConfig = null;
            if (delivery.ChannelKind != ChannelKind.Email)
            {
                configs.TryGetValue((delivery.OrganizationId, delivery.ChannelKind), out channelConfig);
                if (channelConfig is null || !channelConfig.Enabled || string.IsNullOrWhiteSpace(channelConfig.DestinationUrl))
                {
                    // Not-yet-configured / temporarily disabled is TRANSIENT (an operator may set the destination shortly
                    // after creating the rule), so retry with backoff rather than terminally drop the event. Cancel stays
                    // reserved for genuinely terminal causes (a deactivated module, above).
                    delivery.MarkFailed(null, $"No {delivery.ChannelKind} destination is configured yet", now);
                    continue;
                }
            }

            // Durable per-org daily cap (B-6): the Email channel is free but not free of limits (SMTP reputation).
            var usage = await GetUsageAsync(dbContext, usageCache, delivery.OrganizationId, delivery.ChannelKind, today, cancellationToken);
            if (delivery.ChannelKind == ChannelKind.Email && usage.HasReached(EmailDailyCapPerOrg))
            {
                delivery.Cancel("Daily email limit reached for this organisation");
                continue;
            }

            try
            {
                var result = await channel.SendAsync(
                    new ChannelSendRequest(delivery.OrganizationId, delivery.Recipient, delivery.RenderedSubject, delivery.RenderedBody, delivery.DedupValue, channelConfig),
                    cancellationToken);

                if (result.Success)
                {
                    delivery.MarkSucceeded(result.StatusCode, now);
                    usage.Increment();
                }
                else
                {
                    delivery.MarkFailed(result.StatusCode, result.Error, now);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Notification delivery {DeliveryId} failed to send.", delivery.Id);
                delivery.MarkFailed(null, ex.Message, now);
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static async Task<NotificationChannelUsage> GetUsageAsync(
        IApplicationDbContext dbContext, Dictionary<(Guid, ChannelKind), NotificationChannelUsage> cache,
        Guid organizationId, ChannelKind channel, DateOnly today, CancellationToken cancellationToken)
    {
        var key = (organizationId, channel);
        if (cache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var usage = await dbContext.NotificationChannelUsage
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(u => u.OrganizationId == organizationId && u.ChannelKind == channel && u.DayUtc == today, cancellationToken);

        if (usage is null)
        {
            usage = NotificationChannelUsage.Start(organizationId, channel, today);
            dbContext.NotificationChannelUsage.Add(usage);
        }

        cache[key] = usage;
        return usage;
    }

    private static string DedupValue(NotificationJob job, string recipient, ChannelKind channel)
    {
        var raw = $"{job.OrganizationId:N}|{job.Id:N}|{channel}|{recipient.ToLowerInvariant()}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)))[..32];
    }
}
