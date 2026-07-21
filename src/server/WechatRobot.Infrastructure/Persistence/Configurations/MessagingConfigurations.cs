using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WechatRobot.Infrastructure.Persistence.Entities;

namespace WechatRobot.Infrastructure.Persistence.Configurations;

internal sealed class RobotConfigConfiguration : IEntityTypeConfiguration<RobotConfigEntity>
{
    public void Configure(EntityTypeBuilder<RobotConfigEntity> builder)
    {
        builder.ToTable("robot_config");
        builder.HasKey(entity => entity.Id);
        builder.Property(entity => entity.Name).HasMaxLength(128).IsRequired();
        builder.Property(entity => entity.WorkToolRobotId).HasMaxLength(128).IsRequired();
        builder.HasIndex(entity => entity.WorkToolRobotId).IsUnique();
    }
}

internal sealed class GroupProfileConfiguration : IEntityTypeConfiguration<GroupProfileEntity>
{
    public void Configure(EntityTypeBuilder<GroupProfileEntity> builder)
    {
        builder.ToTable("group_profile");
        builder.HasKey(entity => entity.Id);
        builder.Property(entity => entity.ExternalGroupId).HasMaxLength(128).IsRequired();
        builder.Property(entity => entity.Name).HasMaxLength(256).IsRequired();
        builder.HasIndex(entity => new { entity.RobotConfigId, entity.ExternalGroupId }).IsUnique();
        builder.HasOne<RobotConfigEntity>().WithMany().HasForeignKey(entity => entity.RobotConfigId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class GroupRuleConfiguration : IEntityTypeConfiguration<GroupRuleEntity>
{
    public void Configure(EntityTypeBuilder<GroupRuleEntity> builder)
    {
        builder.ToTable("group_rule");
        builder.HasKey(entity => entity.Id);
        builder.Property(entity => entity.IncludePattern).HasMaxLength(1024).IsRequired();
        builder.Property(entity => entity.ExcludePattern).HasMaxLength(1024);
        builder.HasOne<GroupProfileEntity>().WithMany().HasForeignKey(entity => entity.GroupProfileId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class KnowledgeTagConfiguration : IEntityTypeConfiguration<KnowledgeTagEntity>
{
    public void Configure(EntityTypeBuilder<KnowledgeTagEntity> builder)
    {
        builder.ToTable("knowledge_tag");
        builder.HasKey(entity => entity.Id);
        builder.Property(entity => entity.Name).HasMaxLength(128).IsRequired();
        builder.Property(entity => entity.NormalizedName).HasMaxLength(128).IsRequired();
        builder.HasIndex(entity => entity.NormalizedName).IsUnique();
    }
}

internal sealed class ConversationMessageConfiguration : IEntityTypeConfiguration<ConversationMessageEntity>
{
    public void Configure(EntityTypeBuilder<ConversationMessageEntity> builder)
    {
        builder.ToTable("conversation_message");
        builder.HasKey(entity => entity.Id);
        builder.Property(entity => entity.WorkToolMessageId).HasMaxLength(128);
        builder.Property(entity => entity.FallbackHash).HasMaxLength(128).IsRequired();
        builder.Property(entity => entity.SenderExternalUserId).HasMaxLength(128).IsRequired();
        builder.Property(entity => entity.Text).HasColumnType("longtext").IsRequired();
        builder.HasIndex(entity => entity.WorkToolMessageId).IsUnique();
        builder.HasIndex(entity => new { entity.FallbackHash, entity.FallbackWindowStartUtc }).IsUnique();
        builder.HasOne<RobotConfigEntity>().WithMany().HasForeignKey(entity => entity.RobotConfigId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<GroupProfileEntity>().WithMany().HasForeignKey(entity => entity.GroupProfileId).OnDelete(DeleteBehavior.SetNull);
    }
}

internal sealed class DurableJobConfiguration : IEntityTypeConfiguration<DurableJobEntity>
{
    public void Configure(EntityTypeBuilder<DurableJobEntity> builder)
    {
        builder.ToTable("durable_job");
        builder.HasKey(entity => entity.Id);
        builder.Property(entity => entity.JobType).HasMaxLength(128).IsRequired();
        builder.Property(entity => entity.PayloadJson).HasColumnType("longtext").IsRequired();
        builder.Property(entity => entity.Status).HasMaxLength(32).IsRequired();
        builder.HasIndex(entity => new { entity.Status, entity.AvailableAtUtc });
    }
}

internal sealed class SendCommandConfiguration : IEntityTypeConfiguration<SendCommandEntity>
{
    public void Configure(EntityTypeBuilder<SendCommandEntity> builder)
    {
        builder.ToTable("send_command");
        builder.HasKey(entity => entity.Id);
        builder.Property(entity => entity.IdempotencyKey).HasMaxLength(128).IsRequired();
        builder.Property(entity => entity.PayloadJson).HasColumnType("longtext").IsRequired();
        builder.Property(entity => entity.Status).HasMaxLength(32).IsRequired();
        builder.HasIndex(entity => entity.IdempotencyKey).IsUnique();
        builder.HasOne<RobotConfigEntity>().WithMany().HasForeignKey(entity => entity.RobotConfigId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<GroupProfileEntity>().WithMany().HasForeignKey(entity => entity.GroupProfileId).OnDelete(DeleteBehavior.SetNull);
    }
}

internal sealed class DeadLetterConfiguration : IEntityTypeConfiguration<DeadLetterEntity>
{
    public void Configure(EntityTypeBuilder<DeadLetterEntity> builder)
    {
        builder.ToTable("dead_letter");
        builder.HasKey(entity => entity.Id);
        builder.Property(entity => entity.Reason).HasMaxLength(1024).IsRequired();
        builder.Property(entity => entity.PayloadJson).HasColumnType("longtext").IsRequired();
        builder.HasIndex(entity => entity.DurableJobId).IsUnique();
        builder.HasOne<DurableJobEntity>().WithMany().HasForeignKey(entity => entity.DurableJobId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class ModelConfigConfiguration : IEntityTypeConfiguration<ModelConfigEntity>
{
    public void Configure(EntityTypeBuilder<ModelConfigEntity> builder)
    {
        builder.ToTable("model_config");
        builder.HasKey(entity => entity.Id);
        builder.Property(entity => entity.Name).HasMaxLength(128).IsRequired();
        builder.Property(entity => entity.Provider).HasMaxLength(128).IsRequired();
        builder.Property(entity => entity.Model).HasMaxLength(256).IsRequired();
        builder.HasIndex(entity => entity.Name).IsUnique();
    }
}
