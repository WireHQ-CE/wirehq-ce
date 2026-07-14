using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using WireHQ.Application.Abstractions;
using WireHQ.Identity.Jwt;

namespace WireHQ.Api.Security;

/// <summary>Projects the validated JWT into the Application's <see cref="ICurrentUser"/> port.</summary>
public sealed class CurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    private ClaimsPrincipal? Principal => accessor.HttpContext?.User;

    public bool IsAuthenticated => Principal?.Identity?.IsAuthenticated ?? false;

    public Guid? UserId => ParseGuid(JwtRegisteredClaimNames.Sub);

    public Guid? MembershipId => ParseGuid(WireHqClaims.MembershipId);

    public string? Email => Principal?.FindFirstValue(JwtRegisteredClaimNames.Email);

    public Guid? SessionId => ParseGuid(WireHqClaims.SessionId);

    public bool MfaSatisfied => Principal?.FindFirstValue(WireHqClaims.MfaSatisfied) == "1";

    public IReadOnlyCollection<string> Permissions =>
        Principal?.FindAll(WireHqClaims.Permission).Select(c => c.Value).ToArray() ?? [];

    public bool HasPermission(string permission) =>
        Principal?.HasClaim(WireHqClaims.Permission, permission) ?? false;

    public string? PlatformRole => Principal?.FindFirstValue(WireHqClaims.PlatformRole);

    public bool IsPlatformAdmin => PlatformRole == nameof(WireHQ.Domain.Identity.PlatformRole.SuperAdmin);

    public bool IsPlatformOperator =>
        IsPlatformAdmin || PlatformRole == nameof(WireHQ.Domain.Identity.PlatformRole.Support);

    public Guid? ImpersonatorUserId => ParseGuid(WireHqClaims.Impersonator);

    public Guid? ApiKeyId => ParseGuid(WireHqClaims.ApiKeyId);

    private Guid? ParseGuid(string claimType) =>
        Guid.TryParse(Principal?.FindFirstValue(claimType), out var value) ? value : null;
}
