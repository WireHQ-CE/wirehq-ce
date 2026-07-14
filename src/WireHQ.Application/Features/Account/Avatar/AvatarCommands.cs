using Microsoft.EntityFrameworkCore;
using WireHQ.Application.Abstractions;
using WireHQ.Application.Abstractions.Persistence;
using WireHQ.Application.Common.Messaging;
using WireHQ.Domain.Auditing;
using WireHQ.Domain.Identity;
using WireHQ.Shared.Results;

namespace WireHQ.Application.Features.Account.Avatar;

/// <summary>The image content types accepted for avatars, and the maximum upload size.</summary>
public static class AvatarRules
{
    public const int MaxBytes = 512 * 1024;
    public static readonly IReadOnlySet<string> AllowedContentTypes =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "image/png", "image/jpeg", "image/webp" };
}

// ---- Upload ----

/// <summary>Stores (or replaces) the signed-in user's avatar image bytes.</summary>
public sealed record UploadAvatarCommand(byte[] Data, string ContentType) : ICommand;

public sealed class UploadAvatarCommandHandler(
    IApplicationDbContext dbContext,
    ICurrentUser currentUser,
    IDateTimeProvider clock,
    IAuditWriter audit)
    : ICommandHandler<UploadAvatarCommand>
{
    private static readonly Error NotAuthenticated = Error.Unauthorized("auth.unauthenticated", "Authentication is required.");

    public async Task<Result> Handle(UploadAvatarCommand command, CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not { } userId)
        {
            return NotAuthenticated;
        }

        if (command.Data is not { Length: > 0 })
        {
            return Error.Validation("avatar.empty", "No image was uploaded.");
        }

        if (command.Data.Length > AvatarRules.MaxBytes)
        {
            return Error.Validation("avatar.too_large", "The image must be 512 KB or smaller.");
        }

        if (!AvatarRules.AllowedContentTypes.Contains(command.ContentType))
        {
            return Error.Validation("avatar.unsupported_type", "Upload a PNG, JPEG or WebP image.");
        }

        var user = await dbContext.Users.IgnoreQueryFilters()
            .FirstOrDefaultAsync(u => u.Id == userId && !u.IsDeleted, cancellationToken);
        if (user is null)
        {
            return NotAuthenticated;
        }

        var now = clock.UtcNow;
        var avatar = await dbContext.UserAvatars.FirstOrDefaultAsync(a => a.UserId == userId, cancellationToken);
        if (avatar is null)
        {
            dbContext.UserAvatars.Add(UserAvatar.Create(userId, command.Data, command.ContentType.ToLowerInvariant(), now));
        }
        else
        {
            avatar.Replace(command.Data, command.ContentType.ToLowerInvariant(), now);
        }

        user.MarkAvatarUpdated(now);
        audit.Record("account.avatar_updated", AuditOutcome.Success, nameof(User), userId.ToString());
        return Result.Success();
    }
}

// ---- Remove ----

public sealed record RemoveAvatarCommand : ICommand;

public sealed class RemoveAvatarCommandHandler(
    IApplicationDbContext dbContext,
    ICurrentUser currentUser,
    IAuditWriter audit)
    : ICommandHandler<RemoveAvatarCommand>
{
    private static readonly Error NotAuthenticated = Error.Unauthorized("auth.unauthenticated", "Authentication is required.");

    public async Task<Result> Handle(RemoveAvatarCommand command, CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not { } userId)
        {
            return NotAuthenticated;
        }

        var user = await dbContext.Users.IgnoreQueryFilters()
            .FirstOrDefaultAsync(u => u.Id == userId && !u.IsDeleted, cancellationToken);
        if (user is null)
        {
            return NotAuthenticated;
        }

        var avatar = await dbContext.UserAvatars.FirstOrDefaultAsync(a => a.UserId == userId, cancellationToken);
        if (avatar is not null)
        {
            dbContext.UserAvatars.Remove(avatar);
        }

        user.ClearAvatar();
        audit.Record("account.avatar_removed", AuditOutcome.Success, nameof(User), userId.ToString());
        return Result.Success();
    }
}

// ---- Get (public) ----

public sealed record AvatarImage(byte[] Data, string ContentType);

/// <summary>Returns a user's avatar bytes for the public avatars endpoint (anonymous — avatars are public).</summary>
public sealed record GetAvatarQuery(Guid UserId) : IQuery<AvatarImage>;

public sealed class GetAvatarQueryHandler(IApplicationDbContext dbContext)
    : IQueryHandler<GetAvatarQuery, AvatarImage>
{
    private static readonly Error NotFound = Error.NotFound("avatar.not_found", "No avatar.");

    public async Task<Result<AvatarImage>> Handle(GetAvatarQuery query, CancellationToken cancellationToken)
    {
        var avatar = await dbContext.UserAvatars.AsNoTracking()
            .FirstOrDefaultAsync(a => a.UserId == query.UserId, cancellationToken);

        return avatar is null ? NotFound : new AvatarImage(avatar.Data, avatar.ContentType);
    }
}
