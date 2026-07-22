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
}
