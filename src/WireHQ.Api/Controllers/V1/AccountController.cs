using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WireHQ.Api.Controllers;
using WireHQ.Application.Features.Account.Avatar;
using WireHQ.Application.Features.Account.ChangePassword;
using WireHQ.Application.Features.Account.Notifications;
using WireHQ.Application.Features.Account.UpdateProfile;
using WireHQ.Application.Features.Mfa.ConfirmTotp;
using WireHQ.Application.Features.Mfa.DisableMfa;
using WireHQ.Application.Features.Mfa.EnrollTotp;
using WireHQ.Shared.Results;

namespace WireHQ.Api.Controllers.V1;

/// <summary>Self-service account management for the signed-in user (security, MFA, profile).</summary>
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/account")]
[Authorize]
public sealed class AccountController : ApiControllerBase
{
    /// <summary>Begin TOTP enrolment — returns the secret + QR to add to an authenticator app.</summary>
    [HttpPost("mfa/enroll")]
    public async Task<IActionResult> EnrollMfa(CancellationToken cancellationToken) =>
        Ok(await Sender.Send(new EnrollTotpCommand(), cancellationToken));

    /// <summary>Confirm enrolment with the first code; enables MFA and returns one-time recovery codes.</summary>
    [HttpPost("mfa/confirm")]
    public async Task<IActionResult> ConfirmMfa(ConfirmMfaRequest request, CancellationToken cancellationToken) =>
        Ok(await Sender.Send(new ConfirmTotpCommand(request.Code), cancellationToken));

    [HttpPost("mfa/disable")]
    public async Task<IActionResult> DisableMfa(DisableMfaRequest request, CancellationToken cancellationToken) =>
        NoContent(await Sender.Send(new DisableMfaCommand(request.Password), cancellationToken));

    [HttpPost("password")]
    public async Task<IActionResult> ChangePassword(ChangePasswordRequest request, CancellationToken cancellationToken) =>
        NoContent(await Sender.Send(new ChangePasswordCommand(request.CurrentPassword, request.NewPassword), cancellationToken));

    [HttpPatch("profile")]
    public async Task<IActionResult> UpdateProfile(UpdateProfileRequest request, CancellationToken cancellationToken) =>
        NoContent(await Sender.Send(
            new UpdateProfileCommand(
                request.FirstName, request.LastName, request.Username,
                request.JobTitle, request.Phone, request.Timezone, request.Language),
            cancellationToken));

    /// <summary>Upload (or replace) the signed-in user's avatar image (multipart, ≤ 512 KB; png/jpeg/webp).</summary>
    [HttpPost("avatar")]
    [RequestSizeLimit(1_048_576)]
    public async Task<IActionResult> UploadAvatar(IFormFile file, CancellationToken cancellationToken)
    {
        if (file is null || file.Length == 0)
        {
            return Problem(Error.Validation("avatar.empty", "No image was uploaded."));
        }

        using var ms = new MemoryStream();
        await file.CopyToAsync(ms, cancellationToken);
        return NoContent(await Sender.Send(new UploadAvatarCommand(ms.ToArray(), file.ContentType), cancellationToken));
    }

    [HttpDelete("avatar")]
    public async Task<IActionResult> RemoveAvatar(CancellationToken cancellationToken) =>
        NoContent(await Sender.Send(new RemoveAvatarCommand(), cancellationToken));

    /// <summary>The signed-in user's notification opt-ins.</summary>
    [HttpGet("notifications")]
    public async Task<IActionResult> GetNotifications(CancellationToken cancellationToken) =>
        Ok(await Sender.Send(new GetNotificationPreferencesQuery(), cancellationToken));

    [HttpPut("notifications")]
    public async Task<IActionResult> UpdateNotifications(NotificationPreferencesRequest request, CancellationToken cancellationToken) =>
        NoContent(await Sender.Send(
            new UpdateNotificationPreferencesCommand(
                request.SecurityAlerts, request.VpnStatusAlerts, request.ProductAnnouncements,
                request.BillingNotifications, request.MarketingEmails, request.ServiceStatusAlerts),
            cancellationToken));
}

public sealed record NotificationPreferencesRequest(
    bool SecurityAlerts,
    bool VpnStatusAlerts,
    bool ProductAnnouncements,
    bool BillingNotifications,
    bool MarketingEmails,
    bool ServiceStatusAlerts);

public sealed record ConfirmMfaRequest(string Code);

public sealed record DisableMfaRequest(string Password);

public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);

public sealed record UpdateProfileRequest(
    string FirstName,
    string LastName,
    string? Username,
    string? JobTitle,
    string? Phone,
    string? Timezone,
    string? Language);
