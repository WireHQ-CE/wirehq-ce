using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WireHQ.Modules.Orchestration.Domain;

namespace WireHQ.Modules.Orchestration.Persistence.Configurations;

public sealed class InstanceRuntimeStatusConfiguration : IEntityTypeConfiguration<InstanceRuntimeStatus>
{
    public void Configure(EntityTypeBuilder<InstanceRuntimeStatus> builder)
    {
        builder.ToTable("instance_runtime_status", "orch");
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).ValueGeneratedNever();
        builder.Ignore(s => s.DomainEvents);

        builder.Property(s => s.OrganizationId).IsRequired();
        builder.Property(s => s.InstanceId).IsRequired();
        builder.Property(s => s.State).HasMaxLength(16);
        builder.Property(s => s.DesiredConfigHash).HasMaxLength(64);
        builder.Property(s => s.ActualConfigHash).HasMaxLength(64);
        builder.Property(s => s.DriftDetail).HasMaxLength(512);

        // One runtime-status row per instance (upserted).
        builder.HasIndex(s => s.InstanceId).IsUnique();
    }
}
