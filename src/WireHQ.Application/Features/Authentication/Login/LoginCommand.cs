using WireHQ.Application.Common.Messaging;

namespace WireHQ.Application.Features.Authentication.Login;

public sealed record LoginCommand(string Email, string Password, string? TurnstileToken = null)
    : ICommand<LoginResponse>, ICaptchaProtected, ITenantUnscopedRequest;

/// <summary>
/// On success with MFA disabled: <see cref="AccessToken"/> + <see cref="RefreshToken"/> are set
/// and <see cref="MfaRequired"/> is false. With MFA enabled: a permission-less "pending" access
/// token is returned with <see cref="MfaRequired"/> = true; the client must call MFA-verify to
/// upgrade it. The controller moves the refresh token into an HttpOnly cookie.
/// </summary>
public sealed record LoginResponse(string AccessToken, int ExpiresIn, string RefreshToken, bool MfaRequired);
