using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WireHQ.Domain.Sessions;

namespace WireHQ.Infrastructure.Persistence.Configurations;

public sealed class UserSessionConfiguration : IEntityTypeConfiguration<UserSession>
{
    public void Configure(EntityTypeBuilder<UserSession> builder)
    {
        builder.ToTable("user_sessions", "identity");
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).ValueGeneratedNever();
        builder.Property(s => s.IpAddress).HasMaxLength(64);
        builder.Property(s => s.UserAgent).HasMaxLength(512);
        builder.HasIndex(s => s.UserId);
    }
}

public sealed class RefreshTokenConfiguration : IEntityTypeConfiguration<RefreshToken>
{
    public void Configure(EntityTypeBuilder<RefreshToken> builder)
    {
        builder.ToTable("refresh_tokens", "identity");
        builder.HasKey(t => t.Id);
        builder.Property(t => t.Id).ValueGeneratedNever();
        builder.Property(t => t.TokenHash).HasMaxLength(128).IsRequired();

        builder.HasIndex(t => t.TokenHash).IsUnique();
        builder.HasIndex(t => t.FamilyId);   // reuse-detection sweeps
        builder.HasIndex(t => t.SessionId);
    }
}

public sealed class MfaCredentialConfiguration : IEntityTypeConfiguration<MfaCredential>
{
    public void Configure(EntityTypeBuilder<MfaCredential> builder)
    {
        builder.ToTable("mfa_credentials", "identity");
        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).ValueGeneratedNever();
        builder.Property(c => c.Type).HasConversion<string>().HasMaxLength(32);
        builder.Property(c => c.Secret).HasMaxLength(1024).IsRequired();
        builder.Property(c => c.Label).HasMaxLength(128);
        builder.HasIndex(c => c.UserId);
    }
}

public sealed class RecoveryCodeConfiguration : IEntityTypeConfiguration<RecoveryCode>
{
    public void Configure(EntityTypeBuilder<RecoveryCode> builder)
    {
        builder.ToTable("recovery_codes", "identity");
        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).ValueGeneratedNever();
        builder.Property(c => c.CodeHash).HasMaxLength(128).IsRequired();
        builder.HasIndex(c => c.UserId);
    }
}
