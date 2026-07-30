using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WechatRobot.Infrastructure.Persistence.Entities;

namespace WechatRobot.Infrastructure.Persistence.Configurations;

internal sealed class MessageIntentAuditConfiguration
    : IEntityTypeConfiguration<MessageIntentAuditEntity>
{
    public void Configure(EntityTypeBuilder<MessageIntentAuditEntity> builder)
    {
        builder.ToTable("message_intent_audit");
        builder.HasKey(item => item.Id);
        builder.Property(item => item.IntentDecision).HasMaxLength(16).IsRequired();
        builder.Property(item => item.IntentCategory).HasMaxLength(32).IsRequired();
        builder.Property(item => item.IntentReasonCode).HasMaxLength(64).IsRequired();
        builder.Property(item => item.IntentConfidence).HasPrecision(5, 4);
        builder.Property(item => item.FailureCode).HasMaxLength(64);
        builder.Property(item => item.IntentRuntimeMode).HasMaxLength(32).IsRequired();
        builder.Property(item => item.IntentAgentVersion).HasMaxLength(64).IsRequired();
        builder.HasIndex(item => item.ConversationMessageId).IsUnique();
        builder.HasIndex(item => new
        {
            item.GroupProfileId,
            item.IntentRuntimeMode,
            item.IntentDecision,
            item.IntentDecidedAtUtc
        }).HasDatabaseName("IX_intent_audit_diagnostics");
        builder.HasOne<ConversationMessageEntity>().WithMany()
            .HasForeignKey(item => item.ConversationMessageId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<GroupProfileEntity>().WithMany()
            .HasForeignKey(item => item.GroupProfileId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<ModelConfigEntity>().WithMany()
            .HasForeignKey(item => item.IntentModelConfigurationId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
