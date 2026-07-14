using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WireHQ.Domain.Teams;

namespace WireHQ.Infrastructure.Persistence.Configurations;

/// <summary>
/// The team↔membership association. A normal join entity (not an owned type) so it appends
/// cleanly to an already-persisted <see cref="Team"/>; reached only through the team aggregate,
/// which is itself tenant-filtered. (docs/03-multi-tenancy.md)
/// </summary>
public sealed class TeamMemberConfiguration : IEntityTypeConfiguration<TeamMember>
{
    public void Configure(EntityTypeBuilder<TeamMember> builder)
    {
        builder.ToTable("team_members", "core");

        // Natural composite key — a membership belongs to a team at most once.
        builder.HasKey(m => new { m.TeamId, m.MembershipId });

        builder.Property(m => m.MembershipId).IsRequired();
        builder.Property(m => m.AddedAtUtc).IsRequired();

        builder.HasIndex(m => m.MembershipId);
    }
}
