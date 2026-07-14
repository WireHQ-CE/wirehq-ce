using WireHQ.Domain.Identity;

namespace WireHQ.Application.Abstractions.Authentication;

/// <summary>
/// A pluggable external credential authority consulted by the login handler before the local password check
/// (docs/24-ldap-authentication.md §4/§7). For an already-resolved user + the submitted password it answers
/// whether an external authority (today: a synced LDAP/AD directory) governs that user's password, and — if so —
/// whether it verifies. Kept-core and <b>module-neutral</b> (no directory/LDAP types) so the login handler stays
/// ignorant of the SaaS directory engine. It is injected as an <c>IEnumerable</c> and is simply <b>empty in the
/// Community Edition</b>, where login is byte-for-byte the local-password path.
/// </summary>
public interface IExternalPasswordAuthenticator
{
    /// <summary>Verify the password against the external authority that governs this user, if any.</summary>
    Task<ExternalPasswordResult> AuthenticateAsync(User user, string password, CancellationToken cancellationToken);
}

/// <summary>The verdict of an external credential authority (docs/24 §4).</summary>
public enum ExternalPasswordStatus
{
    /// <summary>This authority does not govern the user — fall through to the next authority / the local password.</summary>
    NotApplicable = 0,

    /// <summary>The authority governs the user and the password verified — mint a session on
    /// <see cref="ExternalPasswordResult.MembershipId"/>.</summary>
    Succeeded = 1,

    /// <summary>The authority governs the user and the password did not verify — deny (and count a failed sign-in).</summary>
    Failed = 2,
}

/// <summary>The outcome of an <see cref="IExternalPasswordAuthenticator"/> check (docs/24 §4).</summary>
public sealed record ExternalPasswordResult(ExternalPasswordStatus Status, Guid? MembershipId = null)
{
    public static readonly ExternalPasswordResult NotApplicable = new(ExternalPasswordStatus.NotApplicable);

    public static readonly ExternalPasswordResult Failed = new(ExternalPasswordStatus.Failed);

    /// <summary>The external authority verified the password; mint the session on this directory-linked membership.</summary>
    public static ExternalPasswordResult Success(Guid membershipId) => new(ExternalPasswordStatus.Succeeded, membershipId);
}
