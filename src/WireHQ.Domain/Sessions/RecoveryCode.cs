using WireHQ.Domain.Common;

namespace WireHQ.Domain.Sessions;

/// <summary>
/// A single-use MFA recovery code. Stored hashed (same hasher as passwords) and shown to the
/// user exactly once at generation. Consuming a code is audited. (docs/04-security.md)
/// </summary>
public sealed class RecoveryCode : Entity
{
    // EF Core
    private RecoveryCode()
    {
    }

    private RecoveryCode(Guid id, Guid userId, string codeHash)
        : base(id)
    {
        UserId = userId;
        CodeHash = codeHash;
        CreatedAtUtc = DateTimeOffset.UtcNow;
    }

    public Guid UserId { get; private set; }

    public string CodeHash { get; private set; } = null!;

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset? UsedAtUtc { get; private set; }

    public bool IsUsed => UsedAtUtc is not null;

    public static RecoveryCode Create(Guid userId, string codeHash) =>
        new(Guid.CreateVersion7(), userId, codeHash);

    public void Consume(DateTimeOffset nowUtc) => UsedAtUtc ??= nowUtc;
}
