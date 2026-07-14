using Microsoft.EntityFrameworkCore;
using WireHQ.Domain.Auditing;
using WireHQ.Domain.Authorization;
using WireHQ.Domain.Identity;
using WireHQ.Domain.Memberships;
using WireHQ.Domain.Onboarding;
using WireHQ.Domain.Organizations;
using WireHQ.Domain.Platform;
using WireHQ.Domain.Sessions;
using WireHQ.Domain.Teams;

namespace WireHQ.Application.Abstractions.Persistence;

/// <summary>
/// The persistence port. Application depends on this abstraction (EF Core's <see cref="DbSet{T}"/>
/// and <c>SaveChangesAsync</c>) — never on the concrete <c>ApplicationDbContext</c>, Npgsql, or a
/// provider. Those live in Infrastructure. Tenant isolation and auditing are applied
/// transparently by the real context (global query filters + interceptors), so handlers write
/// plain LINQ and isolation still holds. (docs/03-multi-tenancy.md)
/// Partial: the SaaS-only sets (billing/CMS) live in <c>IApplicationDbContext.Saas.cs</c> so the
/// Community Edition strip removes them file-wise (docs/17 §5).
/// </summary>
public partial interface IApplicationDbContext
{
    DbSet<Organization> Organizations { get; }
    DbSet<OrganizationSettings> OrganizationSettings { get; }
    DbSet<PlatformSettings> PlatformSettings { get; }
    DbSet<BrandAsset> BrandAssets { get; }
    DbSet<OnboardingProfile> OnboardingProfiles { get; }
    DbSet<Team> Teams { get; }
    DbSet<User> Users { get; }
    DbSet<UserAvatar> UserAvatars { get; }
    DbSet<NotificationPreferences> NotificationPreferences { get; }
    DbSet<Membership> Memberships { get; }
    DbSet<Role> Roles { get; }
    DbSet<Permission> Permissions { get; }
    DbSet<UserSession> UserSessions { get; }
    DbSet<RefreshToken> RefreshTokens { get; }
    DbSet<MfaCredential> MfaCredentials { get; }
    DbSet<RecoveryCode> RecoveryCodes { get; }
    DbSet<PasswordResetToken> PasswordResetTokens { get; }
    DbSet<EmailVerificationToken> EmailVerificationTokens { get; }
    DbSet<AuditLog> AuditLogs { get; }
    DbSet<AuditChainAnchor> AuditChainAnchors { get; }

    /// <summary>
    /// Generic entity-set accessor. Feature modules use this to query their own entities (which the
    /// shared context does not name explicitly) while still flowing through the platform's tenancy,
    /// audit, and unit-of-work machinery. Satisfied by <see cref="DbContext.Set{TEntity}()"/>.
    /// </summary>
    DbSet<TEntity> Set<TEntity>() where TEntity : class;

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
