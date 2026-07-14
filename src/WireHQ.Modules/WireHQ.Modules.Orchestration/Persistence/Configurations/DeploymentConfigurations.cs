using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WireHQ.Modules.Orchestration.Domain;

namespace WireHQ.Modules.Orchestration.Persistence.Configurations;

public sealed class DeploymentJobConfiguration : IEntityTypeConfiguration<DeploymentJob>
{
    public void Configure(EntityTypeBuilder<DeploymentJob> builder)
    {
        builder.ToTable("deployment_jobs", "orch");
        builder.HasKey(j => j.Id);
        builder.Property(j => j.Id).ValueGeneratedNever();

        builder.Property(j => j.OrganizationId).IsRequired();
        builder.Property(j => j.InstanceId).IsRequired();
        builder.Property(j => j.Type).HasConversion<string>().HasMaxLength(24);
        builder.Property(j => j.Status).HasConversion<string>().HasMaxLength(16);
        builder.Property(j => j.IdempotencyKey).HasMaxLength(128).IsRequired();
        builder.Property(j => j.CorrelationId).HasMaxLength(64);
        builder.Property(j => j.Error).HasMaxLength(2048);
        builder.Ignore(j => j.DomainEvents);

        // The dispatcher claims the oldest Pending job: WHERE status = 'Pending' ORDER BY created_at.
        builder.HasIndex(j => new { j.Status, j.CreatedAtUtc });
        builder.HasIndex(j => new { j.OrganizationId, j.InstanceId });

        builder.HasMany(j => j.Events)
            .WithOne()
            .HasForeignKey(e => e.DeploymentJobId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.Metadata.FindNavigation(nameof(DeploymentJob.Events))!
            .SetPropertyAccessMode(PropertyAccessMode.Field);
    }
}

public sealed class DeploymentEventConfiguration : IEntityTypeConfiguration<DeploymentEvent>
{
    public void Configure(EntityTypeBuilder<DeploymentEvent> builder)
    {
        builder.ToTable("deployment_events", "orch");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).ValueGeneratedNever();

        builder.Property(e => e.OrganizationId).IsRequired();
        builder.Property(e => e.Phase).HasMaxLength(32).IsRequired();
        builder.Property(e => e.Detail).HasMaxLength(2048);

        builder.HasIndex(e => e.DeploymentJobId);
    }
}
