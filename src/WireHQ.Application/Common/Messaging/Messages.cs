using MediatR;
using WireHQ.Shared.Results;

namespace WireHQ.Application.Common.Messaging;

// Thin CQRS markers over MediatR. Every use case returns a Result/Result<T> so failures are
// values, not exceptions, and the HTTP boundary maps them uniformly.

/// <summary>Marker shared by all commands so the UnitOfWork behavior can target writes only.</summary>
public interface IBaseCommand;

/// <summary>A write operation that returns no payload.</summary>
public interface ICommand : IRequest<Result>, IBaseCommand;

/// <summary>A write operation that returns a payload.</summary>
public interface ICommand<TResponse> : IRequest<Result<TResponse>>, IBaseCommand;

/// <summary>A read operation.</summary>
public interface IQuery<TResponse> : IRequest<Result<TResponse>>;

public interface ICommandHandler<in TCommand> : IRequestHandler<TCommand, Result>
    where TCommand : ICommand;

public interface ICommandHandler<in TCommand, TResponse> : IRequestHandler<TCommand, Result<TResponse>>
    where TCommand : ICommand<TResponse>;

public interface IQueryHandler<in TQuery, TResponse> : IRequestHandler<TQuery, Result<TResponse>>
    where TQuery : IQuery<TResponse>;

/// <summary>
/// Implemented by commands/queries that require authorization. The
/// <c>AuthorizationBehavior</c> enforces these permissions in the pipeline before the handler
/// runs — so a use case is guarded no matter how it is dispatched. (docs/04-security.md)
/// </summary>
public interface IAuthorizedRequest
{
    /// <summary>Permission keys the caller must hold (ALL of them), e.g. <c>identity.users.invite</c>.</summary>
    IReadOnlyCollection<string> RequiredPermissions { get; }
}

/// <summary>
/// Implemented by use cases that require the caller to be a platform operator (Super Admin) — enforced
/// by the <c>AuthorizationBehavior</c>, independent of (and above) any organization role. Platform
/// requests are not org-scoped. (docs/03-multi-tenancy.md)
/// </summary>
public interface IPlatformRequest;

/// <summary>
/// A platform <b>read</b> accessible to any platform operator — Super Admin <b>or</b> the lower
/// <c>Support</c> tier (<see cref="WireHQ.Domain.Identity.PlatformRole"/>). Like <see cref="IPlatformRequest"/>
/// it is RLS-bypassed by the <c>TenantScopeBehavior</c> so it can read across tenants, and the
/// <c>AuthorizationBehavior</c> requires platform-operator tier — but unlike a full <see cref="IPlatformRequest"/>
/// it does <b>not</b> grant Support the right to mutate. Used for non-impersonating cross-tenant diagnostics
/// (e.g. the platform audit search), each of which audits itself (audit-the-auditor). (docs/15 §10, ADR-032)
/// </summary>
public interface IPlatformReadRequest;

/// <summary>
/// Marks a use case that the caller may only run once their email address is verified — a soft gate so
/// unverified users can still sign in and onboard, but sensitive actions (e.g. creating VPN config,
/// inviting members) are blocked until they confirm their email. Enforced by the
/// <c>VerifiedEmailBehavior</c> in the pipeline. (docs/04-security.md)
/// </summary>
public interface IRequiresVerifiedEmail;

/// <summary>
/// Marks a use case that legitimately reads/writes tenant-owned data across organizations or before an
/// active org exists — e.g. logging in (resolve the user's memberships), registering (provision a new
/// org), refreshing/MFA-verifying (re-mint claims), and GetMe (list every org the user belongs to). The
/// <c>TenantScopeBehavior</c> sets the RLS bypass for these before the handler runs, so its cross-tenant
/// reads aren't blocked by the database tenant policy. Everything else stays org-scoped (fail-closed).
/// (docs/03-multi-tenancy.md, ADR-027)
/// </summary>
public interface ITenantUnscopedRequest;

/// <summary>
/// Marks a use case that requires the caller's organization to be entitled to a feature by its plan
/// (edition). The <c>EntitlementBehavior</c> resolves the active org's plan and blocks the request with
/// <c>plan.upgrade_required</c> if the feature isn't included — so a capability is gated no matter how it
/// is dispatched. Feature keys are defined in <c>Entitlements.Features</c>. (docs/commercial.md)
/// </summary>
public interface IRequiresFeature
{
    /// <summary>The feature key the caller's plan must include, e.g. <c>fleet.dashboard</c>.</summary>
    string RequiredFeature { get; }
}

/// <summary>
/// Marks a command for declarative auditing. The <c>AuditBehavior</c> records exactly one audit entry when
/// the command succeeds — deriving the target and a structured before/after diff from the EF
/// <c>ChangeTracker</c> — so a handler declares its audit intent here instead of hand-writing an
/// <c>IAuditWriter.Record(...)</c> call (and its diff) in the body. Coverage becomes enforceable rather than
/// a per-handler discipline. (docs/15 §5, ADR-031)
/// <para>
/// IMPORTANT — do not double-audit: a handler that already calls <c>IAuditWriter.Record(...)</c> must NOT
/// also implement this, or the action is written twice. Mark new handlers, or migrate a manual call across
/// to this marker (removing the manual call).
/// </para>
/// </summary>
public interface IAuditableRequest
{
    /// <summary>The audit action key recorded for this use case, e.g. <c>wg.network.created</c>.</summary>
    string AuditAction { get; }
}

/// <summary>
/// Implemented by the (anonymous) public auth use cases that the site-wide CAPTCHA protects. When
/// Turnstile is enabled in platform settings, the <c>CaptchaBehavior</c> verifies
/// <see cref="TurnstileToken"/> before the handler runs. (docs/04-security.md)
/// </summary>
public interface ICaptchaProtected
{
    /// <summary>The Turnstile response token from the browser widget (null when the page didn't render it).</summary>
    string? TurnstileToken { get; }
}
