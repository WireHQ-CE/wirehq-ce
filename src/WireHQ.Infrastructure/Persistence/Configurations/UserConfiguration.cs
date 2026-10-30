using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WireHQ.Domain.Identity;
using WireHQ.Domain.ValueObjects;

namespace WireHQ.Infrastructure.Persistence.Configurations;

public sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> builder)
    {
        builder.ToTable("users", "identity");
        builder.HasKey(u => u.Id);
        builder.Property(u => u.Id).ValueGeneratedNever();
        builder.Ignore(u => u.DomainEvents);

        builder.OwnsOne(u => u.Email, email =>
        {
            email.Property(e => e.Value).HasColumnName("email").HasMaxLength(Email.MaxLength).IsRequired();
            email.HasIndex(e => e.Value).IsUnique();
        });
        builder.Navigation(u => u.Email).IsRequired();

        // Verified E.164 phone for SMS alerts (docs/36 §8, Wave 4). Optional owned value object → a nullable phone_e164
        // column. The filtered unique index enforces "a verified number is unique across users" (docs/36 §2) while
        // leaving unverified/absent numbers unconstrained (Postgres treats NULLs as distinct anyway).
        builder.OwnsOne(u => u.PhoneE164, phone =>
        {
            phone.Property(p => p.Value).HasColumnName("phone_e164").HasMaxLength(PhoneNumber.MaxLength);
            phone.HasIndex(p => p.Value).IsUnique().HasFilter("phone_verified = true");
        });
        builder.Property(u => u.PhoneVerified).IsRequired();
        builder.Property(u => u.PhoneVerifiedAtUtc);

        builder.Property(u => u.Name).HasMaxLength(User.MaxNameLength).IsRequired();
        builder.Property(u => u.FirstName).HasMaxLength(User.MaxNamePartLength);
        builder.Property(u => u.LastName).HasMaxLength(User.MaxNamePartLength);
        builder.Property(u => u.Username).HasMaxLength(User.MaxUsernameLength);
        // Unique when set; Postgres treats NULLs as distinct, so users without a username are unconstrained.
        builder.HasIndex(u => u.Username).IsUnique();
        builder.Property(u => u.JobTitle).HasMaxLength(User.MaxProfileFieldLength);
        builder.Property(u => u.Phone).HasMaxLength(User.MaxProfileFieldLength);
        builder.Property(u => u.Timezone).HasMaxLength(User.MaxProfileFieldLength);
        builder.Property(u => u.Language).HasMaxLength(16);
        builder.Property(u => u.PasswordHash).HasMaxLength(512).IsRequired();
        builder.Property(u => u.SecurityStamp).HasMaxLength(64).IsRequired();
        builder.Property(u => u.Status).HasConversion<string>().HasMaxLength(32);
        builder.Property(u => u.PlatformRole).HasConversion<string>().HasMaxLength(32);

        // Optimistic concurrency via Postgres' system xmin column (no stored column; bumped on every
        // UPDATE). A concurrent write fails with DbUpdateConcurrencyException (G-05 / HANDOFF gap #8).
        builder.Property<uint>("xmin").HasColumnName("xmin").HasColumnType("xid").IsRowVersion();
    }
}
