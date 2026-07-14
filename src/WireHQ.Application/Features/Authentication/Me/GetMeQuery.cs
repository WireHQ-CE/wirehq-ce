using Microsoft.EntityFrameworkCore;
using WireHQ.Application.Abstractions;
using WireHQ.Application.Abstractions.Persistence;
using WireHQ.Application.Common.Messaging;
using WireHQ.Application.Entitlements;
using WireHQ.Domain.Memberships;
using WireHQ.Domain.Onboarding;
using WireHQ.Shared.Results;

namespace WireHQ.Application.Features.Authentication.Me;

public sealed record GetMeQuery : IQuery<MeResponse>, ITenantUnscopedRequest;

public sealed record MeResponse(
    Guid UserId,
    string Email,
    string Name,
    string? FirstName,
    string? LastName,
    string? Username,
    string? JobTitle,
    string? Phone,
    string? Timezone,
    string? Language,
    string? AvatarUrl,
    bool MfaEnabled,
    bool EmailVerified,
    Guid? ActiveOrganizationId,
    IReadOnlyCollection<MembershipSummary> Organizations,
    IReadOnlyCollection<string> Permissions,
    // Platform-operator role name (e.g. SuperAdmin), or null. Drives the platform UI.
    string? PlatformRole,
    // True when the active org still has the Welcome Wizard pending (drives the first-login redirect).
    bool OnboardingPending,
    // When impersonating, the operator acting as this account (drives the "Viewing as" banner).
    ImpersonatorSummary? ImpersonatedBy,
    // The active org's plan + included features/limits — drives feature-gating in the UI.
    EntitlementSnapshot Entitlements,
    // The active org's billing/subscription status — drives the trial countdown + past-due banner.
    BillingSummary Billing);

public sealed record MembershipSummary(Guid OrganizationId, string Slug, string Name, string Status);

/// <summary>The active org's subscription status for the UI (trial/past-due affordances). (docs/commercial.md §6.4)</summary>
public sealed record BillingSummary(
    string Status,
    DateTimeOffset? TrialEndUtc,
    DateTimeOffset? CurrentPeriodEndUtc,
    DateTimeOffset? GraceEndsUtc);

/// <summary>The operator acting as this account, plus when the time-boxed impersonation session expires
/// (ADR-032) so the UI can show + count down the limit. <see cref="ExpiresAtUtc"/> is null only if the
/// session can't be resolved.</summary>
public sealed record ImpersonatorSummary(Guid UserId, string Name, string Email, DateTimeOffset? ExpiresAtUtc);

/// <summary>Returns the current principal, their organization memberships, and effective permissions.</summary>
public sealed class GetMeQueryHandler(IApplicationDbContext dbContext, ICurrentUser currentUser, ITenantContext tenant, IEntitlementService entitlements, IBillingSummaryReader billingReader)
    : IQueryHandler<GetMeQuery, MeResponse>
{
    private static readonly Error NotAuthenticated = Error.Unauthorized("auth.unauthenticated", "Authentication is required.");

    public async Task<Result<MeResponse>> Handle(GetMeQuery query, CancellationToken cancellationToken)
    {
        if (!currentUser.IsAuthenticated || currentUser.UserId is not { } userId)
        {
            return NotAuthenticated;
        }

        var user = await dbContext.Users
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(u => u.Id == userId && !u.IsDeleted, cancellationToken);

        if (user is null)
        {
            return NotAuthenticated;
        }

        // The user's orgs — joined to org name/slug. Not tenant-filtered: a user spans tenants.
        var memberships = await dbContext.Memberships
            .IgnoreQueryFilters()
            .Where(m => m.UserId == userId && !m.IsDeleted)
            .Join(
                dbContext.Organizations.IgnoreQueryFilters(),
                m => m.OrganizationId,
                o => o.Id,
                (m, o) => new MembershipSummary(o.Id, o.Slug.Value, o.Name, m.Status.ToString()))
            .ToListAsync(cancellationToken);

        var platformRole = user.PlatformRole == WireHQ.Domain.Identity.PlatformRole.None
            ? null
            : user.PlatformRole.ToString();

        ImpersonatorSummary? impersonatedBy = null;
        if (currentUser.ImpersonatorUserId is { } impersonatorId)
        {
            impersonatedBy = await dbContext.Users
                .IgnoreQueryFilters()
                .Where(u => u.Id == impersonatorId)
                .Select(u => new ImpersonatorSummary(u.Id, u.Name, u.Email.Value, null))
                .FirstOrDefaultAsync(cancellationToken);

            // Stamp the time-box expiry (ADR-032) from the impersonation session's start so the banner can
            // show + count it down. Derived = CreatedAtUtc + the hard cap; matches the refresh-side enforcement.
            if (impersonatedBy is not null && currentUser.SessionId is { } sessionId)
            {
                var startedAt = await dbContext.UserSessions
                    .IgnoreQueryFilters()
                    .Where(s => s.Id == sessionId)
                    .Select(s => (DateTimeOffset?)s.CreatedAtUtc)
                    .FirstOrDefaultAsync(cancellationToken);
                if (startedAt is { } started)
                {
                    impersonatedBy = impersonatedBy with
                    {
                        ExpiresAtUtc = started.AddMinutes(AuthSessionService.ImpersonationSessionLifetimeMinutes),
                    };
                }
            }
        }

        // The Welcome Wizard is pending when the active org has a Pending onboarding profile (the global
        // query filter scopes this to the active org; Super Admins with no org never see it).
        var onboardingPending = tenant.OrganizationId is not null
            && await dbContext.OnboardingProfiles.AnyAsync(p => p.Status == OnboardingStatus.Pending, cancellationToken);

        // Cache-busted by the avatar's last-updated tick so a new upload is fetched immediately.
        var avatarUrl = user.AvatarUpdatedAtUtc is { } at
            ? $"/api/v1/avatars/{user.Id}?v={at.UtcTicks}"
            : null;

        var entitlementSnapshot = await entitlements.SnapshotAsync(cancellationToken);

        // The active org's subscription, behind the IBillingSummaryReader port (docs/17 §5): the SaaS
        // reader queries the Subscription row; the Community Edition's null default reports "None".
        BillingSummary billing = new("None", null, null, null);
        if (tenant.OrganizationId is not null)
        {
            var snapshot = await billingReader.ReadAsync(cancellationToken);
            if (snapshot is not null)
            {
                billing = new BillingSummary(
                    snapshot.Status, snapshot.TrialEndUtc, snapshot.CurrentPeriodEndUtc, snapshot.GraceEndsUtc);
            }
        }

        return new MeResponse(
            user.Id,
            user.Email.Value,
            user.Name,
            user.FirstName,
            user.LastName,
            user.Username,
            user.JobTitle,
            user.Phone,
            user.Timezone,
            user.Language,
            avatarUrl,
            user.MfaEnabled,
            user.EmailVerified,
            tenant.OrganizationId,
            memberships,
            currentUser.Permissions,
            platformRole,
            onboardingPending,
            impersonatedBy,
            entitlementSnapshot,
            billing);
    }
}
