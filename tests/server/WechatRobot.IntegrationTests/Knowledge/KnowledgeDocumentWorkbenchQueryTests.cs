using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WechatRobot.Infrastructure.Knowledge;
using WechatRobot.Infrastructure.Persistence;
using WechatRobot.Infrastructure.Persistence.Entities;

namespace WechatRobot.IntegrationTests.Knowledge;

public sealed class KnowledgeDocumentWorkbenchQueryTests
{
    [Fact]
    public async Task Workbench_exposes_pending_delete_and_terminal_retryability()
    {
        await using var database = NewDatabase();
        var document = Document("等待物理清理");
        document.Status = "disabled";
        document.IsDeleteRequested = true;
        var version = Version(
            document.Id,
            "PrivateChatDirect",
            null,
            "张伟");
        version.Status = "disabled";
        database.AddRange(
            document,
            version,
            new DurableJobEntity
            {
                Id = KnowledgeDocumentCleanupJobIdentity.Create(document.Id),
                JobType = "CleanupKnowledgeDocument",
                Status = "deadLetter",
                PayloadJson = JsonSerializer.Serialize(new
                {
                    documentId = document.Id
                })
            });
        await database.SaveChangesAsync(TestContext.Current.CancellationToken);

        var result = await new KnowledgeDocumentWorkbenchQuery(database)
            .GetAsync(
                document.Id,
                version.Id,
                TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.True(result.DocumentIsDeleteRequested);
        Assert.True(result.CanRetryPhysicalDelete);
    }

    [Fact]
    public async Task Private_chat_workbench_returns_approved_chunks_tags_and_source_message()
    {
        await using var database = NewDatabase();
        var source = Message("张伟", "签证还有多久出来？");
        var document = Document("签证进度");
        var version = Version(document.Id, "PrivateChatDirect", source.Id, "张伟");
        document.ActiveVersionId = version.Id;
        var tag = new KnowledgeTagEntity
        {
            Name = "签证知识",
            NormalizedName = "签证知识"
        };
        var chunk = new KnowledgeChunkEntity
        {
            KnowledgeDocumentVersionId = version.Id,
            Sequence = 0,
            Text = "问题：签证还有多久出来？\n答案：出签后会第一时间通知。",
            Question = "签证还有多久出来？",
            SynonymsJson = """["签证结果出来了吗"]""",
            Answer = "出签后会第一时间通知。",
            Status = "approved"
        };
        database.AddRange(source, document, version, tag, chunk);
        database.KnowledgeChunkTags.Add(new KnowledgeChunkTagEntity
        {
            KnowledgeChunkId = chunk.Id,
            KnowledgeTagId = tag.Id
        });
        await database.SaveChangesAsync(TestContext.Current.CancellationToken);

        var result = await new KnowledgeDocumentWorkbenchQuery(database).GetAsync(
            document.Id,
            version.Id,
            TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal(document.Id, result.DocumentId);
        Assert.Equal(version.Id, result.Version.Id);
        Assert.Equal("PrivateChatDirect", result.Version.SourceKind);
        Assert.Equal(tag.Id, Assert.Single(result.Version.Tags).Id);
        var content = Assert.Single(result.Chunks);
        Assert.Equal("签证还有多久出来？", content.Question);
        Assert.Equal(["签证结果出来了吗"], content.Synonyms);
        Assert.Equal("出签后会第一时间通知。", content.Answer);
        Assert.Equal("张伟", result.SourceEvidence?.ActorDisplayName);
        Assert.Equal("签证还有多久出来？", result.SourceEvidence?.Text);
        Assert.True(result.CanCreateRevision);
        Assert.Null(result.EditableRevision);
    }

    [Fact]
    public async Task Conversation_review_workbench_falls_back_through_candidate_for_legacy_source()
    {
        await using var database = NewDatabase();
        var source = Message("李娜", "加拿大签证一般多久？");
        var document = Document("加拿大签证");
        var version = Version(document.Id, "ConversationReview", null, null);
        document.ActiveVersionId = version.Id;
        var chunk = new KnowledgeChunkEntity
        {
            KnowledgeDocumentVersionId = version.Id,
            Sequence = 0,
            Text = "问题：加拿大签证一般多久？\n答案：以签证机关审核为准。",
            Question = "加拿大签证一般多久？",
            Answer = "以签证机关审核为准。",
            Status = "approved"
        };
        var candidate = new KnowledgeCandidateEntity
        {
            QuestionMessageId = source.Id,
            SourceType = "ManualCorrection",
            Question = "加拿大签证一般多久？",
            Answer = "以签证机关审核为准。",
            EvidenceJson = "{}",
            Status = "published",
            KnowledgeDocumentVersionId = version.Id,
            CreatedAtUtc = source.CreatedAtUtc,
            UpdatedAtUtc = source.CreatedAtUtc
        };
        database.AddRange(source, document, version, chunk, candidate);
        await database.SaveChangesAsync(TestContext.Current.CancellationToken);

        var result = await new KnowledgeDocumentWorkbenchQuery(database).GetAsync(
            document.Id,
            version.Id,
            TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal("李娜", result.SourceEvidence?.ActorDisplayName);
        Assert.Equal("加拿大签证一般多久？", result.SourceEvidence?.Text);
    }

    [Fact]
    public async Task Workbench_returns_empty_source_evidence_without_guessing_when_relationship_is_missing()
    {
        await using var database = NewDatabase();
        var document = Document("历史知识");
        var version = Version(document.Id, "ConversationReview", null, "同名成员");
        document.ActiveVersionId = version.Id;
        database.AddRange(
            document,
            version,
            new KnowledgeChunkEntity
            {
                KnowledgeDocumentVersionId = version.Id,
                Sequence = 0,
                Text = "历史内容",
                Status = "approved"
            },
            Message("同名成员", "不应按昵称猜测为来源"));
        await database.SaveChangesAsync(TestContext.Current.CancellationToken);

        var result = await new KnowledgeDocumentWorkbenchQuery(database).GetAsync(
            document.Id,
            version.Id,
            TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Null(result.SourceEvidence);
        Assert.Contains("missing", result.SourceEvidenceUnavailableReason, StringComparison.OrdinalIgnoreCase);
        var serialized = JsonSerializer.Serialize(result);
        Assert.DoesNotContain("不应按昵称猜测为来源", serialized);
    }

    private static KnowledgeDocumentEntity Document(string title) =>
        new()
        {
            Title = title,
            Status = "active",
            StateVersion = 3,
            CreatedAtUtc = UtcNow,
            UpdatedAtUtc = UtcNow
        };

    private static KnowledgeDocumentVersionEntity Version(
        Guid documentId,
        string sourceKind,
        Guid? sourceMessageId,
        string? actor) =>
        new()
        {
            KnowledgeDocumentId = documentId,
            Version = 1,
            OriginalFileName = "knowledge.txt",
            SafeFileName = "knowledge.txt",
            ContentType = "text/plain",
            Sha256 = new string('a', 64),
            SizeBytes = 32,
            ObjectKey = "internal/object",
            Status = "active",
            IsPublished = true,
            SourceKind = sourceKind,
            SourceConversationMessageId = sourceMessageId,
            SourceActorDisplayName = actor,
            CreatedAtUtc = UtcNow,
            UpdatedAtUtc = UtcNow
        };

    private static ConversationMessageEntity Message(string sender, string text) =>
        new()
        {
            RobotConfigId = Guid.NewGuid(),
            FallbackHash = Guid.NewGuid().ToString("N"),
            FallbackWindowStartUtc = UtcNow,
            GroupName = "机器人测试群",
            ChannelType = "PrivateExternal",
            RoomType = 2,
            PeerDisplayName = sender,
            SenderDisplayName = sender,
            Text = text,
            ReceivedAtUtc = UtcNow,
            CreatedAtUtc = UtcNow
        };

    private static WechatRobotDbContext NewDatabase() =>
        new(new DbContextOptionsBuilder<WechatRobotDbContext>()
            .UseInMemoryDatabase($"knowledge-workbench-{Guid.NewGuid():N}")
            .Options);

    private static readonly DateTime UtcNow =
        new(2026, 7, 30, 8, 0, 0, DateTimeKind.Utc);
}
