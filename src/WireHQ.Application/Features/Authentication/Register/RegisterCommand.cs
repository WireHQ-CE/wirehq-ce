using WireHQ.Application.Common.Messaging;

namespace WireHQ.Application.Features.Authentication.Register;

/// <summary>
/// Self-serve signup: creates the user and provisions a personal workspace (org) with them as owner.
/// Low-friction by design — only first/last name, email, password and Terms acceptance are required;
/// business details are optional and gathered later by the Welcome Wizard. When no
/// <see cref="OrganizationName"/> is given, a default "{First}'s Workspace" is created.
/// Authentication itself is a separate step (<c>LoginCommand</c>).
/// </summary>
public sealed record RegisterCommand(
    string Email,
    string Password,
    string FirstName,
    string LastName,
    bool AcceptTerms,
    string? OrganizationName = null,
    string? TurnstileToken = null) : ICommand<RegisterResponse>, ICaptchaProtected, ITenantUnscopedRequest;

public sealed record RegisterResponse(Guid UserId, Guid OrganizationId, string OrganizationSlug);
