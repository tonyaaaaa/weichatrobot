using DotNet.Testcontainers.Builders;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MySql.Data.MySqlClient;
using System.Text.Json;
using Testcontainers.MySql;
using WechatRobot.Application.Conversations;
using WechatRobot.Application.Jobs;
using WechatRobot.Application.Knowledge;
using WechatRobot.Application.Knowledge.Ocr;
using WechatRobot.Application.Knowledge.Parsing;
using WechatRobot.Application.Models;
using WechatRobot.Application.Security;
using WechatRobot.Application.Storage;
using WechatRobot.Infrastructure.Conversations;
using WechatRobot.Infrastructure.Knowledge;
using WechatRobot.Infrastructure.Knowledge.Ocr;
using WechatRobot.Infrastructure.Persistence;
using WechatRobot.Infrastructure.Persistence.Entities;
using WechatRobot.Worker.Jobs;

namespace WechatRobot.IntegrationTests.Knowledge;

public sealed class KnowledgeRetrievalMySql57CompatibilityTests : IAsyncLifetime
{
    private readonly MySqlContainer database = new MySqlBuilder("mysql:5.7.44")
        .WithDatabase("wechatrobot")
        .WithUsername("wechatrobot")
        .WithPassword("wechatrobot-tests-password")
        .WithCommand("--character-set-server=utf8mb4", "--collation-server=utf8mb4_bin")
        .WithTmpfsMount("/var/lib/mysql")
        .Build();

    public async ValueTask InitializeAsync() =>
        await database.StartAsync(TestContext.Current.CancellationToken);

    public async ValueTask DisposeAsync() =>
        await database.DisposeAsync();

    [Fact]
    public async Task Retrieval_loads_vector_hits_without_mysql57_guid_collection_parameters()
    {
        var connection = new MySqlConnectionStringBuilder(database.GetConnectionString())
        {
            SslMode = MySqlSslMode.Disabled,
            Pooling = false
        };
        var factory = new TestDbContextFactory(connection.ConnectionString);
        var tag = new KnowledgeTagEntity { Name = "签证", NormalizedName = "签证" };
        var document = new KnowledgeDocumentEntity
        {
            Title = "日本签证",
            Status = "active",
            ActiveCollectionName = "kb_cosine_3_g1",
            ActiveEmbeddingDimension = 3,
            ActiveDistance = "cosine",
            ActiveIndexGeneration = 1
        };
        var version = new KnowledgeDocumentVersionEntity
        {
            KnowledgeDocumentId = document.Id,
            Version = 1,
            OriginalFileName = "visa.txt",
            SafeFileName = "visa.txt",
            ContentType = "text/plain",
            Sha256 = new string('a', 64),
            ObjectKey = "visa.txt",
            Status = "active",
            IsPublished = true,
            IndexCollectionName = document.ActiveCollectionName,
            EmbeddingDimension = 3,
            VectorDistance = "cosine",
            IndexGeneration = 1
        };
        document.ActiveVersionId = version.Id;
        var firstChunk = new KnowledgeChunkEntity
        {
            KnowledgeDocumentVersionId = version.Id,
            Sequence = 1,
            Text = "申请日本签证需要护照。",
            Status = "approved"
        };
        var secondChunk = new KnowledgeChunkEntity
        {
            KnowledgeDocumentVersionId = version.Id,
            Sequence = 2,
            Text = "还需要签证申请表。",
            Status = "approved"
        };
        await using (var context = await factory.CreateDbContextAsync(TestContext.Current.CancellationToken))
        {
            await context.Database.MigrateAsync(TestContext.Current.CancellationToken);
            context.AddRange(
                tag,
                document,
                version,
                firstChunk,
                secondChunk,
                new KnowledgeChunkTagEntity { KnowledgeChunkId = firstChunk.Id, KnowledgeTagId = tag.Id },
                new KnowledgeChunkTagEntity { KnowledgeChunkId = secondChunk.Id, KnowledgeTagId = tag.Id },
                new ModelConfigEntity
                {
                    Name = "embedding",
                    NormalizedName = "EMBEDDING",
                    Provider = "fake",
                    ConfigurationType = "embedding",
                    BaseUrl = "https://fake.test",
                    Model = "fake",
                    EncryptedApiKey = "fake",
                    IsEnabled = true,
                    IsDefault = true,
                    EmbeddingDimension = 3
                });
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var retrievalDatabase = await factory.CreateDbContextAsync(TestContext.Current.CancellationToken);
        var modelConfigurations = new ModelConfigurationService(new PassThroughProtector());
        var knowledge = new QdrantKnowledgeService(
            retrievalDatabase,
            modelConfigurations,
            new KnowledgeIndexOptions(3, VectorDistance.Cosine),
            TimeProvider.System);
        var retrieval = new KnowledgeRetrievalEvidenceProvider(
            retrievalDatabase,
            knowledge,
            new KnowledgeTagScopeResolver(retrievalDatabase),
            new FixedEmbeddingClient(),
            new FixedVectorStore(
                new(firstChunk.Id, document.Id, version.Id, 0.95),
                new(secondChunk.Id, document.Id, version.Id, 0.90)));

        var scope = await retrieval.ResolveScopeAsync([tag.Id], TestContext.Current.CancellationToken);
        var evidence = await retrieval.RetrieveAsync("需要什么材料？", scope, 5, TestContext.Current.CancellationToken);

        Assert.Equal(2, evidence.Count);
        Assert.Contains(evidence, item => item.Text == "申请日本签证需要护照。");
        Assert.Contains(evidence, item => item.Text == "还需要签证申请表。");
    }

    [Fact]
    public async Task Physical_cleanup_detaches_versions_without_mysql57_guid_collection_parameters()
    {
        var connection = new MySqlConnectionStringBuilder(database.GetConnectionString())
        {
            SslMode = MySqlSslMode.Disabled,
            Pooling = false
        };
        var factory = new TestDbContextFactory(connection.ConnectionString);
        var document = new KnowledgeDocumentEntity
        {
            Title = "待删除文档",
            Status = "disabled",
            IsDeleteRequested = true
        };
        var firstVersion = Version(document.Id, 1, "first.txt");
        var secondVersion = Version(document.Id, 2, "second.txt");
        await using (var context = await factory.CreateDbContextAsync(TestContext.Current.CancellationToken))
        {
            await context.Database.MigrateAsync(TestContext.Current.CancellationToken);
            context.AddRange(document, firstVersion, secondVersion);
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var leased = new LeasedDurableJob(
            Guid.NewGuid(),
            "CleanupKnowledgeDocument",
            JsonSerializer.Serialize(new { documentId = document.Id }),
            0,
            "cleanup-owner");
        var jobs = new FakeJobs(leased);
        var services = new ServiceCollection()
            .AddDbContext<WechatRobotDbContext>(options => options.UseMySQL(connection.ConnectionString))
            .AddSingleton<IDurableJobRepository>(jobs)
            .AddSingleton<IObjectStorage, EmptyStorage>()
            .AddSingleton<IVectorStore>(new FixedVectorStore())
            .AddSingleton(new KnowledgeIndexOptions(3, VectorDistance.Cosine))
            .AddSingleton(TimeProvider.System)
            .AddSingleton<ISecretProtector, PassThroughProtector>()
            .AddScoped<ModelConfigurationService>()
            .AddScoped<QdrantKnowledgeService>()
            .BuildServiceProvider();
        await using var provider = services;
        var worker = new KnowledgeDocumentCleanupWorker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            TimeProvider.System);

        Assert.True(await worker.ProcessOnceAsync(TestContext.Current.CancellationToken));
        Assert.True(jobs.Completed);
        Assert.False(jobs.Failed);
    }

    [Fact]
    public async Task Ocr_failure_releases_multiple_page_claims_on_mysql57()
    {
        var connection = new MySqlConnectionStringBuilder(database.GetConnectionString())
        {
            SslMode = MySqlSslMode.Disabled,
            Pooling = false
        };
        var factory = new TestDbContextFactory(connection.ConnectionString);
        var document = new KnowledgeDocumentEntity { Title = "OCR", Status = "parsing" };
        var version = Version(document.Id, 1, "ocr.pdf");
        version.Status = "parsing";
        await using var context = await factory.CreateDbContextAsync(TestContext.Current.CancellationToken);
        await context.Database.MigrateAsync(TestContext.Current.CancellationToken);
        context.AddRange(document, version);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        var service = new ScannedPdfOcrService(
            context,
            new FailingTwoPageRenderer(),
            new UnusedOcrClient(),
            new OcrProcessingOptions { MaximumPages = 2 });
        using var processing = new DocumentProcessingContext(
            new DocumentParsingLimits(1024, 2, 4096, TimeSpan.FromSeconds(5)),
            TestContext.Current.CancellationToken);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.RecognizeAsync(version.Id, new MemoryStream([1]), processing));

        Assert.Equal("renderer failed", exception.Message);
        var rows = await context.KnowledgeOcrPages.AsNoTracking()
            .OrderBy(row => row.PageNumber)
            .ToArrayAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, rows.Length);
        Assert.All(rows, row => Assert.Equal("failed", row.Status));
        Assert.All(rows, row => Assert.Null(row.LeaseOwner));
    }

    private static KnowledgeDocumentVersionEntity Version(Guid documentId, int version, string name) => new()
    {
        KnowledgeDocumentId = documentId,
        Version = version,
        OriginalFileName = name,
        SafeFileName = name,
        ContentType = "text/plain",
        Sha256 = new string((char)('a' + version), 64),
        ObjectKey = name,
        Status = "disabled"
    };

    private sealed class TestDbContextFactory(string connectionString) : IDbContextFactory<WechatRobotDbContext>
    {
        public WechatRobotDbContext CreateDbContext() => new(
            new DbContextOptionsBuilder<WechatRobotDbContext>()
                .UseMySQL(connectionString)
                .Options);

        public Task<WechatRobotDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }

    private sealed class PassThroughProtector : ISecretProtector
    {
        public string Protect(string plaintext) => plaintext;
        public string Unprotect(string protectedValue) => protectedValue;
    }

    private sealed class FixedEmbeddingClient : IEmbeddingClient
    {
        public Task<EmbeddingBatchResponse> CreateEmbeddingsAsync(
            ModelProviderConfiguration configuration,
            EmbeddingBatchRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new EmbeddingBatchResponse([[1, 0, 0]]));
    }

    private sealed class FixedVectorStore(params VectorSearchHit[] hits) : IVectorStore
    {
        public Task<IReadOnlyList<VectorSearchHit>> SearchAsync(VectorSearchRequest request, CancellationToken token) =>
            Task.FromResult<IReadOnlyList<VectorSearchHit>>(hits);

        public Task EnsureCollectionAsync(VectorCollection collection, CancellationToken token) => Task.CompletedTask;
        public Task UpsertAsync(VectorCollection collection, IReadOnlyList<VectorPoint> points, CancellationToken token) => Task.CompletedTask;
        public Task SetVersionActiveAsync(VectorCollection collection, Guid versionId, bool active, CancellationToken token) => Task.CompletedTask;
        public Task DeleteCollectionAsync(VectorCollection collection, CancellationToken token) => Task.CompletedTask;
        public Task<VectorCollection?> InspectCollectionAsync(string collectionName, CancellationToken token) =>
            Task.FromResult<VectorCollection?>(null);
        public Task DeleteVersionAsync(VectorCollection collection, Guid versionId, CancellationToken token) => Task.CompletedTask;
        public Task<IReadOnlyList<VectorPointMetadata>> InspectVersionAsync(VectorCollection collection, Guid versionId, CancellationToken token) =>
            Task.FromResult<IReadOnlyList<VectorPointMetadata>>([]);
    }

    private sealed class EmptyStorage : IObjectStorage
    {
        public Task DeleteAsync(string objectKey, CancellationToken token) => Task.CompletedTask;
        public Task<StoredObject> PutAsync(string objectKey, Stream content, string contentType, CancellationToken token) =>
            throw new NotSupportedException();
    }

    private sealed class FailingTwoPageRenderer : IPdfPageRenderer
    {
        public Task<int> GetPageCountAsync(Stream pdf, DocumentProcessingContext context) =>
            Task.FromResult(2);

        public Task<IReadOnlyList<OcrRenderedPage>> RenderAsync(
            Stream pdf,
            IReadOnlyList<int> pageNumbers,
            DocumentProcessingContext context) =>
            throw new InvalidOperationException("renderer failed");
    }

    private sealed class UnusedOcrClient : IOcrClient
    {
        public Task<IReadOnlyList<OcrPageResult>> RecognizeAsync(
            IReadOnlyList<OcrRenderedPage> pages,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class FakeJobs(LeasedDurableJob job) : IDurableJobRepository
    {
        private bool leased;
        public bool Completed { get; private set; }
        public bool Failed { get; private set; }

        public Task<LeasedDurableJob?> LeaseNextJobAsync(string type, string owner, DateTime now, TimeSpan duration, CancellationToken token)
        {
            if (leased) return Task.FromResult<LeasedDurableJob?>(null);
            leased = true;
            return Task.FromResult<LeasedDurableJob?>(job);
        }

        public Task CompleteJobAsync(Guid id, string owner, DateTime at, CancellationToken token)
        {
            Completed = true;
            return Task.CompletedTask;
        }

        public Task FailJobAsync(LeasedDurableJob value, string reason, DateTime at, CancellationToken token)
        {
            Failed = true;
            return Task.CompletedTask;
        }

        public Task<InboundMessageIngestResult> IngestInboundMessageAsync(InboundMessageIngestRequest request, CancellationToken token) =>
            throw new NotSupportedException();
        public Task<EnqueueSendCommandResult> EnqueueSendCommandAsync(EnqueueSendCommandRequest request, CancellationToken token) =>
            throw new NotSupportedException();
        public Task<LeasedSendCommand?> LeaseNextSendCommandAsync(string owner, DateTime now, TimeSpan duration, CancellationToken token) =>
            throw new NotSupportedException();
        public Task<bool> MarkSendDispatchingAsync(LeasedSendCommand command, DateTime dispatchedAtUtc, CancellationToken token) =>
            throw new NotSupportedException();
        public Task MarkSendDeliveryUnknownAsync(LeasedSendCommand command, string reason, DateTime failedAtUtc, CancellationToken token) =>
            throw new NotSupportedException();
        public Task MarkSendAcceptedAsync(LeasedSendCommand command, string workToolMessageId, DateTime at, CancellationToken token) =>
            throw new NotSupportedException();
        public Task MarkSendRejectedAsync(LeasedSendCommand command, string reason, DateTime at, CancellationToken token) =>
            throw new NotSupportedException();
        public Task FailSendCommandAsync(LeasedSendCommand command, string reason, DateTime at, TimeSpan? delay, CancellationToken token) =>
            throw new NotSupportedException();
        public Task<bool> RenewSendLeasesAsync(LeasedSendCommand command, DateTime now, TimeSpan duration, CancellationToken token) =>
            throw new NotSupportedException();
    }
}
