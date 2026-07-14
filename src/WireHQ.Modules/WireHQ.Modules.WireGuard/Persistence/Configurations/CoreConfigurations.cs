using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WireHQ.Domain.ValueObjects;
using WireHQ.Modules.WireGuard.Domain;

namespace WireHQ.Modules.WireGuard.Persistence.Configurations;

public sealed class WireGuardNetworkConfiguration : IEntityTypeConfiguration<WireGuardNetwork>
{
    public void Configure(EntityTypeBuilder<WireGuardNetwork> builder)
    {
        builder.ToTable("networks", "wg");
        builder.HasKey(n => n.Id);
        builder.Property(n => n.Id).ValueGeneratedNever();
        builder.Ignore(n => n.DomainEvents);

        builder.Property(n => n.OrganizationId).IsRequired();
        builder.Property(n => n.Name).HasMaxLength(WireGuardNetwork.MaxNameLength).IsRequired();
        builder.Property(n => n.Cidr).HasMaxLength(64).IsRequired();

        builder.Property(n => n.Dns).HasColumnType("jsonb")
            .HasConversion(WireGuardJson.StringCollection, WireGuardJson.StringCollectionComparer);
        builder.Property(n => n.DefaultAllowedIps).HasColumnType("jsonb")
            .HasConversion(WireGuardJson.StringCollection, WireGuardJson.StringCollectionComparer);

        builder.HasIndex(n => new { n.OrganizationId, n.Id });
    }
}

public sealed class WireGuardInstanceConfiguration : IEntityTypeConfiguration<WireGuardInstance>
{
    public void Configure(EntityTypeBuilder<WireGuardInstance> builder)
    {
        builder.ToTable("instances", "wg");
        builder.HasKey(i => i.Id);
        builder.Property(i => i.Id).ValueGeneratedNever();
        builder.Ignore(i => i.DomainEvents);

        builder.Property(i => i.OrganizationId).IsRequired();
        builder.Property(i => i.NetworkId).IsRequired();
        builder.Property(i => i.Name).HasMaxLength(WireGuardInstance.MaxNameLength).IsRequired();
        builder.Property(i => i.Description).HasMaxLength(512);

        builder.OwnsOne(i => i.Slug, slug =>
            slug.Property(s => s.Value).HasColumnName("slug").HasMaxLength(Slug.MaxLength).IsRequired());
        builder.Navigation(i => i.Slug).IsRequired();

        builder.Property(i => i.ProviderType).HasConversion<string>().HasMaxLength(32);
        builder.Property(i => i.InterfaceAddress).HasMaxLength(64).IsRequired();
        builder.Property(i => i.PublicKey).HasMaxLength(64).IsRequired();
        builder.Property(i => i.EndpointHost).HasMaxLength(256);
        builder.Property(i => i.Status).HasConversion<string>().HasMaxLength(32);
        builder.Property(i => i.ExternalId).HasMaxLength(128);

        builder.Property(i => i.Dns).HasColumnType("jsonb")
            .HasConversion(WireGuardJson.StringCollection, WireGuardJson.StringCollectionComparer);
        builder.Property(i => i.ProviderSettings).HasColumnType("jsonb")
            .HasConversion(WireGuardJson.StringDictionary, WireGuardJson.StringDictionaryComparer);

        builder.HasIndex(i => new { i.OrganizationId, i.Id });
        builder.HasIndex(i => i.NetworkId);
    }
}

public sealed class PeerConfiguration : IEntityTypeConfiguration<Peer>
{
    public void Configure(EntityTypeBuilder<Peer> builder)
    {
        builder.ToTable("peers", "wg");
        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).ValueGeneratedNever();
        builder.Ignore(p => p.DomainEvents);

        builder.Property(p => p.OrganizationId).IsRequired();
        builder.Property(p => p.InstanceId).IsRequired();
        builder.Property(p => p.Name).HasMaxLength(Peer.MaxNameLength).IsRequired();
        builder.Property(p => p.Email).HasMaxLength(320);
        builder.Property(p => p.Department).HasMaxLength(128);
        builder.Property(p => p.DeviceType).HasMaxLength(64);
        builder.Property(p => p.Status).HasConversion<string>().HasMaxLength(32);
        builder.Property(p => p.PublicKey).HasMaxLength(64).IsRequired();
        builder.Property(p => p.AssignedAddress).HasMaxLength(64).IsRequired();
        builder.Property(p => p.LastEndpoint).HasMaxLength(128);

        builder.Property(p => p.AllowedIps).HasColumnType("jsonb")
            .HasConversion(WireGuardJson.StringCollection, WireGuardJson.StringCollectionComparer);

        // One address / one public key per instance (live rows only).
        builder.HasIndex(p => new { p.InstanceId, p.AssignedAddress }).IsUnique().HasFilter("is_deleted = false");
        builder.HasIndex(p => new { p.InstanceId, p.PublicKey }).IsUnique().HasFilter("is_deleted = false");
        builder.HasIndex(p => new { p.InstanceId, p.LastHandshakeAtUtc });
        builder.HasIndex(p => p.MembershipId);
    }
}
