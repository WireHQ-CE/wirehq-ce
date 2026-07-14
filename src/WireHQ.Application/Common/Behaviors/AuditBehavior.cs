using MediatR;
using WireHQ.Application.Abstractions;
using WireHQ.Application.Common.Messaging;
using WireHQ.Domain.Auditing;
using WireHQ.Shared.Results;

namespace WireHQ.Application.Common.Behaviors;

/// <summary>
/// Declarative audit coverage. A command marked <see cref="IAuditableRequest"/> gets exactly one audit
/// entry written when it succeeds — the action key comes from the marker, while the target and a structured
/// before/after diff are derived automatically from the pending EF changes (<see cref="IAuditChangeCapture"/>).
/// Handlers no longer hand-write <c>IAuditWriter.Record(...)</c>, so audit coverage is enforced by the
/// pipeline instead of relying on per-handler discipline. (docs/15 §5, ADR-031)
/// <para>
/// Ordering is critical: this behavior is registered AFTER <see cref="UnitOfWorkBehavior{TRequest,TResponse}"/>
/// so it runs INSIDE it — the handler mutates, then this captures the diff and appends the audit entry to the
/// same unit of work, then the outer UnitOfWork commits the business change and its audit row in one
/// transaction (atomic, append-only). The diff is captured before <c>SaveChanges</c>, while the
/// ChangeTracker still holds original values.
/// </para>
/// </summary>
public sealed class AuditBehavior<TRequest, TResponse>(IAuditWriter audit, IAuditChangeCapture capture)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
    where TResponse : Result
{
    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        var response = await next();

        // Only audit a declared, successful command. On failure the outer UnitOfWork discards the unit of
        // work anyway, so there is nothing durable to describe.
        if (request is IAuditableRequest auditable && response.IsSuccess)
        {
            var changeSet = capture.Capture();
            audit.Record(
                auditable.AuditAction,
                AuditOutcome.Success,
                targetType: changeSet.TargetType,
                targetId: changeSet.TargetId,
                changes: changeSet.Changes);
        }

        return response;
    }
}
