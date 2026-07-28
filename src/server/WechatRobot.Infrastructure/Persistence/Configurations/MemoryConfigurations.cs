using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WechatRobot.Infrastructure.Identity;
using WechatRobot.Infrastructure.Persistence.Entities;

namespace WechatRobot.Infrastructure.Persistence.Configurations;

internal sealed class MemoryCandidateConfiguration : IEntityTypeConfiguration<MemoryCandidateEntity>
{
    public void Configure(EntityTypeBuilder<MemoryCandidateEntity> builder)
    {
        builder.ToTable("memory_candidate");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.ScopeType).HasMaxLength(16).IsRequired();
        builder.Property(x => x.ScopeHash).HasMaxLength(64).IsRequired();
        builder.Property(x => x.SubjectKey).HasMaxLength(256);
        builder.Property(x => x.SubjectDisplayName).HasMaxLength(256);
        builder.Property(x => x.MemoryType).HasMaxLength(32).IsRequired();
        builder.Property(x => x.Content).HasColumnType("longtext").IsRequired();
        builder.Property(x => x.NormalizedKey).HasMaxLength(512).IsRequired();
        builder.Property(x => x.Fingerprint).HasMaxLength(64).IsRequired();
        builder.Property(x => x.Status).HasMaxLength(32).IsRequired().IsConcurrencyToken();
        builder.Property(x => x.Version).IsConcurrencyToken();
        builder.HasIndex(x => new { x.ScopeHash, x.MemoryType, x.Fingerprint })
            .HasDatabaseName("UX_memory_candidate_scope_fingerprint")
            .IsUnique();
        builder.HasIndex(x => new { x.Status, x.UpdatedAtUtc });
        builder.HasIndex(x => x.PromotedMemoryEntryId).IsUnique();
        builder.HasIndex(x => x.KnowledgeCandidateId).IsUnique();
        builder.HasOne<RobotConfigEntity>().WithMany().HasForeignKey(x => x.RobotConfigId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<GroupProfileEntity>().WithMany().HasForeignKey(x => x.GroupProfileId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class MemoryObservationConfiguration : IEntityTypeConfiguration<MemoryObservationEntity>
{
    public void Configure(EntityTypeBuilder<MemoryObservationEntity> builder)
    {
        builder.ToTable("memory_observation");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.SourceContentHash).HasMaxLength(64).IsRequired();
        builder.Property(x => x.EvidenceSummary).HasMaxLength(1024).IsRequired();
        builder.HasIndex(x => new { x.MemoryCandidateId, x.ConversationMessageId }).IsUnique();
        builder.HasIndex(x => new { x.MemoryCandidateId, x.ConversationSessionId, x.ObservedAtUtc });
        builder.HasOne<MemoryCandidateEntity>().WithMany().HasForeignKey(x => x.MemoryCandidateId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<ConversationSessionEntity>().WithMany().HasForeignKey(x => x.ConversationSessionId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<ConversationMessageEntity>().WithMany().HasForeignKey(x => x.ConversationMessageId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<ModelConfigEntity>().WithMany().HasForeignKey(x => x.ModelConfigurationId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class MemoryEntryConfiguration : IEntityTypeConfiguration<MemoryEntryEntity>
{
    public void Configure(EntityTypeBuilder<MemoryEntryEntity> builder)
    {
        builder.ToTable("memory_entry");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.ScopeType).HasMaxLength(16).IsRequired();
        builder.Property(x => x.SubjectKey).HasMaxLength(256);
        builder.Property(x => x.SubjectDisplayName).HasMaxLength(256);
        builder.Property(x => x.MemoryType).HasMaxLength(32).IsRequired();
        builder.Property(x => x.Content).HasColumnType("longtext").IsRequired();
        builder.Property(x => x.NormalizedKey).HasMaxLength(512).IsRequired();
        builder.Property(x => x.Status).HasMaxLength(32).IsRequired().IsConcurrencyToken();
        builder.Property(x => x.Version).IsConcurrencyToken();
        builder.HasIndex(x => new { x.Status, x.ExpiresAtUtc });
        builder.HasIndex(x => new { x.ScopeType, x.RobotConfigId, x.GroupProfileId, x.SubjectKey, x.MemoryType });
        builder.HasIndex(x => x.SourceCandidateId).IsUnique();
        builder.HasOne<MemoryEntryEntity>().WithMany().HasForeignKey(x => x.SupersedesMemoryEntryId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<MemoryCandidateEntity>().WithMany().HasForeignKey(x => x.SourceCandidateId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<RobotConfigEntity>().WithMany().HasForeignKey(x => x.RobotConfigId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<GroupProfileEntity>().WithMany().HasForeignKey(x => x.GroupProfileId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class MemoryAuditConfiguration : IEntityTypeConfiguration<MemoryAuditEntity>
{
    public void Configure(EntityTypeBuilder<MemoryAuditEntity> builder)
    {
        builder.ToTable("memory_audit");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Action).HasMaxLength(32).IsRequired();
        builder.Property(x => x.ActorType).HasMaxLength(16).IsRequired();
        builder.Property(x => x.TargetType).HasMaxLength(32).IsRequired();
        builder.Property(x => x.OldStatus).HasMaxLength(32);
        builder.Property(x => x.NewStatus).HasMaxLength(32);
        builder.Property(x => x.ReasonCode).HasMaxLength(128).IsRequired();
        builder.HasIndex(x => new { x.TargetType, x.TargetId, x.CreatedAtUtc });
        builder.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.ActorUserId).OnDelete(DeleteBehavior.SetNull);
    }
}
