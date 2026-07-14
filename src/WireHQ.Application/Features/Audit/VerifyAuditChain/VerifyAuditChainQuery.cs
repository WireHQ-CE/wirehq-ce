using WireHQ.Application.Abstractions;
using WireHQ.Application.Authorization;
using WireHQ.Application.Common.Messaging;
using WireHQ.Shared.Results;

namespace WireHQ.Application.Features.Audit.VerifyAuditChain;

/// <summary>
/// Verifies that the calling tenant's audit hash chain is intact (ADR-031, docs/15 §5) — the
/// tamper-evidence check. Gated by <see cref="Permissions.Audit.Read"/>, scoped to the active org.
/// </summary>
public sealed record VerifyAuditChainQuery : IQuery<AuditChainVerificationResult>, IAuthorizedRequest
{
    public IReadOnlyCollection<string> RequiredPermissions => [Permissions.Audit.Read];
}

public sealed class VerifyAuditChainQueryHandler(ITenantContext tenant, IAuditChainVerifier verifier)
    : IQueryHandler<VerifyAuditChainQuery, AuditChainVerificationResult>
{
    public async Task<Result<AuditChainVerificationResult>> Handle(
        VerifyAuditChainQuery query, CancellationToken cancellationToken)
    {
        var result = await verifier.VerifyAsync(tenant.OrganizationId, cancellationToken);
        return result;
    }
}
