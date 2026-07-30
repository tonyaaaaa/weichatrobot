using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WechatRobot.Infrastructure.Persistence.Entities;

namespace WechatRobot.Infrastructure.Persistence.Configurations;

internal sealed class PrivateKnowledgeIngestBatchConfiguration : IEntityTypeConfiguration<PrivateKnowledgeIngestBatchEntity>
{
    public void Configure(EntityTypeBuilder<PrivateKnowledgeIngestBatchEntity> builder)
    {
        builder.ToTable("private_knowledge_ingest_batch");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.SourceActorDisplayName).HasMaxLength(128).IsRequired();
        builder.Property(x => x.Status).HasMaxLength(32).IsRequired().IsConcurrencyToken();
        builder.Property(x => x.FailureCode).HasMaxLength(128);
        builder.Property(x => x.ReceivedNotificationState).HasMaxLength(32).IsRequired();
        builder.Property(x => x.FinalNotificationState).HasMaxLength(32).IsRequired();
        builder.Property(x => x.Version).IsConcurrencyToken();
        builder.HasIndex(x => x.SourceConversationMessageId).IsUnique();
        builder.HasIndex(x => new { x.Status, x.UpdatedAtUtc });
        builder.HasOne<RobotConfigEntity>().WithMany().HasForeignKey(x => x.RobotConfigId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<ConversationMessageEntity>().WithMany().HasForeignKey(x => x.SourceConversationMessageId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<ModelConfigEntity>().WithMany().HasForeignKey(x => x.ModelConfigurationId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class PrivateKnowledgeIngestItemConfiguration : IEntityTypeConfiguration<PrivateKnowledgeIngestItemEntity>
{
    public void Configure(EntityTypeBuilder<PrivateKnowledgeIngestItemEntity> builder)
    {
        builder.ToTable("private_knowledge_ingest_item");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Question).HasMaxLength(2048).IsRequired();
        builder.Property(x => x.Answer).HasColumnType("longtext").IsRequired();
        builder.Property(x => x.ChangeKind).HasMaxLength(32).IsRequired();
        builder.Property(x => x.QuestionFingerprint).HasMaxLength(64).IsRequired();
        builder.Property(x => x.AnswerFingerprint).HasMaxLength(64).IsRequired();
        builder.Property(x => x.ProposedTagsJson).HasColumnType("json").IsRequired();
        builder.Property(x => x.ResolvedTagIdsJson).HasColumnType("json").IsRequired();
        builder.Property(x => x.FailureCode).HasMaxLength(128);
        builder.HasIndex(x => new { x.BatchId, x.Sequence }).IsUnique();
        builder.HasOne<PrivateKnowledgeIngestBatchEntity>().WithMany().HasForeignKey(x => x.BatchId).OnDelete(DeleteBehavior.Cascade);
    }
}
