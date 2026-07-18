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

    // Digest flush is bounded per tick so one storming org can't monopolise the shared cross-tenant drain (docs/35 §4.5,
    // finding #15/#16): at most this many due rules per tick, at most this many events coalesced into one digest, and a
    // body-size ceiling above which the tail is summarised as "…and N more".
    private const int DigestRuleBatchSize = 50;
    private const int MaxJobsPerDigest = 200;
    private const int MaxDigestBodyChars = 4000;

    // notifications.routing lifts the durable per-org email send cap to a bounded higher value (docs/35 §4.4/§4.5,
    // Slice B). It is a raised-but-FINITE ceiling (10× the free cap), NEVER uncapped/0 — an unbounded per-org email
    // path would reopen the SMTP-reputation/spend-abuse surface B-6/N-7 exist to bound (findings #6/#16/#28).
    private const int RoutedEmailDailyCapPerOrg = 5000;

    private static readonly TimeSpan HistoryRetention = TimeSpan.FromDays(30);

    public async Task RunDueAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        scope.ServiceProvider.GetRequiredService<ISettableTenantContext>().SetBypass();
        var dbContext = scope.ServiceProvider.GetRequiredService<IApplicationDbContext>();
        var now = scope.ServiceProvider.GetRequiredService<IDateTimeProvider>().UtcNow;
        var channels = scope.ServiceProvider.GetServices<INotificationChannel>().ToDictionary(c => c.Kind);
        var effectiveFeatures = scope.ServiceProvider.GetRequiredService<IEffectiveFeatureSet>();

        await ExpandAsync(dbContext, effectiveFeatures, now, cancellationToken);
        await FlushDigestsAsync(dbContext, effectiveFeatures, now, cancellationToken);
        await SendAsync(dbContext, channels, effectiveFeatures, now, cancellationToken);
        await EscalateAsync(dbContext, effectiveFeatures, now, cancellationToken);

        await dbContext.NotificationDeliveries
            .IgnoreQueryFilters()
            .Where(d => (d.Status == NotificationDeliveryStatus.Delivered
                         || d.Status == NotificationDeliveryStatus.Failed
                         || d.Status == NotificationDeliveryStatus.Cancelled)
                        && d.CreatedAtUtc < now - HistoryRetention)
            .ExecuteDeleteAsync(cancellationToken);
    }

    // --- Expand: NotificationJob -> per-recipient NotificationDelivery rows (off the business save path) ---
    private async Task ExpandAsync(
        IApplicationDbContext dbContext, IEffectiveFeatureSet effectiveFeatures, DateTimeOffset now, CancellationToken cancellationToken)
    {
        // EXCLUDE digest jobs (DigestCadence != Immediate) at the SQL level — they are coalesced by FlushDigestsAsync.
        // A loop-level skip would livelock this ordered batch: accumulated digest jobs (oldest-first by GUID v7) would
        // permanently occupy the Take window and starve newer immediate jobs (findings livelock #5/#8/#18).
        var jobs = await dbContext.NotificationJobs
            .IgnoreQueryFilters()
            .Where(j => j.Status == NotificationJobStatus.Pending && j.DigestCadence == DigestCadence.Immediate)
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
            .Include(r => r.EscalationSteps) // needed to arm the escalation cursor when the primary is expanded (docs/35 §5)
            .Where(r => ruleIds.Contains(r.Id))
            .ToDictionaryAsync(r => r.Id, cancellationToken);

        // Expand-time entitlement skip (docs/35 §4.4): don't materialise deliveries for a rule whose required module(s)
        // are no longer in the org's live union — the send-time MM-14 guard would only Cancel them anyway. Pre-load the
        // editions for the gated rules' orgs once (client-side filter — RequiredFeatures.Count is not SQL-translatable).
        var gatedOrgIds = jobs
            .Where(j => rules.TryGetValue(j.RuleId, out var r) && r.RequiredFeatures.Count > 0)
            .Select(j => j.OrganizationId)
            .Distinct()
            .ToList();
        var entitlements = new BackgroundEntitlementResolver(dbContext, effectiveFeatures);
        await entitlements.LoadEditionsAsync(gatedOrgIds, cancellationToken);

        foreach (var job in jobs)
        {
            if (!rules.TryGetValue(job.RuleId, out var rule) || !rule.Enabled || rule.OrganizationId != job.OrganizationId)
            {
                job.MarkSkipped();
                continue;
            }

            // Every required feature must still be live (set-valued MM-14) — revoking ANY one gate skips the expand.
            if (rule.RequiredFeatures.Count > 0
                && !await AllEntitledAsync(entitlements, rule.OrganizationId, rule.RequiredFeatures, cancellationToken))
            {
                job.MarkSkipped();
                continue;
            }

            // Email fans out to per-user recipients; a chat channel is a single shared destination (one delivery per
            // event, not per user) — a shared channel post must not be multiplied by the audience size.
            IReadOnlyList<string> recipients = rule.ChannelKind == ChannelKind.Email
                ? await ResolveAudienceAsync(dbContext, rule.OrganizationId, rule.Audience, rule.AudienceRef, cancellationToken)
                : [$"{rule.ChannelKind} channel"];

            foreach (var recipient in recipients)
            {
                var dedup = DedupValue(job, recipient, rule.ChannelKind);
                dbContext.NotificationDeliveries.Add(NotificationDelivery.Create(
                    rule.OrganizationId, rule.Id, job.Id, rule.ChannelKind, rule.RequiredFeatures,
                    recipient, $"WireHQ: {rule.Name}", job.SummarySnapshot, dedup, now,
                    rule.QuietHoursStart, rule.QuietHoursEnd, rule.QuietHoursTimeZone));
            }

            // A rule with an escalation chain stays LIVE (Escalating) so EscalateAsync can fire the next step when it
            // comes due; the first step is due step[0].DelayMinutes after now (docs/35 §5). Escalation is Immediate-only
            // (validated), so this never runs for a digest job.
            if (rule.HasEscalation)
            {
                var firstStep = rule.EscalationSteps.OrderBy(s => s.StepOrder).First();
                job.BeginEscalating(rule.EscalationSteps.Count, now.AddMinutes(firstStep.DelayMinutes));
            }
            else
            {
                job.MarkExpanded();
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    /// <summary>True only when EVERY feature in the set is in the org's live union (set-valued MM-14, docs/35 Wave 3).</summary>
    private static async Task<bool> AllEntitledAsync(
        BackgroundEntitlementResolver entitlements, Guid organizationId, IReadOnlyCollection<string> features, CancellationToken cancellationToken)
    {
        foreach (var feature in features)
        {
            if (!await entitlements.IsEntitledAsync(organizationId, feature, cancellationToken))
            {
                return false;
            }
        }

        return true;
    }

    // --- Flush digests: coalesce a digest rule's window of matched events into ONE periodic message (docs/35 §4.5) ---
    private async Task FlushDigestsAsync(
        IApplicationDbContext dbContext, IEffectiveFeatureSet effectiveFeatures, DateTimeOffset now, CancellationToken cancellationToken)
    {
        // Due digest rules only (cadence != Immediate AND cursor due). Bounded per tick so one storming org can't
        // monopolise the shared cross-tenant drain (finding #15). Digest jobs live OUTSIDE the immediate-expand pool
        // (DigestCadence != Immediate), so this phase never competes with ExpandAsync for its batch.
        var dueRules = await dbContext.NotificationRules
            .IgnoreQueryFilters()
            .Where(r => r.Enabled && r.DigestCadence != DigestCadence.Immediate && r.NextDigestAtUtc != null && r.NextDigestAtUtc <= now)
            .OrderBy(r => r.NextDigestAtUtc)
            .Take(DigestRuleBatchSize)
            .ToListAsync(cancellationToken);

        if (dueRules.Count == 0)
        {
            return;
        }

        // A digest rule is advanced (requires notifications.routing) — if the org lost the module, skip the flush (drop
        // the gathered jobs) but STILL advance the cursor. Pre-load the gated rules' orgs' editions once.
        var gatedOrgIds = dueRules.Where(r => r.RequiredFeatures.Count > 0).Select(r => r.OrganizationId).Distinct().ToList();
        var entitlements = new BackgroundEntitlementResolver(dbContext, effectiveFeatures);
        await entitlements.LoadEditionsAsync(gatedOrgIds, cancellationToken);

        foreach (var rule in dueRules)
        {
            var entitled = rule.RequiredFeatures.Count == 0
                || await AllEntitledAsync(entitlements, rule.OrganizationId, rule.RequiredFeatures, cancellationToken);

            // Gather this rule's pending jobs (scoped by RuleId, which is per-org), bounded (finding #15/#16).
            var jobs = await dbContext.NotificationJobs
                .IgnoreQueryFilters()
                .Where(j => j.RuleId == rule.Id && j.Status == NotificationJobStatus.Pending)
                .OrderBy(j => j.Id)
                .Take(MaxJobsPerDigest)
                .ToListAsync(cancellationToken);

            if (entitled && jobs.Count > 0)
            {
                var (subject, body) = BuildDigest(rule, jobs);

                // Org-scoped recipients (blocker B-2/#23) via the SAME helper immediate delivery uses — Chat stays one
                // synthetic recipient (a digest posts once, not per user); Email fans per verified user.
                IReadOnlyList<string> recipients = rule.ChannelKind == ChannelKind.Email
                    ? await ResolveAudienceAsync(dbContext, rule.OrganizationId, rule.Audience, rule.AudienceRef, cancellationToken)
                    : [$"{rule.ChannelKind} channel"];

                foreach (var recipient in recipients)
                {
                    dbContext.NotificationDeliveries.Add(NotificationDelivery.Create(
                        rule.OrganizationId, rule.Id, jobId: Guid.Empty, rule.ChannelKind, rule.RequiredFeatures,
                        recipient, subject, body, dedupValue: null, now,
                        rule.QuietHoursStart, rule.QuietHoursEnd, rule.QuietHoursTimeZone));
                }

                foreach (var job in jobs)
                {
                    job.MarkExpanded();
                }
            }
            else
            {
                // Un-entitled, or nothing to coalesce — drop the gathered jobs so they leave the pending pool (mirrors
                // the expand-time entitlement skip). Any jobs beyond the per-digest cap wait for the next window.
                foreach (var job in jobs)
                {
                    job.MarkSkipped();
                }
            }

            // ALWAYS advance the cursor to the next boundary strictly after now — even a zero-job or un-entitled window —
            // so the cursor never stays <= now and re-fires forever (finding cursor-advance #14; no catch-up storm).
            rule.AdvanceDigestCursor(DigestSchedule.NextBoundary(rule.DigestCadence, now));
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    // Coalesce a digest rule's gathered events into one subject + plain-text body. The per-event lines are the already
    // REDACTED job summaries (never raw audit changes, §4.6); the body is capped and its tail summarised as "…and N more".
    private static (string Subject, string Body) BuildDigest(NotificationRule rule, IReadOnlyList<NotificationJob> jobs)
    {
        var cadence = rule.DigestCadence.ToString().ToLowerInvariant();
        var count = jobs.Count;
        var subject = $"WireHQ: {rule.Name} — {cadence} digest ({count} event{(count == 1 ? string.Empty : "s")})";

        var body = new StringBuilder();
        body.Append($"Your {cadence} digest for \"{rule.Name}\": {count} event{(count == 1 ? string.Empty : "s")}.\n\n");
        var rendered = 0;
        foreach (var job in jobs)
        {
            var line = $"• {job.SummarySnapshot}\n";
            if (body.Length + line.Length > MaxDigestBodyChars)
            {
                break;
            }

            body.Append(line);
            rendered++;
        }

        if (rendered < count)
        {
            body.Append($"…and {count - rendered} more.");
        }

        return (subject, body.ToString());
    }

    // --- Escalate: fire the next chain step for jobs whose ack deadline passed unacknowledged (docs/35 §5, Wave 3) ---
    private async Task EscalateAsync(
        IApplicationDbContext dbContext, IEffectiveFeatureSet effectiveFeatures, DateTimeOffset now, CancellationToken cancellationToken)
    {
        // Due escalation jobs only, via a SQL-level filter on the dedicated Escalating status (NOT an in-memory scan of
        // the Expanded pool — the digest-livelock precedent, findings B2/B4). Unacknowledged, cursor due, bounded per
        // tick, oldest-cursor first so one storming org can't monopolise the shared cross-tenant drain.
        var dueJobs = await dbContext.NotificationJobs
            .IgnoreQueryFilters()
            .Where(j => j.Status == NotificationJobStatus.Escalating
                        && j.AcknowledgedAtUtc == null
                        && j.EscalationNextDueAtUtc != null && j.EscalationNextDueAtUtc <= now)
            .OrderBy(j => j.EscalationNextDueAtUtc)
            .Take(DigestRuleBatchSize)
            .ToListAsync(cancellationToken);

        if (dueJobs.Count == 0)
        {
            return;
        }

        var ruleIds = dueJobs.Select(j => j.RuleId).Distinct().ToList();
        var rules = await dbContext.NotificationRules
            .IgnoreQueryFilters()
            .Include(r => r.EscalationSteps)
            .Where(r => ruleIds.Contains(r.Id))
            .ToDictionaryAsync(r => r.Id, cancellationToken);

        // Escalation is advanced (notifications.routing) + each step's channel is gated — re-check the live union per org
        // (the MM-14 pattern, findings M6/M8) so a lapsed module settles the chain / skips an un-entitled step.
        var orgIds = dueJobs.Select(j => j.OrganizationId).Distinct().ToList();
        var entitlements = new BackgroundEntitlementResolver(dbContext, effectiveFeatures);
        await entitlements.LoadEditionsAsync(orgIds, cancellationToken);

        foreach (var job in dueJobs)
        {
            // Rule gone (soft ref), DISABLED (an operator's disable must silence an in-flight chain, mirroring
            // ExpandAsync + Matches), or its chain shrank below the current level → nothing to fire; settle.
            if (!rules.TryGetValue(job.RuleId, out var rule) || !rule.Enabled || rule.OrganizationId != job.OrganizationId
                || job.EscalationLevel >= rule.EscalationSteps.Count)
            {
                job.SettleEscalation();
                continue;
            }

            // The whole advanced feature must still be live; if routing lapsed, stop escalating.
            if (!await entitlements.IsEntitledAsync(job.OrganizationId, PlanFeatures.NotificationsRouting, cancellationToken))
            {
                job.SettleEscalation();
                continue;
            }

            var steps = rule.EscalationSteps.OrderBy(s => s.StepOrder).ToList();
            var step = steps[job.EscalationLevel];
            var level = job.EscalationLevel + 1;

            // Fire the step UNLESS its channel module was revoked (skip creating, but STILL advance so the chain
            // continues to a step that may be entitled — finding B9). Escalation IGNORES quiet hours (time-critical —
            // M17): quiet-window = null, due NOW. Email fans out; Chat is one synthetic recipient (M18).
            var channelLive = step.ChannelKind == ChannelKind.Email
                || await entitlements.IsEntitledAsync(job.OrganizationId, PlanFeatures.NotificationsChat, cancellationToken);
            if (channelLive)
            {
                var stepFeatures = StepRequiredFeatures(step.ChannelKind);
                IReadOnlyList<string> recipients = step.ChannelKind == ChannelKind.Email
                    ? await ResolveAudienceAsync(dbContext, job.OrganizationId, step.Audience, step.AudienceRef, cancellationToken)
                    : [$"{step.ChannelKind} channel"];

                foreach (var recipient in recipients)
                {
                    dbContext.NotificationDeliveries.Add(NotificationDelivery.Create(
                        job.OrganizationId, rule.Id, job.Id, step.ChannelKind, stepFeatures,
                        recipient, $"WireHQ: {rule.Name} (escalation {level})", job.SummarySnapshot, dedupValue: null, now,
                        quietHoursStart: null, quietHoursEnd: null, quietHoursTimeZone: null, escalationLevel: level));
                }
            }

            // Advance: the FOLLOWING step's cursor is now + its delay, or null when the chain is now exhausted (settles).
            var nextDue = level < steps.Count ? now.AddMinutes(steps[level].DelayMinutes) : (DateTimeOffset?)null;
            job.AdvanceEscalation(nextDue);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    // An escalation step's required-feature set: routing always (it's an advanced feature), plus the channel's own key.
    private static IReadOnlyCollection<string> StepRequiredFeatures(ChannelKind channel) => channel switch
    {
        ChannelKind.Chat => [PlanFeatures.NotificationsRouting, PlanFeatures.NotificationsChat],
        _ => [PlanFeatures.NotificationsRouting],
    };

    /// <summary>Resolve an audience — <b>org-scoped</b>, de-duplicated, verified-email addresses only. Parameterised by
    /// audience (not the whole rule) so an escalation step's OWN audience resolves through the same path (docs/35 §5).</summary>
    private static async Task<IReadOnlyList<string>> ResolveAudienceAsync(
        IApplicationDbContext dbContext, Guid organizationId, NotificationAudience audience, Guid? audienceRef, CancellationToken cancellationToken)
    {
        // Members of the org (scoped explicitly to organizationId — NOT a cross-tenant join).
        var memberQuery = dbContext.Memberships
            .IgnoreQueryFilters()
            .Where(m => m.OrganizationId == organizationId && m.Status == MembershipStatus.Active && !m.IsDeleted);

        if (audience == NotificationAudience.Role && audienceRef is { } roleId)
        {
            memberQuery = memberQuery.Where(m => m.Roles.Any(r => r.RoleId == roleId));
        }

        var userIds = await memberQuery.Select(m => m.UserId).Distinct().ToListAsync(cancellationToken);
        if (userIds.Count == 0)
        {
            return [];
        }

        // OptedInUsers honours the per-user preference (Wave 1 maps it to the security-alerts opt-in, default on).
        if (audience == NotificationAudience.OptedInUsers)
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
        // a deactivated Chat/SMS/routing module stops sends even for already-queued rows. Set-valued (docs/35 Wave 3):
        // EVERY key in RequiredFeatures must be live, so revoking ANY one of a rule's modules stops it. Shared with the
        // webhook + directory drains via BackgroundEntitlementResolver (docs/33 §5.4): resolve each gated org's edition
        // once and cache the union per distinct edition (install-global, so it depends only on edition).
        var gatedOrgIds = due.Where(d => d.RequiredFeatures.Count > 0).Select(d => d.OrganizationId).Distinct().ToList();
        // Also resolve routing entitlement for every org with a due EMAIL delivery (even free-core ones, whose
        // RequiredFeatures is empty), so the per-org email daily cap can be chosen below without an extra query.
        var emailOrgIds = due.Where(d => d.ChannelKind == ChannelKind.Email).Select(d => d.OrganizationId).Distinct().ToList();
        var entitlements = new BackgroundEntitlementResolver(dbContext, effectiveFeatures);
        await entitlements.LoadEditionsAsync(gatedOrgIds.Concat(emailOrgIds).Distinct(), cancellationToken);

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
            if (delivery.RequiredFeatures.Count > 0
                && !await AllEntitledAsync(entitlements, delivery.OrganizationId, delivery.RequiredFeatures, cancellationToken))
            {
                delivery.Cancel("Channel module is not active for this organisation");
                continue;
            }

            // Quiet hours (docs/35 §5): if the rule's window (copied onto the delivery) is active NOW, DEFER to its end
            // rather than send — enforced here at SEND time against the current instant, so a delivery that became due
            // DURING quiet hours is held, and one whose window has since passed goes straight out. A defer is not a
            // failed attempt (Attempts/CreatedAtUtc untouched); an unentitled delivery is Cancelled above, never deferred.
            if (delivery.QuietDeferUntil(now) is { } deferUntil)
            {
                delivery.Defer(deferUntil);
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
            // notifications.routing lifts the ceiling to a bounded higher value (Slice B) — never uncapped (#6/#16/#28).
            var usage = await GetUsageAsync(dbContext, usageCache, delivery.OrganizationId, delivery.ChannelKind, today, cancellationToken);
            if (delivery.ChannelKind == ChannelKind.Email)
            {
                var emailCap = await entitlements.IsEntitledAsync(delivery.OrganizationId, PlanFeatures.NotificationsRouting, cancellationToken)
                    ? RoutedEmailDailyCapPerOrg
                    : EmailDailyCapPerOrg;
                if (usage.HasReached(emailCap))
                {
                    delivery.Cancel("Daily email limit reached for this organisation");
                    continue;
                }
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
