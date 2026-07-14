using WireHQ.Domain.Common;

namespace WireHQ.Domain.Identity;

/// <summary>
/// A single-use password-reset token, stored only as a hash with a short expiry. The raw token is
/// delivered out-of-band (email) and never persisted. Consuming it is one-way. (docs/04-security.md)
/// </summary>
public sealed class PasswordResetToken : Entity
{
    // EF Core
    private PasswordResetToken()
    {
    }

    private PasswordResetToken(Guid id, Guid userId, string tokenHash, DateTimeOffset expiresAtUtc)
        : base(id)
    {
        UserId = userId;
        TokenHash = tokenHash;
        CreatedAtUtc = DateTimeOffset.UtcNow;
        ExpiresAtUtc = expiresAtUtc;
    }

    public Guid UserId { get; private set; }

    public string TokenHash { get; private set; } = null!;

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset ExpiresAtUtc { get; private set; }

    public DateTimeOffset? UsedAtUtc { get; private set; }

    public bool IsActive(DateTimeOffset nowUtc) => UsedAtUtc is null && ExpiresAtUtc > nowUtc;

    public static PasswordResetToken Issue(Guid userId, string tokenHash, DateTimeOffset expiresAtUtc) =>
        new(Guid.CreateVersion7(), userId, tokenHash, expiresAtUtc);

    public void Consume(DateTimeOffset nowUtc) => UsedAtUtc ??= nowUtc;
}
