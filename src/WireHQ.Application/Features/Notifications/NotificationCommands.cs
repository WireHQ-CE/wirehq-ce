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

/// <summary>The entitlement feature a rule for a channel needs (null = free-core Email).</summary>
public sealed record ChannelGate(string? RequiredFeature);

/// <summary>
/// Channel gating for rule create/update (docs/35 §4.4, N-5 "channel-includes-its-rules"): Email is free-core (no
/// feature key); a Chat rule requires <c>notifications.chat</c>; SMS is a later wave. Resolves the required feature
/// and verifies the org holds it, failing with <c>ChannelNotAvailable</c> otherwise.
/// </summary>
internal static class NotificationChannelGating
{
    public static async Task<Result<ChannelGate>> ResolveAsync(
        ChannelKind channel, IEntitlementService entitlements, CancellationToken cancellationToken)
    {
        string? feature;
        switch (channel)
        {
            case ChannelKind.Email: feature = null; break;
            case ChannelKind.Chat: feature = PlanFeatures.NotificationsChat; break;
            default: return NotificationErrors.ChannelNotAvailable; // SMS — not built yet (Wave 4)
        }

        if (feature is not null && !await entitlements.HasFeatureAsync(feature, cancellationToken))
        {
            return NotificationErrors.ChannelNotAvailable;
        }

        return new ChannelGate(feature);
    }
}

// Create / update / enable-disable / delete / send-test an org's notification rules (docs/35-notifications.md §4.4).
// All gated on the sensitive notifications.manage permission. Wave 1 delivers the free-core Email channel: a single
// Email rule is permission-only (no feature key), capped at a free quota; Chat/SMS channels are added in later waves
// and additionally require their entitlement. The audit actions are all `notifications.*`, which the route cache
// deny-lists, so managing rules can never trigger a rule (the self-loop guard).

// --- Create ---

public sealed record CreateNotificationRuleCommand(
    string Name, string EventPattern, ChannelKind ChannelKind, NotificationAudience Audience, Guid? AudienceRef)
    : ICommand<Guid>, IAuthorizedRequest, IRequiresVerifiedEmail
{
    /// <summary>Free-core Email rules per org before the (later) Advanced Notifications module is required.</summary>
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
    IApplicationDbContext dbContext, ITenantContext tenant, IEntitlementService entitlements, IAuditWriter audit)
    : ICommandHandler<CreateNotificationRuleCommand, Guid>
{
    public async Task<Result<Guid>> Handle(CreateNotificationRuleCommand command, CancellationToken cancellationToken)
    {
        if (tenant.OrganizationId is not { } organizationId)
        {
            return Error.Forbidden("auth.tenant_required", "No active organization.");
        }

        // Channel gating (docs/35 §4.4): Email is free-core; a Chat rule requires the notifications.chat entitlement.
        var gate = await NotificationChannelGating.ResolveAsync(command.ChannelKind, entitlements, cancellationToken);
        if (gate.IsFailure)
        {
            return gate.Error;
        }

        // The free quota applies only to free-core Email rules; a gated Chat rule is paid, so it isn't capped.
        if (gate.Value.RequiredFeature is null)
        {
            var emailRules = await dbContext.NotificationRules.CountAsync(r => r.ChannelKind == ChannelKind.Email, cancellationToken);
            if (emailRules >= CreateNotificationRuleCommand.FreeEmailRuleQuota)
            {
                return NotificationErrors.FreeQuotaExceeded;
            }
        }

        var result = NotificationRule.Create(
            organizationId, command.Name, command.EventPattern, command.ChannelKind, command.Audience, command.AudienceRef, gate.Value.RequiredFeature);
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
    Guid Id, string Name, string EventPattern, ChannelKind ChannelKind, NotificationAudience Audience, Guid? AudienceRef)
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
    IApplicationDbContext dbContext, IEntitlementService entitlements, IAuditWriter audit)
    : ICommandHandler<UpdateNotificationRuleCommand>
{
    public async Task<Result> Handle(UpdateNotificationRuleCommand command, CancellationToken cancellationToken)
    {
        var gate = await NotificationChannelGating.ResolveAsync(command.ChannelKind, entitlements, cancellationToken);
        if (gate.IsFailure)
        {
            return gate.Error;
        }

        var rule = await dbContext.NotificationRules.FirstOrDefaultAsync(r => r.Id == command.Id, cancellationToken);
        if (rule is null)
        {
            return NotificationErrors.NotFound;
        }

        // Converting a (paid, uncapped) Chat rule into a free-core Email rule must respect the free-Email quota
        // (excluding this rule) — else create-as-Chat then update-to-Email would bypass the cap.
        if (gate.Value.RequiredFeature is null && rule.ChannelKind != ChannelKind.Email)
        {
            var emailRules = await dbContext.NotificationRules.CountAsync(
                r => r.ChannelKind == ChannelKind.Email && r.Id != rule.Id, cancellationToken);
            if (emailRules >= CreateNotificationRuleCommand.FreeEmailRuleQuota)
            {
                return NotificationErrors.FreeQuotaExceeded;
            }
        }

        var result = rule.Update(command.Name, command.EventPattern, command.ChannelKind, command.Audience, command.AudienceRef, gate.Value.RequiredFeature);
        if (result.IsFailure)
        {
            return result.Error;
        }

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
            rule.OrganizationId, rule.Id, jobId: Guid.Empty, ChannelKind.Email, requiredFeature: null,
            currentUser.Email, rendered.Subject, rendered.Summary, dedupValue: null, now));

        audit.Record("notifications.test_sent", AuditOutcome.Success, nameof(NotificationRule), rule.Id.ToString());

        return Result.Success();
    }
}
