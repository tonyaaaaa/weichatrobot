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
        builder.Property(entity => entity.CallbackSecretHash).HasMaxLength(128).IsRequired();
        builder.Property(entity => entity.SendRateLimitPerMinute).HasDefaultValue(50).IsRequired();
        builder.Property(entity => entity.SendRateTokens).HasPrecision(10, 4).HasDefaultValue(50m).IsRequired();
        builder.Property(entity => entity.SendLeaseOwner).HasMaxLength(128);
        builder.Property(entity => entity.SendCoordinationVersion).IsConcurrencyToken();
        builder.ToTable(table => table.HasCheckConstraint("CK_robot_config_send_rate_limit", "`SendRateLimitPerMinute` BETWEEN 1 AND 60"));
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

internal sealed class GroupProfileTagConfiguration : IEntityTypeConfiguration<GroupProfileTagEntity>
{
    public void Configure(EntityTypeBuilder<GroupProfileTagEntity> builder)
    {
        builder.ToTable("group_profile_tag");
        builder.HasKey(entity => new { entity.GroupProfileId, entity.KnowledgeTagId });
        builder.HasOne<GroupProfileEntity>().WithMany().HasForeignKey(entity => entity.GroupProfileId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<KnowledgeTagEntity>().WithMany().HasForeignKey(entity => entity.KnowledgeTagId).OnDelete(DeleteBehavior.Restrict);
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
        builder.Property(entity => entity.Direction).HasMaxLength(16).HasDefaultValue("inbound").IsRequired();
        builder.Property(entity => entity.Role).HasMaxLength(16).HasDefaultValue("user").IsRequired();
        builder.Property(entity => entity.FallbackHash).HasMaxLength(128).IsRequired();
        builder.Property(entity => entity.SenderExternalUserId).HasMaxLength(128).IsRequired();
        builder.Property(entity => entity.Text).HasColumnType("longtext").IsRequired();
        builder.HasIndex(entity => entity.WorkToolMessageId).IsUnique();
        builder.HasIndex(entity => new { entity.FallbackHash, entity.FallbackWindowStartUtc }).IsUnique();
        builder.HasIndex(entity => entity.InReplyToMessageId).IsUnique();
        builder.HasOne<RobotConfigEntity>().WithMany().HasForeignKey(entity => entity.RobotConfigId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<GroupProfileEntity>().WithMany().HasForeignKey(entity => entity.GroupProfileId).OnDelete(DeleteBehavior.SetNull);
        builder.HasOne<ConversationSessionEntity>().WithMany().HasForeignKey(entity => entity.ConversationSessionId).OnDelete(DeleteBehavior.SetNull);
    }
}

internal sealed class ConversationSessionConfiguration : IEntityTypeConfiguration<ConversationSessionEntity>
{
    public void Configure(EntityTypeBuilder<ConversationSessionEntity> builder)
    {
        builder.ToTable("conversation_session");
        builder.HasKey(entity => entity.Id);
        builder.Property(entity => entity.SenderScopeKey).HasMaxLength(128).IsRequired();
        builder.Property(entity => entity.Summary).HasColumnType("longtext");
        builder.HasIndex(entity => new { entity.GroupProfileId, entity.SenderScopeKey }).IsUnique();
        builder.HasOne<GroupProfileEntity>().WithMany().HasForeignKey(entity => entity.GroupProfileId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class RetrievalAuditConfiguration : IEntityTypeConfiguration<RetrievalAuditEntity>
{
    public void Configure(EntityTypeBuilder<RetrievalAuditEntity> builder)
    {
        builder.ToTable("retrieval_audit");
        builder.HasKey(entity => entity.Id);
        builder.Property(entity => entity.Decision).HasMaxLength(32).IsRequired();
        builder.Property(entity => entity.ContextPolicy).HasMaxLength(1024).IsRequired();
        builder.Property(entity => entity.FailureCode).HasMaxLength(64);
        builder.Property(entity => entity.EvidenceJson).HasColumnType("json").IsRequired();
        builder.HasIndex(entity => entity.ConversationMessageId).IsUnique();
        builder.HasIndex(entity => new { entity.GroupProfileId, entity.CreatedAtUtc });
        builder.HasOne<ConversationMessageEntity>().WithMany().HasForeignKey(entity => entity.ConversationMessageId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<GroupProfileEntity>().WithMany().HasForeignKey(entity => entity.GroupProfileId).OnDelete(DeleteBehavior.Restrict);
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
        builder.Property(entity => entity.LeaseOwner).HasMaxLength(128);
        builder.Property(entity => entity.Version).IsConcurrencyToken();
        builder.HasIndex(entity => new { entity.Status, entity.NextAttemptAtUtc });
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
        builder.Property(entity => entity.LeaseOwner).HasMaxLength(128);
        builder.Property(entity => entity.Version).IsConcurrencyToken();
        builder.HasIndex(entity => entity.IdempotencyKey).IsUnique();
        builder.HasIndex(entity => new { entity.Status, entity.NextAttemptAtUtc });
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
        builder.Property(entity => entity.ConfigurationType).HasMaxLength(32).IsRequired();
        builder.Property(entity => entity.BaseUrl).HasMaxLength(2048).IsRequired();
        builder.Property(entity => entity.Model).HasMaxLength(256).IsRequired();
        builder.Property(entity => entity.EncryptedApiKey).HasColumnType("longtext");
        builder.Property(entity => entity.TimeoutSeconds).IsRequired();
        builder.Property(entity => entity.MaxRetries).IsRequired();
        builder.HasIndex(entity => entity.Name).IsUnique();
    }
}

internal sealed class WorkToolOperationAuditConfiguration : IEntityTypeConfiguration<WorkToolOperationAuditEntity>
{
    public void Configure(EntityTypeBuilder<WorkToolOperationAuditEntity> builder)
    {
        builder.ToTable("worktool_operation_audit");
        builder.HasKey(entity => entity.Id);
        builder.Property(entity => entity.OperatorName).HasMaxLength(256).IsRequired();
        builder.Property(entity => entity.Operation).HasMaxLength(64).IsRequired();
        builder.Property(entity => entity.WorkToolCommandNumber).IsRequired();
        builder.Property(entity => entity.SanitizedRequestJson).HasColumnType("longtext").IsRequired();
        builder.Property(entity => entity.Status).HasMaxLength(32).IsRequired();
        builder.Property(entity => entity.Result).HasMaxLength(1024);
        builder.HasIndex(entity => entity.CreatedAtUtc);
    }
}

internal sealed class WorkToolOperationConfirmationConfiguration : IEntityTypeConfiguration<WorkToolOperationConfirmationEntity>
{
    public void Configure(EntityTypeBuilder<WorkToolOperationConfirmationEntity> builder)
    {
        builder.ToTable("worktool_operation_confirmation");
        builder.HasKey(entity => entity.Id);
        builder.Property(entity => entity.TokenHash).HasMaxLength(128).IsRequired();
        builder.Property(entity => entity.OperatorName).HasMaxLength(256).IsRequired();
        builder.Property(entity => entity.PayloadHash).HasMaxLength(128).IsRequired();
        builder.Property(entity => entity.Version).IsConcurrencyToken();
        builder.HasIndex(entity => entity.TokenHash).IsUnique();
    }
}
