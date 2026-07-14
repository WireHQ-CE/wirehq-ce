using Microsoft.EntityFrameworkCore;
using WireHQ.Application.Abstractions;
using WireHQ.Application.Abstractions.Persistence;
using WireHQ.Application.Common.Messaging;
using WireHQ.Domain.Auditing;
using WireHQ.Domain.Identity;
using WireHQ.Shared.Results;

namespace WireHQ.Application.Features.Account.Notifications;

// The signed-in user's notification opt-ins. Stored per user; created on first read with sensible
// defaults. These are preferences only — outbound notification/email flows consult them when they exist.

public sealed record NotificationPreferencesResponse(
    bool SecurityAlerts,
    bool VpnStatusAlerts,
    bool ProductAnnouncements,
    bool BillingNotifications,
    bool MarketingEmails,
    bool ServiceStatusAlerts);

/// <summary>Returns the current user's notification preferences (defaults if none saved yet).</summary>
public sealed record GetNotificationPreferencesQuery : IQuery<NotificationPreferencesResponse>;

public sealed class GetNotificationPreferencesQueryHandler(IApplicationDbContext dbContext, ICurrentUser currentUser)
    : IQueryHandler<GetNotificationPreferencesQuery, NotificationPreferencesResponse>
{
    private static readonly Error NotAuthenticated = Error.Unauthorized("auth.unauthenticated", "Authentication is required.");

    public async Task<Result<NotificationPreferencesResponse>> Handle(GetNotificationPreferencesQuery query, CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not { } userId)
        {
            return NotAuthenticated;
        }

        var prefs = await dbContext.NotificationPreferences.AsNoTracking()
            .FirstOrDefaultAsync(p => p.UserId == userId, cancellationToken)
            ?? NotificationPreferences.CreateDefault(userId);

        return new NotificationPreferencesResponse(
            prefs.SecurityAlerts, prefs.VpnStatusAlerts, prefs.ProductAnnouncements,
            prefs.BillingNotifications, prefs.MarketingEmails, prefs.ServiceStatusAlerts);
    }
}

/// <summary>Updates (or creates) the current user's notification preferences.</summary>
public sealed record UpdateNotificationPreferencesCommand(
    bool SecurityAlerts,
    bool VpnStatusAlerts,
    bool ProductAnnouncements,
    bool BillingNotifications,
    bool MarketingEmails,
    bool ServiceStatusAlerts) : ICommand;

public sealed class UpdateNotificationPreferencesCommandHandler(
    IApplicationDbContext dbContext,
    ICurrentUser currentUser,
    IAuditWriter audit)
    : ICommandHandler<UpdateNotificationPreferencesCommand>
{
    private static readonly Error NotAuthenticated = Error.Unauthorized("auth.unauthenticated", "Authentication is required.");

    public async Task<Result> Handle(UpdateNotificationPreferencesCommand command, CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not { } userId)
        {
            return NotAuthenticated;
        }

        var prefs = await dbContext.NotificationPreferences.FirstOrDefaultAsync(p => p.UserId == userId, cancellationToken);
        if (prefs is null)
        {
            prefs = NotificationPreferences.CreateDefault(userId);
            dbContext.NotificationPreferences.Add(prefs);
        }

        prefs.Update(command.SecurityAlerts, command.VpnStatusAlerts, command.ProductAnnouncements,
            command.BillingNotifications, command.MarketingEmails, command.ServiceStatusAlerts);

        audit.Record("account.notifications_updated", AuditOutcome.Success, nameof(NotificationPreferences), userId.ToString());
        return Result.Success();
    }
}
