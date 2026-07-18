using FluentValidation;
using Microsoft.EntityFrameworkCore;
using WireHQ.Application.Abstractions;
using WireHQ.Application.Abstractions.Persistence;
using WireHQ.Application.Authorization;
using WireHQ.Application.Common.Messaging;
using WireHQ.Application.Entitlements;
using WireHQ.Domain.Auditing;
using WireHQ.Domain.Notifications;
using WireHQ.Shared.Results;

namespace WireHQ.Application.Features.Notifications;

/// <summary>
/// Shape gating for rule create/update (docs/35 §4.4, N-5 "channel-includes-its-rules" + Wave 3 advanced routing).
/// Folds two independent gates into the SET of entitlement keys a rule's deliveries must hold (set-valued MM-14):
/// <list type="bullet">
/// <item><b>Channel</b> — Email is free-core (no key); a Chat rule requires <c>notifications.chat</c>; SMS is Wave 4.</item>
/// <item><b>Advanced shape</b> — a multi-pattern rule (and, later, digests/quiet-hours/escalation) requires
/// <c>notifications.routing</c> on TOP of any channel key.</item>
/// </list>
/// Verifies the org currently holds EACH resulting key (channel first → <c>ChannelNotAvailable</c>; routing →
/// <c>AdvancedRequired</c>) and returns the set to store on the rule. A set is used (not one key) so revoking EITHER
/// module — the channel OR routing — stops an advanced+Chat rule's queued deliveries at the drain (docs/35 Wave 3).
/// Applied IDENTICALLY in Create and Update so an advanced/gated shape can never be smuggled in via an update.
/// </summary>
internal static class NotificationShapeGating
{
    public static async Task<Result<IReadOnlyCollection<string>>> ResolveAsync(
        ChannelKind channel, bool isAdvanced, IEntitlementService entitlements, CancellationToken cancellationToken)
    {
        var required = new HashSet<string>(StringComparer.Ordinal);

        // Channel's own feature (N-5): buying the channel includes the rules to drive it.
        string? channelFeature = channel switch
        {
            ChannelKind.Email => null, // free-core
            ChannelKind.Chat => PlanFeatures.NotificationsChat,
            _ => "unsupported", // SMS — not built yet (Wave 4)
        };
        if (channelFeature == "unsupported")
        {
            return NotificationErrors.ChannelNotAvailable;
        }

        if (channelFeature is not null)
        {
            if (!await entitlements.HasFeatureAsync(channelFeature, cancellationToken))
            {
                return NotificationErrors.ChannelNotAvailable;
            }

            required.Add(channelFeature);
        }

        // Advanced routing (multi-pattern in Slice A) requires notifications.routing on top of the channel key.
        if (isAdvanced)
        {
            if (!await entitlements.HasFeatureAsync(PlanFeatures.NotificationsRouting, cancellationToken))
            {
                return NotificationErrors.AdvancedRequired;
            }

            required.Add(PlanFeatures.NotificationsRouting);
        }

        return required.ToArray();
    }

    /// <summary>The additional patterns that survive the domain's normalisation (trimmed, non-blank, distinct, and not
    /// equal to the primary) — mirrors <c>NotificationRule.ReplaceAdditionalPatterns</c> so the advanced marker used to
    /// gate matches the shape the rule will actually persist (a rule whose extras all collapse away stays free-core).</summary>
    public static IReadOnlyCollection<string> EffectiveAdditionalPatterns(string eventPattern, IReadOnlyCollection<string>? additionalPatterns)
    {
        if (additionalPatterns is null)
        {
            return Array.Empty<string>();
        }

        var primary = eventPattern.Trim();
        return additionalPatterns
            .Select(p => p?.Trim() ?? string.Empty)
            .Where(p => p.Length > 0 && !string.Equals(p, primary, StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>Each escalation step's channel must be one the org can actually use at author time (docs/35 §5, M19): a
    /// Chat step needs <c>notifications.chat</c>; SMS is not built (Wave 4). Routing itself is required by the
    /// advanced-shape gate (an escalation chain makes the rule advanced). Rejected here so an unusable step can't be
    /// authored and then silently no-op at fire time.</summary>
    public static async Task<Error?> ValidateEscalationChannelsAsync(
        IReadOnlyCollection<EscalationStepSpec>? escalationSteps, IEntitlementService entitlements, CancellationToken cancellationToken)
    {
        if (escalationSteps is not { Count: > 0 })
        {
            return null;
        }

        foreach (var step in escalationSteps)
        {
            if (step.ChannelKind == ChannelKind.Sms)
            {
                return NotificationErrors.EscalationChannelUnavailable;
            }

            if (step.ChannelKind == ChannelKind.Chat
                && !await entitlements.HasFeatureAsync(PlanFeatures.NotificationsChat, cancellationToken))
            {
                return NotificationErrors.EscalationChannelUnavailable;
            }
        }

        return null;
    }
}

// Create / update / enable-disable / delete / send-test an org's notification rules (docs/35-notifications.md §4.4).
// All gated on the sensitive notifications.manage permission. Wave 1 delivers the free-core Email channel: a single
// Email rule is permission-only (no feature key), capped at a free quota; Chat/SMS channels are added in later waves
// and additionally require their entitlement. The audit actions are all `notifications.*`, which the route cache
// deny-lists, so managing rules can never trigger a rule (the self-loop guard).

// --- Create ---

public sealed record CreateNotificationRuleCommand(
    string Name, string EventPattern, ChannelKind ChannelKind, NotificationAudience Audience, Guid? AudienceRef,
    IReadOnlyCollection<string>? AdditionalPatterns = null, DigestCadence DigestCadence = DigestCadence.Immediate,
    TimeOnly? QuietHoursStart = null, TimeOnly? QuietHoursEnd = null, string? QuietHoursTimeZone = null,
    IReadOnlyCollection<EscalationStepSpec>? EscalationSteps = null)
    : ICommand<Guid>, IAuthorizedRequest, IRequiresVerifiedEmail
{
    /// <summary>Free-core Email rules per org before the Advanced Notifications module (notifications.routing) is
    /// required. Only free-core Email rules (empty RequiredFeatures) count toward this — routed rules are paid.</summary>
    public const int FreeEmailRuleQuota = 5;

    public IReadOnlyCollection<string> RequiredPermissions => [Permissions.Notifications.Manage];
}

public sealed class CreateNotificationRuleCommandValidator : AbstractValidator<CreateNotificationRuleCommand>
{
    public CreateNotificationRuleCommandValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(NotificationRule.MaxNameLength);
        RuleFor(x => x.EventPattern).NotEmpty().MaximumLength(NotificationRule.MaxPatternLength);
    }
}

public sealed class CreateNotificationRuleCommandHandler(
    IApplicationDbContext dbContext, ITenantContext tenant, IEntitlementService entitlements, IDateTimeProvider clock, IAuditWriter audit)
    : ICommandHandler<CreateNotificationRuleCommand, Guid>
{
    public async Task<Result<Guid>> Handle(CreateNotificationRuleCommand command, CancellationToken cancellationToken)
    {
        if (tenant.OrganizationId is not { } organizationId)
        {
            return Error.Forbidden("auth.tenant_required", "No active organization.");
        }

        // Shape gating (docs/35 §4.4): Email is free-core; a Chat rule requires notifications.chat; an ADVANCED shape —
        // multi-pattern OR a non-immediate digest cadence — additionally requires notifications.routing. Returns the SET.
        var effectiveExtras = NotificationShapeGating.EffectiveAdditionalPatterns(command.EventPattern, command.AdditionalPatterns);
        var isAdvanced = effectiveExtras.Count > 0 || command.DigestCadence != DigestCadence.Immediate
            || QuietHours.IsConfigured(command.QuietHoursStart, command.QuietHoursEnd, command.QuietHoursTimeZone)
            || command.EscalationSteps is { Count: > 0 };
        var gate = await NotificationShapeGating.ResolveAsync(command.ChannelKind, isAdvanced, entitlements, cancellationToken);
        if (gate.IsFailure)
        {
            return gate.Error;
        }

        var requiredFeatures = gate.Value;

        // Each escalation step's channel must be usable by this org (docs/35 §5, M19) — checked identically in Create/Update.
        if (await NotificationShapeGating.ValidateEscalationChannelsAsync(command.EscalationSteps, entitlements, cancellationToken) is { } escalationError)
        {
            return escalationError;
        }

        // The free quota applies only to free-core rules (empty RequiredFeatures) — routed/gated rules are paid, so a
        // routing org's advanced email rules never consume the free 5-rule allowance (finding #21). Counted client-side
        // because RequiredFeatures is a value-converted set (its Count is not SQL-translatable); the set is org-scoped
        // and small.
        if (requiredFeatures.Count == 0)
        {
            var emailFeatureSets = await dbContext.NotificationRules
                .Where(r => r.ChannelKind == ChannelKind.Email)
                .Select(r => r.RequiredFeatures)
                .ToListAsync(cancellationToken);
            if (emailFeatureSets.Count(f => f.Count == 0) >= CreateNotificationRuleCommand.FreeEmailRuleQuota)
            {
                // Beyond the free quota, a plain Email rule is itself an advanced shape (docs/35 §4.4 "email beyond the
                // free quota ⇒ notifications.routing"): if the org holds routing, persist it as a ROUTED rule; else cap it.
                if (!await entitlements.HasFeatureAsync(PlanFeatures.NotificationsRouting, cancellationToken))
                {
                    return NotificationErrors.FreeQuotaExceeded;
                }

                requiredFeatures = [PlanFeatures.NotificationsRouting];
            }
        }

        // Digest cursor init (docs/35 §4.5): a non-immediate cadence gets a concrete first cursor = the next anchor
        // boundary strictly after now (a null cursor never fires — null <= now is UNKNOWN in SQL). Null for Immediate.
        var nextDigestAtUtc = DigestSchedule.NextBoundary(command.DigestCadence, clock.UtcNow);

        var result = NotificationRule.Create(
            organizationId, command.Name, command.EventPattern, command.ChannelKind, command.Audience, command.AudienceRef,
            requiredFeatures, command.AdditionalPatterns, command.DigestCadence, nextDigestAtUtc,
            command.QuietHoursStart, command.QuietHoursEnd, command.QuietHoursTimeZone, command.EscalationSteps);
        if (result.IsFailure)
        {
            return result.Error;
        }

        var rule = result.Value;
        dbContext.NotificationRules.Add(rule);

        audit.Record("notifications.rule_created", AuditOutcome.Success, nameof(NotificationRule), rule.Id.ToString(),
            new { rule.Name, rule.EventPattern, Channel = rule.ChannelKind.ToString() });

        return rule.Id;
    }
}

// --- Update ---

public sealed record UpdateNotificationRuleCommand(
    Guid Id, string Name, string EventPattern, ChannelKind ChannelKind, NotificationAudience Audience, Guid? AudienceRef,
    IReadOnlyCollection<string>? AdditionalPatterns = null, DigestCadence DigestCadence = DigestCadence.Immediate,
    TimeOnly? QuietHoursStart = null, TimeOnly? QuietHoursEnd = null, string? QuietHoursTimeZone = null,
    IReadOnlyCollection<EscalationStepSpec>? EscalationSteps = null)
    : ICommand, IAuthorizedRequest
{
    public IReadOnlyCollection<string> RequiredPermissions => [Permissions.Notifications.Manage];
}

public sealed class UpdateNotificationRuleCommandValidator : AbstractValidator<UpdateNotificationRuleCommand>
{
    public UpdateNotificationRuleCommandValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(NotificationRule.MaxNameLength);
        RuleFor(x => x.EventPattern).NotEmpty().MaximumLength(NotificationRule.MaxPatternLength);
    }
}

public sealed class UpdateNotificationRuleCommandHandler(
    IApplicationDbContext dbContext, IEntitlementService entitlements, IDateTimeProvider clock, IAuditWriter audit)
    : ICommandHandler<UpdateNotificationRuleCommand>
{
    public async Task<Result> Handle(UpdateNotificationRuleCommand command, CancellationToken cancellationToken)
    {
        // Identical shape gating to Create (docs/35 §4.4) — an advanced/gated shape (multi-pattern OR a non-immediate
        // digest cadence) can't be smuggled in via update.
        var effectiveExtras = NotificationShapeGating.EffectiveAdditionalPatterns(command.EventPattern, command.AdditionalPatterns);
        var isAdvanced = effectiveExtras.Count > 0 || command.DigestCadence != DigestCadence.Immediate
            || QuietHours.IsConfigured(command.QuietHoursStart, command.QuietHoursEnd, command.QuietHoursTimeZone)
            || command.EscalationSteps is { Count: > 0 };
        var gate = await NotificationShapeGating.ResolveAsync(command.ChannelKind, isAdvanced, entitlements, cancellationToken);
        if (gate.IsFailure)
        {
            return gate.Error;
        }

        var requiredFeatures = gate.Value;

        // Each escalation step's channel must be usable by this org (docs/35 §5, M19) — checked identically in Create/Update.
        if (await NotificationShapeGating.ValidateEscalationChannelsAsync(command.EscalationSteps, entitlements, cancellationToken) is { } escalationError)
        {
            return escalationError;
        }

        // Normal child navs don't auto-load — Include the additional patterns so ReplaceAdditionalPatterns can diff
        // against the persisted set (the SystemRolePermissionReconciler precedent) rather than clobbering it.
        var rule = await dbContext.NotificationRules
            .Include(r => r.AdditionalPatterns)
            .Include(r => r.EscalationSteps)
            .FirstOrDefaultAsync(r => r.Id == command.Id, cancellationToken);
        if (rule is null)
        {
            return NotificationErrors.NotFound;
        }

        // If the NEW shape is free-core (empty RequiredFeatures) it must respect the free-Email quota (excluding this
        // rule) — else create-as-gated then update-to-free would bypass the cap. Only free-core email rules count.
        if (requiredFeatures.Count == 0)
        {
            var emailFeatureSets = await dbContext.NotificationRules
                .Where(r => r.ChannelKind == ChannelKind.Email && r.Id != rule.Id)
                .Select(r => r.RequiredFeatures)
                .ToListAsync(cancellationToken);
            if (emailFeatureSets.Count(f => f.Count == 0) >= CreateNotificationRuleCommand.FreeEmailRuleQuota)
            {
                // Identical to Create: beyond the free quota, promote a plain Email rule to a ROUTED rule if the org
                // holds notifications.routing, else cap it — so the gate can't be dodged via update (docs/35 §4.4).
                if (!await entitlements.HasFeatureAsync(PlanFeatures.NotificationsRouting, cancellationToken))
                {
                    return NotificationErrors.FreeQuotaExceeded;
                }

                requiredFeatures = [PlanFeatures.NotificationsRouting];
            }
        }

        // Digest cursor: preserve the existing cursor when the cadence is unchanged (so an unrelated edit doesn't skip a
        // due/overdue window); recompute the next boundary when the cadence changes (null when reverting to Immediate).
        var cadenceChanged = rule.DigestCadence != command.DigestCadence;
        var nextDigestAtUtc = cadenceChanged
            ? DigestSchedule.NextBoundary(command.DigestCadence, clock.UtcNow)
            : rule.NextDigestAtUtc;

        var result = rule.Update(
            command.Name, command.EventPattern, command.ChannelKind, command.Audience, command.AudienceRef,
            requiredFeatures, command.AdditionalPatterns, command.DigestCadence, nextDigestAtUtc,
            command.QuietHoursStart, command.QuietHoursEnd, command.QuietHoursTimeZone, command.EscalationSteps);
        if (result.IsFailure)
        {
            return result.Error;
        }

        // If the cadence changed, re-stamp this rule's still-pending jobs to the new cadence so they route to the right
        // drain phase (immediate-expand vs digest-flush). Otherwise a job captured under the old cadence would be
        // orphaned by the SQL-level filter (excluded from expand, and no longer flushed). A bounded set-based UPDATE.
        if (cadenceChanged)
        {
            await dbContext.NotificationJobs
                .Where(j => j.RuleId == rule.Id && j.Status == NotificationJobStatus.Pending)
                .ExecuteUpdateAsync(s => s.SetProperty(j => j.DigestCadence, command.DigestCadence), cancellationToken);
        }

        // Keep in-flight Escalating jobs' denormalised step count truthful for the "step X of Y" display when the chain
        // length changes (the drain's fire/settle decisions already use the LIVE rule.EscalationSteps.Count). The WHERE
        // makes it a no-op when nothing changed.
        var escalationStepCount = rule.EscalationSteps.Count;
        await dbContext.NotificationJobs
            .Where(j => j.RuleId == rule.Id && j.Status == NotificationJobStatus.Escalating && j.EscalationStepCount != escalationStepCount)
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.EscalationStepCount, escalationStepCount), cancellationToken);

        audit.Record("notifications.rule_updated", AuditOutcome.Success, nameof(NotificationRule), rule.Id.ToString(),
            new { rule.Name, rule.EventPattern, Channel = rule.ChannelKind.ToString() });

        return Result.Success();
    }
}

// --- Enable / disable ---

public sealed record SetNotificationRuleEnabledCommand(Guid Id, bool Enabled)
    : ICommand, IAuthorizedRequest
{
    public IReadOnlyCollection<string> RequiredPermissions => [Permissions.Notifications.Manage];
}

public sealed class SetNotificationRuleEnabledCommandHandler(IApplicationDbContext dbContext, IAuditWriter audit)
    : ICommandHandler<SetNotificationRuleEnabledCommand>
{
    public async Task<Result> Handle(SetNotificationRuleEnabledCommand command, CancellationToken cancellationToken)
    {
        var rule = await dbContext.NotificationRules.FirstOrDefaultAsync(r => r.Id == command.Id, cancellationToken);
        if (rule is null)
        {
            return NotificationErrors.NotFound;
        }

        if (command.Enabled)
        {
            rule.Enable();
        }
        else
        {
            rule.Disable();
        }

        audit.Record(command.Enabled ? "notifications.rule_enabled" : "notifications.rule_disabled",
            AuditOutcome.Success, nameof(NotificationRule), rule.Id.ToString());

        return Result.Success();
    }
}

// --- Delete (remove the rule + its pending jobs/deliveries — soft references, no FK) ---

public sealed record DeleteNotificationRuleCommand(Guid Id)
    : ICommand, IAuthorizedRequest
{
    public IReadOnlyCollection<string> RequiredPermissions => [Permissions.Notifications.Manage];
}

public sealed class DeleteNotificationRuleCommandHandler(IApplicationDbContext dbContext, IAuditWriter audit)
    : ICommandHandler<DeleteNotificationRuleCommand>
{
    public async Task<Result> Handle(DeleteNotificationRuleCommand command, CancellationToken cancellationToken)
    {
        var rule = await dbContext.NotificationRules.FirstOrDefaultAsync(r => r.Id == command.Id, cancellationToken);
        if (rule is null)
        {
            return NotificationErrors.NotFound;
        }

        // Jobs + deliveries reference the rule softly (no FK), so remove this rule's rows explicitly. Only the
        // not-yet-sent ones matter operationally; historical terminal rows are pruned by the sender anyway.
        var jobs = await dbContext.NotificationJobs.Where(j => j.RuleId == command.Id).ToListAsync(cancellationToken);
        dbContext.NotificationJobs.RemoveRange(jobs);
        var deliveries = await dbContext.NotificationDeliveries.Where(d => d.RuleId == command.Id).ToListAsync(cancellationToken);
        dbContext.NotificationDeliveries.RemoveRange(deliveries);
        dbContext.NotificationRules.Remove(rule);

        audit.Record("notifications.rule_deleted", AuditOutcome.Success, nameof(NotificationRule), rule.Id.ToString(), new { rule.Name });

        return Result.Success();
    }
}

// --- Send a test (delivers a sample to the acting user directly, bypassing the route cache) ---

public sealed record SendTestNotificationCommand(Guid Id)
    : ICommand, IAuthorizedRequest
{
    public IReadOnlyCollection<string> RequiredPermissions => [Permissions.Notifications.Manage];
}

public sealed class SendTestNotificationCommandHandler(
    IApplicationDbContext dbContext, ICurrentUser currentUser, IDateTimeProvider clock, IAuditWriter audit)
    : ICommandHandler<SendTestNotificationCommand>
{
    public async Task<Result> Handle(SendTestNotificationCommand command, CancellationToken cancellationToken)
    {
        var rule = await dbContext.NotificationRules.FirstOrDefaultAsync(r => r.Id == command.Id, cancellationToken);
        if (rule is null)
        {
            return NotificationErrors.NotFound;
        }

        if (string.IsNullOrWhiteSpace(currentUser.Email))
        {
            return Error.Validation("notification.no_recipient", "Your account has no email address to test with.");
        }

        var now = clock.UtcNow;
        var rendered = NotificationSummary.Test(rule.Name);
        // Wave 1: the test is always an email to the acting operator, whatever the rule's channel — it verifies the
        // pipeline (capture → deliver → send) without fanning out to the rule's whole audience.
        dbContext.NotificationDeliveries.Add(NotificationDelivery.Create(
            rule.OrganizationId, rule.Id, jobId: Guid.Empty, ChannelKind.Email, requiredFeatures: Array.Empty<string>(),
            currentUser.Email, rendered.Subject, rendered.Summary, dedupValue: null, now));

        audit.Record("notifications.test_sent", AuditOutcome.Success, nameof(NotificationRule), rule.Id.ToString());

        return Result.Success();
    }
}

// --- Acknowledge an escalation alert (stops the chain) — authenticated, PERMISSION-gated, tenant-scoped ---
// Acknowledging is a silencing action (it halts an in-flight escalation), so it is gated on the dedicated
// notifications.acknowledge permission — NOT open to any authenticated member (pre-code review B1/M7/M12). The job is
// loaded WITHOUT IgnoreQueryFilters, so the ambient tenant filter makes a cross-org jobId simply NotFound (M2). There
// is NO anonymous / emailed ack token (the review rejected that exfil surface).

public sealed record AcknowledgeNotificationAlertCommand(Guid JobId)
    : ICommand, IAuthorizedRequest
{
    public IReadOnlyCollection<string> RequiredPermissions => [Permissions.Notifications.Acknowledge];
}

public sealed class AcknowledgeNotificationAlertCommandHandler(
    IApplicationDbContext dbContext, ICurrentUser currentUser, IDateTimeProvider clock, IAuditWriter audit)
    : ICommandHandler<AcknowledgeNotificationAlertCommand>
{
    public async Task<Result> Handle(AcknowledgeNotificationAlertCommand command, CancellationToken cancellationToken)
    {
        // Tenant-scoped by the ambient org filter (NO IgnoreQueryFilters): a job outside the caller's org is NotFound.
        var job = await dbContext.NotificationJobs.FirstOrDefaultAsync(j => j.Id == command.JobId, cancellationToken);
        if (job is null)
        {
            return NotificationErrors.AlertNotFound;
        }

        // Idempotent: a job that already settled (delivered / exhausted / acknowledged) is a no-op success.
        if (job.IsEscalating)
        {
            job.Acknowledge(currentUser.UserId, clock.UtcNow);
            audit.Record("notifications.alert_acknowledged", AuditOutcome.Success, nameof(NotificationJob), job.Id.ToString());
        }

        return Result.Success();
    }
}
