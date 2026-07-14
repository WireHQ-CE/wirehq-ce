using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using WireHQ.Domain.Organizations;
using WireHQ.Domain.ValueObjects;

namespace WireHQ.Infrastructure.Persistence.Configurations;

public sealed class OrganizationConfiguration : IEntityTypeConfiguration<Organization>
{
    public void Configure(EntityTypeBuilder<Organization> builder)
    {
        builder.ToTable("organizations", "core");
        builder.HasKey(o => o.Id);
        builder.Property(o => o.Id).ValueGeneratedNever();
        builder.Ignore(o => o.DomainEvents);

        builder.OwnsOne(o => o.Slug, slug =>
        {
            slug.Property(s => s.Value).HasColumnName("slug").HasMaxLength(Slug.MaxLength).IsRequired();
            slug.HasIndex(s => s.Value).IsUnique();
        });
        builder.Navigation(o => o.Slug).IsRequired();

        builder.Property(o => o.Name).HasMaxLength(Organization.MaxNameLength).IsRequired();
        builder.Property(o => o.Status).HasConversion<string>().HasMaxLength(32);
        builder.Property(o => o.Edition).HasConversion<string>().HasMaxLength(32);
        builder.Property(o => o.DataRegion).HasMaxLength(32);

        builder.Property(o => o.LegalName).HasMaxLength(200);
        builder.Property(o => o.Website).HasMaxLength(256);
        builder.Property(o => o.Industry).HasMaxLength(100);
        builder.Property(o => o.CompanySize).HasMaxLength(32);
        builder.Property(o => o.Country).HasMaxLength(64);
        builder.Property(o => o.Timezone).HasMaxLength(64);

        // Optimistic concurrency via Postgres' system xmin column (no stored column; bumped on every
        // UPDATE). A concurrent write fails with DbUpdateConcurrencyException (G-05 / HANDOFF gap #8).
        builder.Property<uint>("xmin").HasColumnName("xmin").HasColumnType("xid").IsRowVersion();
    }
}

// NB: the billing entity configurations live in their own file (BillingConfigurations.cs) — a
// SaaS-only surface the Community Edition strip removes (docs/17 §5).

public sealed class OrganizationSettingsConfiguration : IEntityTypeConfiguration<OrganizationSettings>
{
    public void Configure(EntityTypeBuilder<OrganizationSettings> builder)
    {
        builder.ToTable("organization_settings", "core");
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).ValueGeneratedNever();

        builder.HasIndex(s => s.OrganizationId).IsUnique();

        builder.Property(s => s.RequireMfa).HasDefaultValue(false);
        builder.Property(s => s.SessionIdleTimeoutMinutes);

        // jsonb-backed collections — settings/entitlements are read together, rarely queried.
        builder.Property(s => s.EnabledModules)
            .HasColumnType("jsonb")
            .HasConversion(JsonConverters.StringCollection, JsonComparers.StringCollection);

        builder.Property(s => s.Flags)
            .HasColumnType("jsonb")
            .HasConversion(JsonConverters.StringDictionary, JsonComparers.StringDictionary);
    }
}

/// <summary>JSON value converters for jsonb-backed value collections.</summary>
internal static class JsonConverters
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static readonly ValueConverter<IReadOnlyCollection<string>, string> StringCollection =
        new(v => JsonSerializer.Serialize(v, Options),
            v => JsonSerializer.Deserialize<IReadOnlyCollection<string>>(v, Options) ?? new List<string>());

    public static readonly ValueConverter<IReadOnlyDictionary<string, string>, string> StringDictionary =
        new(v => JsonSerializer.Serialize(v, Options),
            v => JsonSerializer.Deserialize<IReadOnlyDictionary<string, string>>(v, Options) ?? new Dictionary<string, string>());
}

internal static class JsonComparers
{
    public static readonly ValueComparer<IReadOnlyCollection<string>> StringCollection =
        new((a, b) => a!.SequenceEqual(b!),
            v => v.Count,
            v => v.ToList());

    public static readonly ValueComparer<IReadOnlyDictionary<string, string>> StringDictionary =
        new((a, b) => a!.Count == b!.Count && !a.Except(b).Any(),
            v => v.Count,
            v => v.ToDictionary(kv => kv.Key, kv => kv.Value));
}
