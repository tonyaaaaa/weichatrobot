using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WechatRobot.Infrastructure.Persistence.Entities;
using WechatRobot.Infrastructure.Identity;

namespace WechatRobot.Infrastructure.Persistence.Configurations;

internal sealed class HandoffCaseConfiguration : IEntityTypeConfiguration<HandoffCaseEntity>
{
    public void Configure(EntityTypeBuilder<HandoffCaseEntity> builder)
    {
        builder.ToTable("handoff_case"); builder.HasKey(x => x.Id);
        builder.Property(x => x.State).HasMaxLength(32).IsRequired(); builder.Property(x => x.State).IsConcurrencyToken();
        builder.Property(x => x.ReasonCode).HasMaxLength(128).IsRequired(); builder.Property(x => x.EvidenceJson).HasColumnType("json").IsRequired();
        builder.Property(x => x.PauseScope).HasMaxLength(16).IsRequired(); builder.Property(x => x.StableSenderId).HasMaxLength(128);
        builder.Property(x => x.FinalAnswer).HasColumnType("longtext"); builder.Property(x => x.Version).IsConcurrencyToken();
        builder.Property(x => x.StartIdempotencyKey).HasMaxLength(128); builder.HasIndex(x => x.StartIdempotencyKey).IsUnique();
        builder.Property(x => x.RequestFingerprint).HasMaxLength(64);
        builder.HasIndex(x => x.QuestionMessageId).IsUnique(); builder.HasIndex(x => new { x.GroupProfileId, x.State });
        builder.HasOne<ConversationMessageEntity>().WithMany().HasForeignKey(x => x.QuestionMessageId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<RobotConfigEntity>().WithMany().HasForeignKey(x => x.RobotConfigId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<GroupProfileEntity>().WithMany().HasForeignKey(x => x.GroupProfileId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.AssigneeUserId).OnDelete(DeleteBehavior.SetNull);
        builder.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.ResolvedByUserId).OnDelete(DeleteBehavior.SetNull);
    }
}

internal sealed class HandoffMessageConfiguration : IEntityTypeConfiguration<HandoffMessageEntity>
{
    public void Configure(EntityTypeBuilder<HandoffMessageEntity> builder)
    {
        builder.ToTable("handoff_message"); builder.HasKey(x => x.Id); builder.Property(x => x.ExternalMessageId).HasMaxLength(128);
        builder.Property(x => x.SenderDisplayName).HasMaxLength(128).IsRequired(); builder.Property(x => x.AuthenticationKind).HasMaxLength(64).IsRequired();
        builder.Property(x => x.Text).HasColumnType("longtext").IsRequired(); builder.HasIndex(x => x.ExternalMessageId).IsUnique();
        builder.HasOne<HandoffCaseEntity>().WithMany().HasForeignKey(x => x.HandoffCaseId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.AuthenticatedUserId).OnDelete(DeleteBehavior.SetNull);
    }
}

internal sealed class HandoffTransitionConfiguration : IEntityTypeConfiguration<HandoffTransitionEntity>
{
    public void Configure(EntityTypeBuilder<HandoffTransitionEntity> builder)
    {
        builder.ToTable("handoff_transition"); builder.HasKey(x => x.Id); builder.Property(x => x.FromState).HasMaxLength(32).IsRequired();
        builder.Property(x => x.ToState).HasMaxLength(32).IsRequired(); builder.Property(x => x.ReasonCode).HasMaxLength(128).IsRequired();
        builder.Property(x => x.IdempotencyKey).HasMaxLength(128).IsRequired(); builder.HasIndex(x => x.IdempotencyKey).IsUnique();
        builder.HasIndex(x => new { x.HandoffCaseId, x.Sequence }).IsUnique();
        builder.HasOne<HandoffCaseEntity>().WithMany().HasForeignKey(x => x.HandoffCaseId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.ActorUserId).OnDelete(DeleteBehavior.SetNull);
    }
}

internal sealed class KnowledgeCandidateConfiguration : IEntityTypeConfiguration<KnowledgeCandidateEntity>
{
    public void Configure(EntityTypeBuilder<KnowledgeCandidateEntity> builder)
    {
        builder.ToTable("knowledge_candidate"); builder.HasKey(x => x.Id); builder.Property(x => x.Question).HasMaxLength(2048).IsRequired();
        builder.Property(x => x.Answer).HasColumnType("longtext").IsRequired(); builder.Property(x => x.EvidenceJson).HasColumnType("json").IsRequired();
        builder.Property(x => x.Status).HasMaxLength(32).IsRequired().IsConcurrencyToken(); builder.Property(x => x.Version).IsConcurrencyToken();
        builder.Property(x => x.SourceType).HasMaxLength(32).HasDefaultValue("HistoricalHandoff").IsRequired();
        builder.HasIndex(x => x.HandoffCaseId).IsUnique(); builder.HasIndex(x => x.KnowledgeDocumentVersionId).IsUnique();
        builder.HasIndex(x => x.SourceMemoryCandidateId).IsUnique();
        builder.HasOne<HandoffCaseEntity>().WithMany().HasForeignKey(x => x.HandoffCaseId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<ConversationMessageEntity>().WithMany().HasForeignKey(x => x.QuestionMessageId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<ConversationMessageEntity>().WithMany().HasForeignKey(x => x.SourceConversationMessageId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<MemoryCandidateEntity>().WithMany().HasForeignKey(x => x.SourceMemoryCandidateId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<KnowledgeDocumentVersionEntity>().WithMany().HasForeignKey(x => x.KnowledgeDocumentVersionId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class KnowledgeReviewConfiguration : IEntityTypeConfiguration<KnowledgeReviewEntity>
{
    public void Configure(EntityTypeBuilder<KnowledgeReviewEntity> builder)
    {
        builder.ToTable("knowledge_review"); builder.HasKey(x => x.Id); builder.Property(x => x.Decision).HasMaxLength(32).IsRequired();
        builder.Property(x => x.TagIdsJson).HasColumnType("json").IsRequired(); builder.Property(x => x.RevisedAnswer).HasColumnType("longtext");
        builder.Property(x => x.IdempotencyKey).HasMaxLength(128).IsRequired(); builder.HasIndex(x => x.IdempotencyKey).IsUnique();
        builder.Property(x => x.RequestFingerprint).HasMaxLength(64);
        builder.HasOne<KnowledgeCandidateEntity>().WithMany().HasForeignKey(x => x.KnowledgeCandidateId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.ReviewerUserId).OnDelete(DeleteBehavior.SetNull);
    }
}
