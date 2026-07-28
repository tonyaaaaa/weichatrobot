using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using WechatRobot.Infrastructure.Identity;
using WechatRobot.Infrastructure.Persistence.Entities;

namespace WechatRobot.Infrastructure.Persistence;

public sealed class WechatRobotDbContext : IdentityDbContext<ApplicationUser, IdentityRole<Guid>, Guid>
{
    public WechatRobotDbContext(DbContextOptions<WechatRobotDbContext> options)
        : base(options)
    {
    }

    public DbSet<RobotConfigEntity> RobotConfigs => Set<RobotConfigEntity>();
    public DbSet<GroupProfileEntity> GroupProfiles => Set<GroupProfileEntity>();
    public DbSet<GroupRuleEntity> GroupRules => Set<GroupRuleEntity>();
    public DbSet<GroupProfileTagEntity> GroupProfileTags => Set<GroupProfileTagEntity>();
    public DbSet<KnowledgeTagEntity> KnowledgeTags => Set<KnowledgeTagEntity>();
    public DbSet<ConversationMessageEntity> ConversationMessages => Set<ConversationMessageEntity>();
    public DbSet<ConversationSessionEntity> ConversationSessions => Set<ConversationSessionEntity>();
    public DbSet<RetrievalAuditEntity> RetrievalAudits => Set<RetrievalAuditEntity>();
    public DbSet<DurableJobEntity> DurableJobs => Set<DurableJobEntity>();
    public DbSet<SendCommandEntity> SendCommands => Set<SendCommandEntity>();
    public DbSet<DeadLetterEntity> DeadLetters => Set<DeadLetterEntity>();
    public DbSet<ModelConfigEntity> ModelConfigs => Set<ModelConfigEntity>();
    public DbSet<WorkToolOperationAuditEntity> WorkToolOperationAudits => Set<WorkToolOperationAuditEntity>();
    public DbSet<WorkToolOperationConfirmationEntity> WorkToolOperationConfirmations => Set<WorkToolOperationConfirmationEntity>();
    public DbSet<KnowledgeDocumentEntity> KnowledgeDocuments => Set<KnowledgeDocumentEntity>();
    public DbSet<KnowledgeDocumentVersionEntity> KnowledgeDocumentVersions => Set<KnowledgeDocumentVersionEntity>();
    public DbSet<KnowledgeChunkEntity> KnowledgeChunks => Set<KnowledgeChunkEntity>();
    public DbSet<KnowledgeChunkPreviewEntity> KnowledgeChunkPreviews => Set<KnowledgeChunkPreviewEntity>();
    public DbSet<KnowledgeChunkTagEntity> KnowledgeChunkTags => Set<KnowledgeChunkTagEntity>();
    public DbSet<KnowledgeOcrPageEntity> KnowledgeOcrPages => Set<KnowledgeOcrPageEntity>();
    public DbSet<KnowledgeIndexJobEntity> KnowledgeIndexJobs => Set<KnowledgeIndexJobEntity>();
    public DbSet<HandoffCaseEntity> HandoffCases => Set<HandoffCaseEntity>();
    public DbSet<HandoffMessageEntity> HandoffMessages => Set<HandoffMessageEntity>();
    public DbSet<HandoffTransitionEntity> HandoffTransitions => Set<HandoffTransitionEntity>();
    public DbSet<KnowledgeCandidateEntity> KnowledgeCandidates => Set<KnowledgeCandidateEntity>();
    public DbSet<KnowledgeReviewEntity> KnowledgeReviews => Set<KnowledgeReviewEntity>();
    public DbSet<WorkerHeartbeatEntity> WorkerHeartbeats => Set<WorkerHeartbeatEntity>();
    public DbSet<SystemSettingEntity> SystemSettings => Set<SystemSettingEntity>();
    public DbSet<AdministrationAuditEntity> AdministrationAudits => Set<AdministrationAuditEntity>();
    public DbSet<WorkToolRateLimitBucketEntity> WorkToolRateLimitBuckets =>
        Set<WorkToolRateLimitBucketEntity>();
    public DbSet<GroupHumanAgentEntity> GroupHumanAgents =>
        Set<GroupHumanAgentEntity>();
    public DbSet<MemoryCandidateEntity> MemoryCandidates => Set<MemoryCandidateEntity>();
    public DbSet<MemoryObservationEntity> MemoryObservations => Set<MemoryObservationEntity>();
    public DbSet<MemoryEntryEntity> MemoryEntries => Set<MemoryEntryEntity>();
    public DbSet<MemoryAuditEntity> MemoryAudits => Set<MemoryAuditEntity>();

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        ValidatePersistenceInvariants();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(
        bool acceptAllChangesOnSuccess,
        CancellationToken cancellationToken = default)
    {
        ValidatePersistenceInvariants();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.ApplyConfigurationsFromAssembly(typeof(WechatRobotDbContext).Assembly);

        foreach (var entityType in builder.Model.GetEntityTypes())
        {
            foreach (var property in entityType.GetProperties())
            {
                if (property.ClrType == typeof(DateTime))
                {
                    property.SetValueConverter(new UtcDateTimeConverter());
                    property.SetColumnType("datetime(6)");
                }
                else if (property.ClrType == typeof(DateTime?))
                {
                    property.SetValueConverter(new UtcNullableDateTimeConverter());
                    property.SetColumnType("datetime(6)");
                }
            }
        }
    }

    private void ValidatePersistenceInvariants()
    {
        foreach (var entry in ChangeTracker.Entries()
                     .Where(entry => entry.State is EntityState.Added or EntityState.Modified))
        {
            switch (entry.Entity)
            {
                case RobotConfigEntity robot
                    when robot.SendRateLimitPerMinute is < 1 or > 60:
                    throw new InvalidOperationException(
                        $"{nameof(RobotConfigEntity.SendRateLimitPerMinute)} must be between 1 and 60.");

                case GroupProfileEntity group
                    when group.HandoffPausePolicy is not ("Group" or "Sender"):
                    throw new InvalidOperationException(
                        $"{nameof(GroupProfileEntity.HandoffPausePolicy)} must be Group or Sender.");

                case GroupProfileEntity group
                    when group.ArchivedAtUtc is not null && group.IsEnabled:
                    throw new InvalidOperationException(
                        $"{nameof(GroupProfileEntity.ArchivedAtUtc)} requires a disabled group.");

                case GroupProfileEntity group
                    when group.RegistrationSource is not ("Manual" or "WorkToolImport"):
                    throw new InvalidOperationException(
                        $"{nameof(GroupProfileEntity.RegistrationSource)} must be Manual or WorkToolImport.");

                case GroupHumanAgentEntity agent
                    when agent.VerificationStatus is not ("Verified" or "Missing" or "Conflict" or "Stale"):
                    throw new InvalidOperationException(
                        $"{nameof(GroupHumanAgentEntity.VerificationStatus)} must be Verified, Missing, Conflict, or Stale.");
            }
        }
    }
}
