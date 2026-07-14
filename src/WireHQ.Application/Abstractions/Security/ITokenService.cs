namespace WireHQ.Application.Abstractions.Security;

/// <summary>Issues and describes auth tokens. Implemented in WireHQ.Identity.</summary>
public interface ITokenService
{
    AccessToken IssueAccessToken(TokenSubject subject);

    /// <summary>Generate a high-entropy opaque refresh token plus the hash to persist.</summary>
    RawRefreshToken IssueRefreshToken(DateTimeOffset expiresAtUtc);

    /// <summary>Deterministic hash used to look up / compare a presented refresh token.</summary>
    string HashRefreshToken(string rawToken);
}

/// <summary>The claims that go into an access token.</summary>
public sealed record TokenSubject(
    Guid UserId,
    string Email,
    Guid SessionId,
    Guid? OrganizationId,
    Guid? MembershipId,
    IReadOnlyCollection<string> Permissions,
    bool MfaSatisfied,
    string SecurityStamp,
    // Platform-operator role name (null for normal users). Kept as a string so the token
    // layer stays decoupled from the domain enum.
    string? PlatformRole = null,
    // Set only on an impersonation token: the platform operator acting as this account.
    Guid? ImpersonatorUserId = null);

public sealed record AccessToken(string Value, DateTimeOffset ExpiresAtUtc, int ExpiresInSeconds);

public sealed record RawRefreshToken(string Value, string Hash, DateTimeOffset ExpiresAtUtc);
