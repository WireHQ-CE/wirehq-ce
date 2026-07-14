using System.Security.Cryptography;
using System.Text;
using WireHQ.Domain.Common;
using WireHQ.Shared.Results;

namespace WireHQ.Modules.Orchestration.Domain;

/// <summary>
/// A single-use, short-TTL bootstrap token an operator mints to enrol one agent. Only the SHA-256
/// <b>hash</b> of the token is stored — the clear token is shown once and travels in the install command
/// (<c>wirehq-agent enroll --token …</c>). The gateway's enrol endpoint looks it up by hash, checks it's
/// unused + unexpired, then burns it. Tenant-owned + audited. (docs/12 §5/§8, ADR-019/028)
/// </summary>
public sealed class AgentEnrollmentToken : AggregateRoot, ITenantOwned, IAuditable
{
    public const int DefaultTtlHours = 24;

    private AgentEnrollmentToken()
    {
    }

    private AgentEnrollmentToken(Guid id, Guid organizationId, string tokenHash, DateTimeOffset expiresAtUtc)
        : base(id)
    {
        OrganizationId = organizationId;
        TokenHash = tokenHash;
        ExpiresAtUtc = expiresAtUtc;
    }

    public Guid OrganizationId { get; private set; }

    /// <summary>SHA-256 hash (hex) of the clear token. The clear value is never stored.</summary>
    public string TokenHash { get; private set; } = null!;

    public DateTimeOffset ExpiresAtUtc { get; private set; }
    public DateTimeOffset? UsedAtUtc { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }
    public Guid? CreatedBy { get; private set; }
    public DateTimeOffset? UpdatedAtUtc { get; private set; }
    public Guid? UpdatedBy { get; private set; }

    /// <summary>
    /// The single hashing rule for enrolment tokens (SHA-256 hex of the clear token). Both the operator
    /// mint path and the gateway enrol path hash through here so a clear token matches its stored hash. The
    /// token is 256-bit random, so a plain hash (no salt) is sufficient — there is no low-entropy guess space.
    /// </summary>
    public static string HashToken(string clearToken) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(clearToken)));

    public static Result<AgentEnrollmentToken> Issue(Guid id, Guid organizationId, string tokenHash, DateTimeOffset expiresAtUtc) =>
        string.IsNullOrWhiteSpace(tokenHash)
            ? OrchestrationErrors.Agent.InvalidToken
            : new AgentEnrollmentToken(id, organizationId, tokenHash, expiresAtUtc);

    /// <summary>True when the token can still be redeemed (not used, not expired).</summary>
    public bool IsRedeemable(DateTimeOffset now) => UsedAtUtc is null && ExpiresAtUtc > now;

    /// <summary>Burns the token so it can never be redeemed again.</summary>
    public void Redeem(DateTimeOffset at) => UsedAtUtc = at;
}
