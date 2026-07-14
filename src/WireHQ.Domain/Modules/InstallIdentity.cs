using WireHQ.Domain.Common;

namespace WireHQ.Domain.Modules;

/// <summary>
/// This install's stable identity — a single row in the platform-global <c>modules</c> schema, minted the first
/// time the operator activates a module (docs/29-ce-marketplace-modules.md M-5). Its <see cref="Fingerprint"/>
/// (<c>fp</c>) is the value the CE sends to the hosted licensing service on activate/verify: a random token that
/// survives restarts and rebuilds and moves with a database restore, so an activation token bound to it keeps
/// working on the same install and is useless if copied elsewhere.
///
/// <para>The table is a <b>singleton</b> — exactly one identity per install — enforced by a fixed primary key
/// (<see cref="SingletonId"/>): the fingerprint lives in its own random column, NOT the key, so the key can be
/// constant (a concurrent second insert then collides on the PK instead of minting a duplicate identity) while
/// the fingerprint stays unique per install. CE-ONLY: overlay-added; absent from the SaaS model.</para>
/// </summary>
public sealed class InstallIdentity : Entity
{
    /// <summary>The fixed primary key of the one install-identity row — the singleton guard (see the type remarks).</summary>
    public static readonly Guid SingletonId = new("00000000-0000-0000-0000-00000c0de001");

    // EF Core
    private InstallIdentity()
    {
    }

    private InstallIdentity(string fingerprint, DateTimeOffset createdAtUtc)
        : base(SingletonId)
    {
        Fingerprint = fingerprint;
        CreatedAtUtc = createdAtUtc;
    }

    /// <summary>The random per-install fingerprint (<c>fp</c>) the licensing call-home sends. Stored (not derived
    /// from the key) so it is unique per install even though the row's key is a constant singleton value.</summary>
    public string Fingerprint { get; private set; } = null!;

    /// <summary>When this install identity was first minted.</summary>
    public DateTimeOffset CreatedAtUtc { get; private set; }

    public static InstallIdentity Create(DateTimeOffset now) => new(Guid.CreateVersion7().ToString("N"), now);
}
