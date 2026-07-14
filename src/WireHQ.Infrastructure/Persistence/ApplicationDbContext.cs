using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using WireHQ.Application.Abstractions;
using WireHQ.Application.Abstractions.Persistence;
using WireHQ.Domain.Auditing;
using WireHQ.Domain.Authorization;
using WireHQ.Domain.Common;
using WireHQ.Domain.Identity;
using WireHQ.Domain.Memberships;
using WireHQ.Domain.Onboarding;
using WireHQ.Domain.Organizations;
using WireHQ.Domain.Platform;
using WireHQ.Domain.Sessions;
using WireHQ.Domain.Teams;

namespace WireHQ.Infrastructure.Persistence;

/// <summary>
/// The concrete persistence implementation. Applies tenant + soft-delete global query filters
/// (Layer 1 of isolation — see docs/03-multi-tenancy.md) and snake_case naming, and routes
/// auditing/tenant-stamping/domain-event dispatch through interceptors. Application depends only
/// on <see cref="IApplicationDbContext"/>, never on this type.
/// Partial: the SaaS-only sets (billing/CMS) live in <c>ApplicationDbContext.Saas.cs</c> so the
/// Community Edition strip removes them file-wise (docs/17 §5).
/// </summary>
public sealed partial class ApplicationDbContext(
    DbContextOptions<ApplicationDbContext> options,
    ITenantContext tenant,
    IEnumerable<IModelConfigurationContributor> modelContributors)
    : DbContext(options), IApplicationDbContext
{
    /// <summary>Read by the tenant query filters at query time (per context instance).</summary>
    public Guid? CurrentOrganizationId => tenant.OrganizationId;

    public DbSet<Organization> Organizations => Set<Organization>();
    public DbSet<OrganizationSettings> OrganizationSettings => Set<OrganizationSettings>();
    public DbSet<PlatformSettings> PlatformSettings => Set<PlatformSettings>();
    public DbSet<BrandAsset> BrandAssets => Set<BrandAsset>();
    public DbSet<OnboardingProfile> OnboardingProfiles => Set<OnboardingProfile>();
    public DbSet<Team> Teams => Set<Team>();
    public DbSet<User> Users => Set<User>();
    public DbSet<UserAvatar> UserAvatars => Set<UserAvatar>();
    public DbSet<NotificationPreferences> NotificationPreferences => Set<NotificationPreferences>();
    public DbSet<Membership> Memberships => Set<Membership>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<Permission> Permissions => Set<Permission>();
    public DbSet<UserSession> UserSessions => Set<UserSession>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<MfaCredential> MfaCredentials => Set<MfaCredential>();
    public DbSet<RecoveryCode> RecoveryCodes => Set<RecoveryCode>();
    public DbSet<PasswordResetToken> PasswordResetTokens => Set<PasswordResetToken>();
    public DbSet<EmailVerificationToken> EmailVerificationTokens => Set<EmailVerificationToken>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<AuditChainAnchor> AuditChainAnchors => Set<AuditChainAnchor>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("core");
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(ApplicationDbContext).Assembly);

        // Feature modules contribute their own entities/schema (e.g. "wg") via contributors,
        // keeping their persistence in their assembly while sharing this context. (docs/11 §1.3)
        foreach (var contributor in modelContributors)
        {
            contributor.Configure(modelBuilder);
        }

        // snake_case column/table naming is applied globally via UseSnakeCaseNamingConvention()
        // in the DbContext options (see Infrastructure DI) — no per-entity naming needed here.
        ApplyGlobalQueryFilters(modelBuilder);

        base.OnModelCreating(modelBuilder);
    }

    /// <summary>
    /// Layer 1 isolation + soft delete, applied generically by marker interface so it covers core
    /// AND module entities (which this context can't name) with one proven mechanism. Tenant-owned
    /// entities (<see cref="ITenantOwned"/>) are scoped to the active org; soft-deletable entities
    /// (<see cref="ISoftDeletable"/>) hide deleted rows. The org reference is the context's
    /// <see cref="CurrentOrganizationId"/> — EF re-evaluates it per query against the current
    /// context, the documented multi-tenant pattern. Anonymous/cross-tenant flows opt out with
    /// <c>IgnoreQueryFilters()</c>. (docs/03-multi-tenancy.md, docs/11-wireguard-module.md §1.2)
    /// </summary>
    private void ApplyGlobalQueryFilters(ModelBuilder modelBuilder)
    {
        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            if (entityType.IsOwned())
            {
                continue;
            }

            var clrType = entityType.ClrType;
            var isTenantOwned = typeof(ITenantOwned).IsAssignableFrom(clrType);
            var isSoftDeletable = typeof(ISoftDeletable).IsAssignableFrom(clrType);
            if (!isTenantOwned && !isSoftDeletable)
            {
                continue;
            }

            var parameter = Expression.Parameter(clrType, "e");
            Expression? body = null;

            if (isTenantOwned)
            {
                // e.OrganizationId == this.CurrentOrganizationId  (compare Guid to Guid?)
                var orgProperty = Expression.Property(parameter, nameof(ITenantOwned.OrganizationId));
                var currentOrg = Expression.Property(Expression.Constant(this), nameof(CurrentOrganizationId));
                body = Expression.Equal(Expression.Convert(orgProperty, typeof(Guid?)), currentOrg);
            }

            if (isSoftDeletable)
            {
                var notDeleted = Expression.Not(Expression.Property(parameter, nameof(ISoftDeletable.IsDeleted)));
                body = body is null ? notDeleted : Expression.AndAlso(body, notDeleted);
            }

            modelBuilder.Entity(clrType).HasQueryFilter(Expression.Lambda(body!, parameter));
        }
    }
}
