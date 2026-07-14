using WireHQ.Domain.Common;

namespace WireHQ.Domain.Sessions;

/// <summary>
/// A rotating refresh token, stored only as a hash. Tokens form a <em>family</em> chained by
/// rotation; presenting an already-rotated token is treated as theft and the whole family is
/// revoked (reuse detection). This neutralizes stolen-refresh-token replay. (docs/04-security.md)
/// </summary>
public sealed class RefreshToken : Entity
{
    // EF Core
    private RefreshToken()
    {
    }

    private RefreshToken(Guid id, Guid sessionId, Guid userId, string tokenHash, Guid familyId, DateTimeOffset expiresAtUtc)
        : base(id)
    {
        SessionId = sessionId;
        UserId = userId;
        TokenHash = tokenHash;
        FamilyId = familyId;
        CreatedAtUtc = DateTimeOffset.UtcNow;
        ExpiresAtUtc = expiresAtUtc;
    }

    public Guid SessionId { get; private set; }

    public Guid UserId { get; private set; }

    /// <summary>SHA-256 of the opaque token. The raw token is never stored.</summary>
    public string TokenHash { get; private set; } = null!;

    /// <summary>Identifies the rotation chain. Revoking the family kills every token in it.</summary>
    public Guid FamilyId { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset ExpiresAtUtc { get; private set; }

    public DateTimeOffset? RotatedAtUtc { get; private set; }

    public DateTimeOffset? RevokedAtUtc { get; private set; }

    public Guid? ReplacedByTokenId { get; private set; }

    public bool IsActive(DateTimeOffset nowUtc) =>
        RevokedAtUtc is null && RotatedAtUtc is null && ExpiresAtUtc > nowUtc;

    public static RefreshToken Issue(Guid sessionId, Guid userId, string tokenHash, Guid familyId, DateTimeOffset expiresAtUtc) =>
        new(Guid.CreateVersion7(), sessionId, userId, tokenHash, familyId, expiresAtUtc);

    /// <summary>Rotate this token, producing its successor in the same family.</summary>
    public RefreshToken Rotate(string newTokenHash, DateTimeOffset nowUtc, DateTimeOffset newExpiresAtUtc)
    {
        var next = new RefreshToken(Guid.CreateVersion7(), SessionId, UserId, newTokenHash, FamilyId, newExpiresAtUtc);
        RotatedAtUtc = nowUtc;
        ReplacedByTokenId = next.Id;
        return next;
    }

    public void Revoke(DateTimeOffset nowUtc) => RevokedAtUtc ??= nowUtc;
}
