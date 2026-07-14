using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using WireHQ.Api.Controllers;
using WireHQ.Api.Extensions;
using WireHQ.Api.Security;
using WireHQ.Application.Features.Authentication.ForgotPassword;
using WireHQ.Application.Features.Authentication.Login;
using WireHQ.Application.Features.Authentication.Logout;
using WireHQ.Application.Features.Authentication.Me;
using WireHQ.Application.Features.Authentication.Refresh;
using WireHQ.Application.Features.Authentication.Register;
using WireHQ.Application.Features.Authentication.ResetPassword;
using WireHQ.Application.Features.Authentication.SecurityConfig;
using WireHQ.Application.Features.Authentication.Setup;
using WireHQ.Application.Features.Authentication.VerifyEmail;
using WireHQ.Application.Features.Authentication.VerifyMfa;

namespace WireHQ.Api.Controllers.V1;

[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/auth")]
[Authorize]
[EnableRateLimiting(ApiServiceCollectionExtensions.AuthRateLimitPolicy)]
public sealed class AuthController : ApiControllerBase
{
    /// <summary>Public security config the auth pages read to decide whether to render the CAPTCHA. No secret.</summary>
    [HttpGet("security-config")]
    [AllowAnonymous]
    public async Task<IActionResult> SecurityConfig(CancellationToken cancellationToken) =>
        Ok(await Sender.Send(new GetSecurityConfigQuery(), cancellationToken));

    /// <summary>Browser first-run setup: claims a fresh, ownerless self-hosted instance (docs/17). Only
    /// works while Setup:Enabled and no user exists — refused everywhere else, including all of SaaS.</summary>
    [HttpPost("setup")]
    [AllowAnonymous]
    public async Task<IActionResult> Setup(SetupRequest request, CancellationToken cancellationToken)
    {
        var result = await Sender.Send(
            new CompleteSetupCommand(
                request.Email, request.FirstName, request.LastName, request.Password, request.OrganizationName),
            cancellationToken);

        return Created(result);
    }

    [HttpPost("register")]
    [AllowAnonymous]
    public async Task<IActionResult> Register(RegisterRequest request, CancellationToken cancellationToken)
    {
        var result = await Sender.Send(
            new RegisterCommand(
                request.Email, request.Password, request.FirstName, request.LastName,
                request.AcceptTerms, request.OrganizationName, request.TurnstileToken),
            cancellationToken);

        return Created(result);
    }

    [HttpPost("login")]
    [AllowAnonymous]
    public async Task<IActionResult> Login(LoginRequest request, CancellationToken cancellationToken)
    {
        var result = await Sender.Send(new LoginCommand(request.Email, request.Password, request.TurnstileToken), cancellationToken);
        if (result.IsFailure)
        {
            return Problem(result.Error);
        }

        SetRefreshCookie(result.Value.RefreshToken);
        return base.Ok(new AuthTokenResponse(result.Value.AccessToken, result.Value.ExpiresIn, result.Value.MfaRequired));
    }

    [HttpPost("refresh")]
    [AllowAnonymous]
    public async Task<IActionResult> Refresh(CancellationToken cancellationToken)
    {
        if (!Request.Cookies.TryGetValue(RefreshTokenCookie.Name, out var refreshToken) || string.IsNullOrEmpty(refreshToken))
        {
            return Unauthorized();
        }

        var result = await Sender.Send(new RefreshTokenCommand(refreshToken), cancellationToken);
        if (result.IsFailure)
        {
            ClearRefreshCookie();
            return Problem(result.Error);
        }

        SetRefreshCookie(result.Value.RefreshToken);
        return base.Ok(new AuthTokenResponse(result.Value.AccessToken, result.Value.ExpiresIn, false));
    }

    [HttpPost("logout")]
    public async Task<IActionResult> Logout(CancellationToken cancellationToken)
    {
        var result = await Sender.Send(new LogoutCommand(), cancellationToken);
        ClearRefreshCookie();
        return NoContent(result);
    }

    [HttpGet("me")]
    public async Task<IActionResult> Me(CancellationToken cancellationToken) =>
        Ok(await Sender.Send(new GetMeQuery(), cancellationToken));

    /// <summary>Completes an MFA-pending login with a TOTP or recovery code, returning a full access token.</summary>
    [HttpPost("mfa/verify")]
    public async Task<IActionResult> VerifyMfa(VerifyMfaRequest request, CancellationToken cancellationToken)
    {
        var result = await Sender.Send(new VerifyMfaCommand(request.Code), cancellationToken);
        return result.IsFailure
            ? Problem(result.Error)
            : base.Ok(new AuthTokenResponse(result.Value.AccessToken, result.Value.ExpiresIn, false));
    }

    [HttpPost("forgot-password")]
    [AllowAnonymous]
    public async Task<IActionResult> ForgotPassword(ForgotPasswordRequest request, CancellationToken cancellationToken)
    {
        var result = await Sender.Send(new ForgotPasswordCommand(request.Email, request.TurnstileToken), cancellationToken);
        // Always 202 — the response never reveals whether the email is registered.
        return result.IsSuccess ? Accepted() : Problem(result.Error);
    }

    [HttpPost("reset-password")]
    [AllowAnonymous]
    public async Task<IActionResult> ResetPassword(ResetPasswordRequest request, CancellationToken cancellationToken) =>
        NoContent(await Sender.Send(new ResetPasswordCommand(request.Token, request.NewPassword, request.TurnstileToken), cancellationToken));

    /// <summary>Confirms a registration via the emailed link.</summary>
    [HttpPost("verify-email")]
    [AllowAnonymous]
    public async Task<IActionResult> VerifyEmail(VerifyEmailRequest request, CancellationToken cancellationToken) =>
        NoContent(await Sender.Send(new VerifyEmailCommand(request.Token), cancellationToken));

    /// <summary>Re-sends the verification email for the signed-in user.</summary>
    [HttpPost("resend-verification")]
    public async Task<IActionResult> ResendVerification(CancellationToken cancellationToken) =>
        NoContent(await Sender.Send(new ResendVerificationCommand(), cancellationToken));

    private void SetRefreshCookie(string token) => RefreshTokenCookie.Set(Response, token, Request.IsHttps);

    private void ClearRefreshCookie() => RefreshTokenCookie.Clear(Response);
}

public sealed record SetupRequest(
    string Email,
    string FirstName,
    string LastName,
    string Password,
    string? OrganizationName = null);

public sealed record RegisterRequest(
    string Email,
    string Password,
    string FirstName,
    string LastName,
    bool AcceptTerms,
    string? OrganizationName = null,
    string? TurnstileToken = null);

public sealed record LoginRequest(string Email, string Password, string? TurnstileToken = null);

public sealed record AuthTokenResponse(string AccessToken, int ExpiresIn, bool MfaRequired);

public sealed record VerifyMfaRequest(string Code);

public sealed record ForgotPasswordRequest(string Email, string? TurnstileToken = null);

public sealed record ResetPasswordRequest(string Token, string NewPassword, string? TurnstileToken = null);

public sealed record VerifyEmailRequest(string Token);
