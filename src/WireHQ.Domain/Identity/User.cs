using WireHQ.Domain.Common;
using WireHQ.Domain.ValueObjects;
using WireHQ.Shared.Results;

namespace WireHQ.Domain.Identity;

/// <summary>
/// A platform-global identity. A <see cref="User"/> is not owned by a tenant — it joins
/// organizations through memberships (see docs/05-database.md), which is what lets one person
/// belong to many orgs with independent roles, like GitHub or Slack.
/// </summary>
public sealed class User : AggregateRoot, IAuditable, ISoftDeletable
{
    public const int MaxNameLength = 128;
    public const int MaxNamePartLength = 64;
    public const int MaxUsernameLength = 39;
    public const int MaxProfileFieldLength = 80;
    private const int MaxFailedAttempts = 5;

    // EF Core
    private User()
    {
    }

    private User(Guid id, Email email, string name, string passwordHash)
        : base(id)
    {
        Email = email;
        Name = name;
        PasswordHash = passwordHash;
        Status = UserStatus.PendingVerification;
        SecurityStamp = Guid.NewGuid().ToString("N");
    }

    public Email Email { get; private set; } = null!;

    /// <summary>The canonical display name. Kept in sync with <see cref="FirstName"/>+<see cref="LastName"/>
    /// when those are set (self-signup); invited users may have only this until they complete their profile.</summary>
    public string Name { get; private set; } = null!;

    public string? FirstName { get; private set; }

    public string? LastName { get; private set; }

    /// <summary>When the user accepted the Terms of Service (set at self-signup). Null for invited/legacy users.</summary>
    public DateTimeOffset? TermsAcceptedAtUtc { get; private set; }

    /// <summary>Optional handle (unique when set). Display/personalisation only — login is by email.</summary>
    public string? Username { get; private set; }

    public string? JobTitle { get; private set; }
    public string? Phone { get; private set; }

    /// <summary>IANA timezone id (e.g. "Europe/London"). Free string; the UI offers a curated list.</summary>
    public string? Timezone { get; private set; }

    /// <summary>BCP-47 language tag (e.g. "en").</summary>
    public string? Language { get; private set; }

    /// <summary>Set when the user has an avatar; the bytes live in <c>UserAvatar</c>. Doubles as a cache-buster.</summary>
    public DateTimeOffset? AvatarUpdatedAtUtc { get; private set; }

    /// <summary>Self-describing hash (see WireHQ.Identity password hasher). Never the plaintext.</summary>
    public string PasswordHash { get; private set; } = null!;

    public DateTimeOffset? PasswordUpdatedAtUtc { get; private set; }

    public bool EmailVerified { get; private set; }

    public UserStatus Status { get; private set; }

    /// <summary>Platform-operator tier (above org roles). Defaults to <see cref="PlatformRole.None"/>.</summary>
    public PlatformRole PlatformRole { get; private set; }

    public bool MfaEnabled { get; private set; }

    /// <summary>Rotated on credential/security changes; invalidates outstanding sessions/tokens.</summary>
    public string SecurityStamp { get; private set; } = null!;

    public int FailedSignInAttempts { get; private set; }

    public DateTimeOffset? LockoutEndsAtUtc { get; private set; }

    public DateTimeOffset? LastSignInAtUtc { get; private set; }

    // IAuditable
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public Guid? CreatedBy { get; private set; }
    public DateTimeOffset? UpdatedAtUtc { get; private set; }
    public Guid? UpdatedBy { get; private set; }

    // ISoftDeletable
    public bool IsDeleted { get; private set; }
    public DateTimeOffset? DeletedAtUtc { get; private set; }
    public Guid? DeletedBy { get; private set; }

    public bool IsLockedOut(DateTimeOffset nowUtc) =>
        Status == UserStatus.Locked && LockoutEndsAtUtc is { } until && until > nowUtc;

    public static Result<User> Register(string emailInput, string name, string passwordHash)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > MaxNameLength)
        {
            return UserErrors.InvalidName;
        }

        var emailResult = Email.Create(emailInput);
        if (emailResult.IsFailure)
        {
            return emailResult.Error;
        }

        if (string.IsNullOrWhiteSpace(passwordHash))
        {
            return UserErrors.MissingPasswordHash;
        }

        var user = new User(Guid.CreateVersion7(), emailResult.Value, name.Trim(), passwordHash);
        user.Raise(new UserRegistered(user.Id, user.Email.Value, user.Name));
        return user;
    }

    /// <summary>
    /// Self-serve signup with split first/last names. The display <see cref="Name"/> is derived from them;
    /// the parts are kept so the profile and personalised UI can use them.
    /// </summary>
    public static Result<User> Register(string emailInput, string firstName, string lastName, string passwordHash)
    {
        if (string.IsNullOrWhiteSpace(firstName) || firstName.Length > MaxNamePartLength ||
            string.IsNullOrWhiteSpace(lastName) || lastName.Length > MaxNamePartLength)
        {
            return UserErrors.InvalidName;
        }

        var fullName = $"{firstName.Trim()} {lastName.Trim()}";
        var result = Register(emailInput, fullName, passwordHash);
        if (result.IsFailure)
        {
            return result.Error;
        }

        var user = result.Value;
        user.FirstName = firstName.Trim();
        user.LastName = lastName.Trim();
        return user;
    }

    /// <summary>Records acceptance of the Terms of Service (compliance trail).</summary>
    public void AcceptTerms(DateTimeOffset acceptedAtUtc) => TermsAcceptedAtUtc = acceptedAtUtc;

    public Result VerifyEmail()
    {
        if (EmailVerified)
        {
            return Result.Success();
        }

        EmailVerified = true;
        if (Status == UserStatus.PendingVerification)
        {
            Status = UserStatus.Active;
        }

        Raise(new UserEmailVerified(Id));
        return Result.Success();
    }

    public void ChangePassword(string newPasswordHash)
    {
        PasswordHash = newPasswordHash;
        PasswordUpdatedAtUtc = DateTimeOffset.UtcNow;
        RotateSecurityStamp();
        Raise(new UserPasswordChanged(Id));
    }

    public Result ChangeName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > MaxNameLength)
        {
            return UserErrors.InvalidName;
        }

        Name = name.Trim();
        return Result.Success();
    }

    /// <summary>Sets the split first/last names and re-derives the display <see cref="Name"/>.</summary>
    public Result SetName(string firstName, string lastName)
    {
        if (string.IsNullOrWhiteSpace(firstName) || firstName.Length > MaxNamePartLength ||
            string.IsNullOrWhiteSpace(lastName) || lastName.Length > MaxNamePartLength)
        {
            return UserErrors.InvalidName;
        }

        FirstName = firstName.Trim();
        LastName = lastName.Trim();
        Name = $"{FirstName} {LastName}";
        return Result.Success();
    }

    /// <summary>Updates the optional profile fields (validated/normalised by the caller; stored trimmed).</summary>
    public void SetProfileDetails(string? username, string? jobTitle, string? phone, string? timezone, string? language)
    {
        Username = Clean(username);
        JobTitle = Clean(jobTitle);
        Phone = Clean(phone);
        Timezone = Clean(timezone);
        Language = Clean(language);

        static string? Clean(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();
    }

    /// <summary>Marks that the user now has an avatar (the bytes live in <c>UserAvatar</c>).</summary>
    public void MarkAvatarUpdated(DateTimeOffset at) => AvatarUpdatedAtUtc = at;

    /// <summary>Clears the avatar marker (after the bytes are removed).</summary>
    public void ClearAvatar() => AvatarUpdatedAtUtc = null;

    /// <summary>Grants/revokes platform-operator status. Rotates the security stamp so the privilege
    /// change takes effect immediately (outstanding tokens are invalidated).</summary>
    public void SetPlatformRole(PlatformRole role)
    {
        if (PlatformRole == role)
        {
            return;
        }

        PlatformRole = role;
        RotateSecurityStamp();
    }

    /// <summary>Records a successful sign-in and clears any failed-attempt state.</summary>
    public void RegisterSuccessfulSignIn(DateTimeOffset nowUtc)
    {
        FailedSignInAttempts = 0;
        LockoutEndsAtUtc = null;
        if (Status == UserStatus.Locked)
        {
            Status = UserStatus.Active;
        }

        LastSignInAtUtc = nowUtc;
    }

    /// <summary>Records a failed attempt; locks the account with backoff once the threshold is hit.</summary>
    public void RegisterFailedSignIn(DateTimeOffset nowUtc)
    {
        FailedSignInAttempts++;
        if (FailedSignInAttempts < MaxFailedAttempts)
        {
            return;
        }

        // Exponential-ish backoff capped at 30 minutes.
        var minutes = Math.Min(30, (int)Math.Pow(2, FailedSignInAttempts - MaxFailedAttempts) * 5);
        LockoutEndsAtUtc = nowUtc.AddMinutes(minutes);
        Status = UserStatus.Locked;
        Raise(new UserLockedOut(Id, LockoutEndsAtUtc.Value));
    }

    public void EnableMfa()
    {
        if (MfaEnabled)
        {
            return;
        }

        MfaEnabled = true;
        RotateSecurityStamp();
        Raise(new UserMfaEnabled(Id));
    }

    public void DisableMfa()
    {
        if (!MfaEnabled)
        {
            return;
        }

        MfaEnabled = false;
        RotateSecurityStamp();
        Raise(new UserMfaDisabled(Id));
    }

    private void RotateSecurityStamp() => SecurityStamp = Guid.NewGuid().ToString("N");
}

public static class UserErrors
{
    public static readonly Error InvalidName = Error.Validation("user.invalid_name", "Name is required and must be 128 characters or fewer.");
    public static readonly Error MissingPasswordHash = Error.Validation("user.missing_password", "A password is required.");
    public static readonly Error EmailTaken = Error.Conflict("user.email_taken", "An account with that email already exists.");
    public static readonly Error NotFound = Error.NotFound("user.not_found", "User was not found.");
    public static readonly Error InvalidCredentials = Error.Unauthorized("auth.invalid_credentials", "Invalid email or password.");
    public static readonly Error AccountLocked = Error.Forbidden("auth.account_locked", "Account is temporarily locked. Try again later.");
    public static readonly Error MfaRequired = Error.Unauthorized("auth.mfa_required", "Multi-factor authentication is required.");
    public static readonly Error RegistrationDisabled = Error.Forbidden("auth.registration_disabled", "Self-serve registration is disabled on this instance. Ask an administrator to invite you.");
}
