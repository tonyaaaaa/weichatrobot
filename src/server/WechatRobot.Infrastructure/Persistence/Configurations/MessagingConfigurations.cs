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
        builder.Property(entity => entity.EncryptedWorkToolRobotId).HasMaxLength(512);
        builder.Property(entity => entity.CallbackRouteCode).HasMaxLength(64);
        builder.Property(entity => entity.CallbackSecretHash).HasMaxLength(128).IsRequired();
        builder.Property(entity => entity.EncryptedCallbackSecret).HasMaxLength(1024);
        builder.Property(entity => entity.PreviousCallbackSecretHash).HasMaxLength(128);
        builder.Property(entity => entity.SendRateLimitPerMinute).HasDefaultValue(50).IsRequired();
        builder.Property(entity => entity.SendRateTokens).HasPrecision(10, 4).HasDefaultValue(50m).IsRequired();
        builder.Property(entity => entity.SendLeaseOwner).HasMaxLength(128);
        builder.Property(entity => entity.SendCoordinationVersion).IsConcurrencyToken();
        builder.ToTable(table => table.HasCheckConstraint("CK_robot_config_send_rate_limit", "`SendRateLimitPerMinute` BETWEEN 1 AND 60"));
        builder.HasIndex(entity => entity.WorkToolRobotId).IsUnique();
        builder.HasIndex(entity => entity.CallbackRouteCode).IsUnique();
    }
}

internal sealed class GroupProfileConfiguration : IEntityTypeConfiguration<GroupProfileEntity>
{
    public void Configure(EntityTypeBuilder<GroupProfileEntity> builder)
    {
        builder.ToTable("group_profile");
        builder.HasKey(entity => entity.Id);
        builder.Property(entity => entity.ExternalGroupId).HasMaxLength(128);
        builder.Property(entity => entity.Name).HasMaxLength(256).IsRequired();
        builder.Property(entity => entity.WorkToolGroupRemark).HasMaxLength(256);
        builder.Property(entity => entity.HandoffPausePolicy).HasMaxLength(16).HasDefaultValue("Group").IsRequired();
        builder.Property(entity => entity.ConfigurationVersion).HasDefaultValue(0).IsConcurrencyToken();
        builder.Property(entity => entity.StateVersion).HasDefaultValue(0).IsConcurrencyToken();
        builder.Property(entity => entity.WebSearchResultCount).HasDefaultValue(5);
        builder.Property(entity => entity.WebSearchRecency).HasMaxLength(16).HasDefaultValue("NoLimit").IsRequired();
        builder.Property(entity => entity.WebSearchDomainFilter).HasMaxLength(512);
        builder.Property(entity => entity.WebSearchContentSize).HasMaxLength(16).HasDefaultValue("Medium").IsRequired();
        builder.Property(entity => entity.FinalNoEvidencePolicy).HasMaxLength(32).HasDefaultValue("InsufficientEvidence").IsRequired();
        builder.Property(entity => entity.RegistrationSource)
            .HasMaxLength(32)
            .HasDefaultValue("Manual")
            .IsRequired();
        builder.ToTable(table => table.HasCheckConstraint("CK_group_profile_handoff_pause_policy", "`HandoffPausePolicy` IN ('Group','Sender')"));
        builder.ToTable(table => table.HasCheckConstraint(
            "CK_group_profile_registration_source",
            "`RegistrationSource` IN ('Manual','WorkToolImport')"));
        builder.HasIndex(entity => new { entity.RobotConfigId, entity.Name, entity.WorkToolGroupRemark });
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
        builder.Property(entity => entity.Version).IsConcurrencyToken();
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
        builder.Property(entity => entity.ProcessingState).HasMaxLength(32).HasDefaultValue("completed").IsRequired();
        builder.Property(entity => entity.TerminalDecision).HasMaxLength(32);
        builder.Property(entity => entity.TerminalReason).HasMaxLength(64);
        builder.Property(entity => entity.TerminalEvidenceJson).HasColumnType("json");
        builder.Property(entity => entity.FallbackHash).HasMaxLength(128).IsRequired();
        builder.Property(entity => entity.GroupName).HasMaxLength(256).IsRequired();
        builder.Property(entity => entity.GroupRemark).HasMaxLength(256);
        builder.Property(entity => entity.SenderDisplayName).HasMaxLength(128).IsRequired();
        builder.Property(entity => entity.StableSenderId).HasMaxLength(128);
        builder.Property(entity => entity.Text).HasColumnType("longtext").IsRequired();
        builder.HasIndex(entity => entity.WorkToolMessageId).IsUnique();
        builder.HasIndex(entity => new { entity.FallbackHash, entity.FallbackWindowStartUtc }).IsUnique();
        builder.HasIndex(entity => entity.InReplyToMessageId).IsUnique();
        builder.HasIndex(entity => new { entity.ConversationSessionId, entity.SessionSequence }).IsUnique();
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
        builder.Property(entity => entity.LeaseOwner).HasMaxLength(128);
        builder.Property(entity => entity.Version).IsConcurrencyToken();
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
        builder.Property(entity => entity.AnswerSource).HasMaxLength(32).HasDefaultValue("none").IsRequired();
        builder.Property(entity => entity.WebSearchFailureCode).HasMaxLength(64);
        builder.Property(entity => entity.WebSearchSourcesJson).HasColumnType("json").IsRequired();
        builder.Property(entity => entity.MemoryRecallJson).HasColumnType("json").IsRequired();
        builder.Property(entity => entity.EvidenceJson).HasColumnType("json").IsRequired();
        builder.Property(entity => entity.InputSummaryJson).HasColumnType("json").IsRequired();
        builder.HasIndex(entity => entity.ConversationMessageId).IsUnique();
        builder.HasIndex(entity => new { entity.GroupProfileId, entity.CreatedAtUtc });
        builder.HasOne<ConversationMessageEntity>().WithMany().HasForeignKey(entity => entity.ConversationMessageId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<GroupProfileEntity>().WithMany().HasForeignKey(entity => entity.GroupProfileId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<ModelConfigEntity>().WithMany().HasForeignKey(entity => entity.ModelConfigurationId).OnDelete(DeleteBehavior.Restrict);
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
        builder.HasIndex(entity => entity.GroupProfileId);
        builder.HasIndex(entity => new { entity.Status, entity.NextAttemptAtUtc });
        builder.HasIndex(entity => entity.RelatedConversationMessageId).IsUnique();
        builder.HasOne<ConversationMessageEntity>().WithMany().HasForeignKey(entity => entity.RelatedConversationMessageId).OnDelete(DeleteBehavior.Restrict);
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
        builder.Property(entity => entity.ReconciliationReason).HasMaxLength(256);
        builder.Property(entity => entity.WorkToolCommandMessageId).HasMaxLength(128);
        builder.Property(entity => entity.WorkToolSuccessListJson).HasColumnType("json");
        builder.Property(entity => entity.WorkToolFailListJson).HasColumnType("json");
        builder.Property(entity => entity.Version).IsConcurrencyToken();
        builder.HasIndex(entity => entity.IdempotencyKey).IsUnique();
        builder.HasIndex(entity => new { entity.Status, entity.NextAttemptAtUtc });
        builder.HasIndex(entity => entity.WorkToolCommandMessageId).IsUnique();
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
        builder.Property(entity => entity.NormalizedName).HasMaxLength(128).IsRequired();
        builder.Property(entity => entity.Provider).HasMaxLength(128).IsRequired();
        builder.Property(entity => entity.ConfigurationType).HasMaxLength(32).IsRequired();
        builder.Property(entity => entity.DefaultConfigurationType)
            .HasMaxLength(32)
            .HasComputedColumnSql(
                "CASE WHEN `IsDefault` = 1 THEN `ConfigurationType` ELSE NULL END",
                stored: true);
        builder.Property(entity => entity.BaseUrl).HasMaxLength(2048).IsRequired();
        builder.Property(entity => entity.Model).HasMaxLength(256).IsRequired();
        builder.Property(entity => entity.WebSearchMode).HasMaxLength(32).HasDefaultValue("None").IsRequired();
        builder.Property(entity => entity.EncryptedApiKey).HasColumnType("longtext");
        builder.Property(entity => entity.TimeoutSeconds).IsRequired();
        builder.Property(entity => entity.MaxRetries).IsRequired();
        builder.Property(entity => entity.ConnectionStatus).HasMaxLength(16).IsRequired();
        builder.Property(entity => entity.LastTestFailureSummary).HasMaxLength(1024);
        builder.Property(entity => entity.TestedConfigurationFingerprint).HasMaxLength(64);
        builder.Property(entity => entity.Version).IsConcurrencyToken();
        builder.HasIndex(entity => entity.NormalizedName).IsUnique();
        builder.HasIndex(entity => entity.DefaultConfigurationType).IsUnique();
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
        builder.Property(entity => entity.EncryptedCommandJson).HasMaxLength(8192);
        builder.Property(entity => entity.LeaseOwner).HasMaxLength(128);
        builder.Property(entity => entity.WorkToolCommandMessageId).HasMaxLength(128);
        builder.Property(entity => entity.WorkToolSuccessListJson).HasColumnType("json");
        builder.Property(entity => entity.WorkToolFailListJson).HasColumnType("json");
        builder.Property(entity => entity.ReconciliationStatus).HasMaxLength(32);
        builder.Property(entity => entity.Version).IsConcurrencyToken();
        builder.HasIndex(entity => entity.CreatedAtUtc);
        builder.HasIndex(entity => new { entity.Status, entity.CreatedAtUtc });
        builder.HasIndex(entity => entity.WorkToolCommandMessageId).IsUnique();
        builder.HasIndex(entity => new
        {
            entity.ReconciliationStatus,
            entity.ReconciliationNextAttemptAtUtc
        }).HasDatabaseName("IX_worktool_operation_audit_reconciliation_due");
        builder.HasOne<RobotConfigEntity>().WithMany().HasForeignKey(entity => entity.RobotConfigId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<GroupProfileEntity>().WithMany()
            .HasForeignKey(entity => entity.ReconciledGroupProfileId)
            .HasConstraintName("FK_worktool_operation_audit_reconciled_group")
            .OnDelete(DeleteBehavior.SetNull);
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
