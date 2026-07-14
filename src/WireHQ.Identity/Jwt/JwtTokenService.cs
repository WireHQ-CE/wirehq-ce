using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using WireHQ.Application.Abstractions.Security;

namespace WireHQ.Identity.Jwt;

/// <summary>
/// Issues signed access tokens and opaque refresh tokens. Access tokens are short-lived and
/// stateless; refresh tokens are random, returned raw to the caller, and persisted only as a
/// SHA-256 hash. (docs/04-security.md)
/// </summary>
public sealed class JwtTokenService : ITokenService
{
    private readonly JwtOptions _options;
    private readonly SigningCredentials _signingCredentials;
    private readonly JwtSecurityTokenHandler _handler = new();

    public JwtTokenService(IOptions<JwtOptions> options)
    {
        _options = options.Value;
        var keyBytes = Encoding.UTF8.GetBytes(_options.SigningKey);
        _signingCredentials = new SigningCredentials(new SymmetricSecurityKey(keyBytes), SecurityAlgorithms.HmacSha256);
    }

    public AccessToken IssueAccessToken(TokenSubject subject)
    {
        var now = DateTimeOffset.UtcNow;
        var expiresAt = now.AddMinutes(_options.AccessTokenMinutes);

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, subject.UserId.ToString()),
            new(JwtRegisteredClaimNames.Email, subject.Email),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
            new(WireHqClaims.SessionId, subject.SessionId.ToString()),
            new(WireHqClaims.SecurityStamp, subject.SecurityStamp),
            new(WireHqClaims.MfaSatisfied, subject.MfaSatisfied ? "1" : "0"),
        };

        if (subject.OrganizationId is { } orgId)
        {
            claims.Add(new Claim(WireHqClaims.OrganizationId, orgId.ToString()));
        }

        if (subject.MembershipId is { } membershipId)
        {
            claims.Add(new Claim(WireHqClaims.MembershipId, membershipId.ToString()));
        }

        if (subject.PlatformRole is { } platformRole)
        {
            claims.Add(new Claim(WireHqClaims.PlatformRole, platformRole));
        }

        if (subject.ImpersonatorUserId is { } impersonatorId)
        {
            claims.Add(new Claim(WireHqClaims.Impersonator, impersonatorId.ToString()));
        }

        claims.AddRange(subject.Permissions.Select(p => new Claim(WireHqClaims.Permission, p)));

        var token = new JwtSecurityToken(
            issuer: _options.Issuer,
            audience: _options.Audience,
            claims: claims,
            notBefore: now.UtcDateTime,
            expires: expiresAt.UtcDateTime,
            signingCredentials: _signingCredentials);

        return new AccessToken(_handler.WriteToken(token), expiresAt, _options.AccessTokenMinutes * 60);
    }

    public RawRefreshToken IssueRefreshToken(DateTimeOffset expiresAtUtc)
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        var raw = Base64UrlEncoder.Encode(bytes);
        return new RawRefreshToken(raw, HashRefreshToken(raw), expiresAtUtc);
    }

    public string HashRefreshToken(string rawToken)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(rawToken));
        return Convert.ToBase64String(hash);
    }
}
