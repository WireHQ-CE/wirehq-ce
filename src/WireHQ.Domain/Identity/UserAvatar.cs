using WireHQ.Domain.Common;

namespace WireHQ.Domain.Identity;

/// <summary>
/// A user's avatar image bytes, stored in its own table (keyed by <see cref="UserId"/>) so the blob is
/// never loaded with normal <see cref="User"/> queries. Served by the public avatars endpoint; the
/// <see cref="User.AvatarUpdatedAtUtc"/> marker tells callers when one exists (and busts caches).
/// </summary>
public sealed class UserAvatar : Entity
{
    // EF Core
    private UserAvatar()
    {
    }

    private UserAvatar(Guid id, Guid userId, byte[] data, string contentType, DateTimeOffset updatedAtUtc)
        : base(id)
    {
        UserId = userId;
        Data = data;
        ContentType = contentType;
        UpdatedAtUtc = updatedAtUtc;
    }

    public Guid UserId { get; private set; }

    public byte[] Data { get; private set; } = [];

    public string ContentType { get; private set; } = null!;

    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public static UserAvatar Create(Guid userId, byte[] data, string contentType, DateTimeOffset now) =>
        new(Guid.CreateVersion7(), userId, data, contentType, now);

    public void Replace(byte[] data, string contentType, DateTimeOffset now)
    {
        Data = data;
        ContentType = contentType;
        UpdatedAtUtc = now;
    }
}
