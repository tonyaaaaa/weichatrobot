using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WechatRobot.Infrastructure.Identity;
using WechatRobot.Infrastructure.Persistence.Entities;

namespace WechatRobot.Infrastructure.Persistence.Configurations;

internal sealed class GroupHumanAgentConfiguration
    : IEntityTypeConfiguration<GroupHumanAgentEntity>
{
    public void Configure(EntityTypeBuilder<GroupHumanAgentEntity> builder)
    {
        builder.ToTable("group_human_agent");
        builder.HasKey(agent => new { agent.GroupProfileId, agent.ApplicationUserId });
        builder.Property(agent => agent.WorkToolDisplayNameSnapshot)
            .HasMaxLength(128)
            .UseCollation("utf8mb4_bin")
            .IsRequired();
        builder.Property(agent => agent.VerificationStatus)
            .HasMaxLength(16)
            .HasDefaultValue("Stale")
            .IsRequired();
        builder.Property<Guid?>("DefaultGroupProfileId")
            .HasComputedColumnSql(
                "CASE WHEN `IsDefault` = 1 AND `IsEnabled` = 1 THEN `GroupProfileId` ELSE NULL END",
                stored: true);
        builder.HasIndex("DefaultGroupProfileId").IsUnique();
        builder.HasOne<GroupProfileEntity>()
            .WithMany()
            .HasForeignKey(agent => agent.GroupProfileId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<ApplicationUser>()
            .WithMany()
            .HasForeignKey(agent => agent.ApplicationUserId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.ToTable(table => table.HasCheckConstraint(
            "CK_group_human_agent_verification_status",
            "`VerificationStatus` IN ('Verified','Missing','Conflict','Stale')"));
    }
}
