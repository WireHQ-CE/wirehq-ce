using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using WireHQ.Application.Abstractions;
using WireHQ.Application.Features.Notifications;
using WireHQ.Domain.Auditing;
using WireHQ.Domain.Notifications;

namespace WireHQ.Infrastructure.Persistence.Interceptors;

/// <summary>
/// The notification <b>capture</b> stage (docs/35-notifications.md §4.1). On save it inspects the newly-added
/// <see cref="AuditLog"/> rows and, for each org-scoped entry that matches an <b>enabled</b> rule (looked up in the
/// in-memory <see cref="NotificationRouteCache"/> — <b>no query on the save path</b>), adds <b>one</b>
/// <see cref="NotificationJob"/> to the <b>same unit of work</b> as its cause. Recipient expansion happens later, off
/// the request path, in the background drain — so a bulk operation writing thousands of audit rows can never explode
/// the triggering transaction (blocker B-1). The captured summary is <b>redacted</b> (never the raw audit changes,
/// §4.6). A no-op until a rule exists (empty cache → nothing added). Kept-core.
/// </summary>
public sealed class NotificationOutboxInterceptor(
    NotificationRouteCache cache, IDateTimeProvider clock, ILogger<NotificationOutboxInterceptor> logger) : SaveChangesInterceptor
{
    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        if (eventData.Context is { } context)
        {
            await CaptureAsync(context, cancellationToken);
        }

        return await base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private async Task CaptureAsync(DbContext context, CancellationToken cancellationToken)
    {
        // Only org-scoped audit entries can drive a notification (rules are per-org). Platform/anonymous entries skip.
        var audits = context.ChangeTracker.Entries<AuditLog>()
            .Where(e => e.State == EntityState.Added && e.Entity.OrganizationId is not null)
            .Select(e => e.Entity)
            .ToList();

        if (audits.Count == 0)
        {
            return;
        }

        try
        {
            await cache.EnsureLoadedAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Never fail the unrelated business save for a notification — skip capture this once (the cache warms on
            // the sender's next tick). Same reliability trade-off as the webhook outbox.
            logger.LogWarning(ex, "Notification outbox: route cache unavailable; skipping capture for this save.");
            return;
        }

        var now = clock.UtcNow;
        List<NotificationJob>? jobs = null;
        foreach (var audit in audits)
        {
            var organizationId = audit.OrganizationId!.Value;
            var ruleIds = cache.MatchingRules(organizationId, audit.Action);
            if (ruleIds.Count == 0)
            {
                continue;
            }

            var summary = NotificationSummary.From(audit).Summary;
            foreach (var ruleId in ruleIds)
            {
                (jobs ??= []).Add(NotificationJob.Create(organizationId, ruleId, audit.Id, audit.Action, summary, now));
            }
        }

        if (jobs is not null)
        {
            // Added to the same context mid-save → EF includes them in this transaction (the outbox guarantee).
            context.Set<NotificationJob>().AddRange(jobs);
        }
    }
}
