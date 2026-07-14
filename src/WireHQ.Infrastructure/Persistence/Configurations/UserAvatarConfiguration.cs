using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WireHQ.Domain.Identity;

namespace WireHQ.Infrastructure.Persistence.Configurations;

/// <summary>Avatar image bytes, one row per user (unique <c>user_id</c>). Kept out of the user row so the
/// blob is only loaded when an avatar is actually served.</summary>
public sealed class UserAvatarConfiguration : IEntityTypeConfiguration<UserAvatar>
{
    public void Configure(EntityTypeBuilder<UserAvatar> builder)
    {
        builder.ToTable("user_avatars", "identity");
        builder.HasKey(a => a.Id);
        builder.Property(a => a.Id).ValueGeneratedNever();

        builder.HasIndex(a => a.UserId).IsUnique();
        builder.Property(a => a.Data).IsRequired();
        builder.Property(a => a.ContentType).HasMaxLength(64).IsRequired();
    }
}
