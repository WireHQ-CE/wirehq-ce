using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WireHQ.Modules.Orchestration.Domain;

namespace WireHQ.Modules.Orchestration.Persistence.Configurations;

public sealed class DeploymentTargetConfiguration : IEntityTypeConfiguration<DeploymentTarget>
{
    public void Configure(EntityTypeBuilder<DeploymentTarget> builder)
    {
        builder.ToTable("deployment_targets", "orch");
        builder.HasKey(t => t.Id);
        builder.Property(t => t.Id).ValueGeneratedNever();
        builder.Ignore(t => t.DomainEvents);

        builder.Property(t => t.OrganizationId).IsRequired();
        builder.Property(t => t.InstanceId).IsRequired();
        builder.Property(t => t.Kind).HasConversion<string>().HasMaxLength(16);
        builder.Property(t => t.KeyCustody).HasConversion<string>().HasMaxLength(16);
        builder.Property(t => t.InterfaceName).HasMaxLength(15).IsRequired();

        // One binding per instance.
        builder.HasIndex(t => t.InstanceId).IsUnique();
    }
}
