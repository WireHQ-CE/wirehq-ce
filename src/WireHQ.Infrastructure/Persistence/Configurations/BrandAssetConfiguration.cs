using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WireHQ.Domain.Platform;

namespace WireHQ.Infrastructure.Persistence.Configurations;

/// <summary>
/// An operator brand image (logo/favicon), stored as a bounded blob. **Install-global** — not tenant-owned, lives in
/// the <c>core</c> schema (inherits the blanket grant, outside the RLS tenant loop; docs/34 §4.3). Content-addressed:
/// a fresh id is minted on every replace, so the serve URL is immutable-cacheable.
/// </summary>
public sealed class BrandAssetConfiguration : IEntityTypeConfiguration<BrandAsset>
{
    public void Configure(EntityTypeBuilder<BrandAsset> builder)
    {
        builder.ToTable("brand_assets", "core");
        builder.HasKey(a => a.Id);
        builder.Property(a => a.Id).ValueGeneratedNever();

        builder.Property(a => a.Kind).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(a => a.ContentType).HasMaxLength(64).IsRequired();
        builder.Property(a => a.Bytes).IsRequired();
        builder.Property(a => a.UpdatedAtUtc).IsRequired();
    }
}
