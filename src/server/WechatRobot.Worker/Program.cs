using Microsoft.EntityFrameworkCore;
using WechatRobot.Application.Jobs;
using WechatRobot.Application.Messaging;
using WechatRobot.Application.WorkTool;
using WechatRobot.Application.Knowledge;
using WechatRobot.Application.Knowledge.Chunking;
using WechatRobot.Application.Knowledge.Parsing;
using WechatRobot.Application.Storage;
using WechatRobot.Infrastructure.Persistence;
using WechatRobot.Infrastructure.Knowledge;
using WechatRobot.Infrastructure.Knowledge.Parsing;
using WechatRobot.Application.Knowledge.Ocr;
using WechatRobot.Infrastructure.Knowledge.Ocr;
using WechatRobot.Infrastructure.Storage;
using WechatRobot.Application.Models;
using WechatRobot.Application.Agents;
using WechatRobot.Application.FixedReplies;
using WechatRobot.Application.PrivateChat;
using WechatRobot.Application.Security;
using WechatRobot.Infrastructure.Models;
using WechatRobot.Infrastructure.Agents;
using WechatRobot.Infrastructure.Security;
using WechatRobot.Infrastructure.WorkTool;
using WechatRobot.Application.Conversations;
using WechatRobot.Infrastructure.Conversations;
using WechatRobot.Infrastructure.Health;
using WechatRobot.Worker.Jobs;
using WechatRobot.Infrastructure.Logging;
using WechatRobot.Infrastructure.Configuration;
using WechatRobot.Application.Memory;
using WechatRobot.Infrastructure.Memory;
using Microsoft.Extensions.Options;

DotEnvFileLoader.Load();
var builder = Host.CreateApplicationBuilder(args);
builder.Logging.AddRedactingConsole();
StartupConfigurationValidator.Validate(builder.Configuration, requireCors: false);
var connectionString = builder.Configuration.GetConnectionString("WechatRobot")
    ?? throw new InvalidOperationException("ConnectionStrings:WechatRobot must be configured.");
builder.Services.AddDbContextFactory<WechatRobotDbContext>(
    options => options.UseMySQL(connectionString));
builder.Services.AddScoped<IDurableJobRepository, DurableJobRepository>();
builder.Services.AddScoped<SendCommandService>();
builder.Services.AddOptions<DocumentUploadOptions>().BindConfiguration(DocumentUploadOptions.SectionName)
    .Validate(options => options.MaximumBytes is > 0 and <= int.MaxValue && options.MaximumArchiveEntries > 0 &&
        options.MaximumExpandedArchiveBytes > 0 && options.MaximumArchiveExpansionRatio > 0, "Document upload limits are invalid.")
    .ValidateOnStart();
builder.Services.AddOptions<OssOptions>().BindConfiguration(OssOptions.SectionName);
if (builder.Configuration["ObjectStorage:Provider"]?.Equals("loopback", StringComparison.OrdinalIgnoreCase) == true)
{
    LoopbackHttpPolicy.EnsureDevelopmentOnly(true, builder.Environment.EnvironmentName);
    builder.Services.AddOptions<LoopbackObjectStorageOptions>().BindConfiguration(LoopbackObjectStorageOptions.SectionName).ValidateOnStart();
    builder.Services.AddHttpClient<IObjectStorage, LoopbackObjectStorage>()
        .ConfigurePrimaryHttpMessageHandler(LoopbackHttpPolicy.CreatePrimaryHandler);
}
else
{
    builder.Services.AddSingleton<IOssTransport, AliyunOssTransport>();
    builder.Services.AddSingleton<IObjectStorage, AliyunOssStorage>();
}
builder.Services.AddScoped<IKnowledgeDocumentStore, KnowledgeDocumentStore>();
builder.Services.AddOptions<DocumentParsingOptions>().BindConfiguration(DocumentParsingOptions.SectionName)
    .Validate(options => options.MaximumSourceBytes > 0 && options.MaximumPages > 0 && options.MaximumMemoryBytes > 0 && options.ExecutionTimeoutSeconds > 0 &&
        options.MaximumPageCharacters > 0 && options.MaximumExpandedEntryBytes > 0 && options.MaximumResultCharacters > 0, "Document parsing limits are invalid.")
    .ValidateOnStart();
LoopbackHttpPolicy.EnsureDevelopmentOnly(
    builder.Configuration.GetValue<bool>($"{DocumentSourceOptions.SectionName}:AllowLoopbackHttp"),
    builder.Environment.EnvironmentName);
builder.Services.AddOptions<DocumentSourceOptions>().BindConfiguration(DocumentSourceOptions.SectionName);
builder.Services.AddHttpClient<IDocumentSourceReader, HttpDocumentSourceReader>()
    .ConfigurePrimaryHttpMessageHandler(LoopbackHttpPolicy.CreatePrimaryHandler);
builder.Services.AddSingleton<MarkdownTextParser>();
builder.Services.AddSingleton<DocxParser>();
builder.Services.AddSingleton<PdfTextParser>();
builder.Services.AddSingleton<DocumentParserSelector>(services => new DocumentParserSelector([
    services.GetRequiredService<MarkdownTextParser>(), services.GetRequiredService<DocxParser>(), services.GetRequiredService<PdfTextParser>()]));
builder.Services.AddSingleton<ChunkingService>();
builder.Services.AddScoped<ChunkPreviewRepository>();
var knowledgeIndexOptions = builder.Configuration.GetSection(KnowledgeIndexOptions.SectionName).Get<KnowledgeIndexOptions>()
    ?? new KnowledgeIndexOptions(1536, VectorDistance.Cosine);
knowledgeIndexOptions.Validate();
builder.Services.AddSingleton(knowledgeIndexOptions);
builder.Services.AddSingleton(KnowledgeIndexWorkerOptions.Default);
builder.Services.AddSingleton<ISecretProtector, AesGcmSecretProtector>();
builder.Services.AddScoped<ModelConfigurationService>();
builder.Services.AddScoped<IAgentChatClientFactory, OpenAiCompatibleAgentChatClientFactory>();
builder.Services.AddScoped<IAgentModelConfigurationReader, AgentModelConfigurationReader>();
builder.Services.AddScoped<IAgentCapabilityProbe, AgentCapabilityProbe>();
builder.Services.AddOptions<AgentRuntimeOptions>()
    .BindConfiguration(AgentRuntimeOptions.SectionName)
    .Validate(options =>
    {
        try { options.Validate(); return true; }
        catch (InvalidOperationException) { return false; }
    }, "Agent runtime options are invalid.")
    .ValidateOnStart();
builder.Services.AddSingleton(services =>
    services.GetRequiredService<IOptions<AgentRuntimeOptions>>().Value);
builder.Services.AddScoped<IMessageIntentAgent, MessageIntentAgent>();
builder.Services.AddScoped<IMessageIntentAuditStore, MessageIntentAuditStore>();
builder.Services.AddScoped<IAnswerAgent, AnswerAgent>();
builder.Services.AddScoped<IFixedReplyTemplateStore, FixedReplyTemplateStore>();
builder.Services.AddScoped<FixedReplyTemplateService>();
builder.Services.AddScoped<ITemplateRoutingAgent, TemplateRoutingAgent>();
builder.Services.AddScoped<IPrivateKnowledgeIngestStore, PrivateKnowledgeIngestStore>();
builder.Services.AddScoped<IPrivateChatProcessor, PrivateChatProcessor>();
builder.Services.AddScoped<IPrivateKnowledgeProposalAgent, PrivateKnowledgeProposalAgent>();
builder.Services.AddScoped<IPrivateKnowledgeIngestProcessor, PrivateKnowledgeIngestProcessor>();
builder.Services.AddSingleton<MemoryExtractionValidator>();
builder.Services.AddScoped<IMemoryExtractor, ChatMemoryExtractor>();
builder.Services.AddScoped<IMemoryRelationshipClassifier, ChatMemoryRelationshipClassifier>();
builder.Services.AddScoped<MemoryExtractionService>();
builder.Services.AddScoped<IMemoryStore, EfMemoryStore>();
builder.Services.AddScoped<MemoryOrganizationService>();
builder.Services.AddScoped<IMemoryRecallService, MemoryRecallService>();
builder.Services.AddScoped<QdrantKnowledgeService>();
builder.Services.AddScoped<IKnowledgeService>(services => services.GetRequiredService<QdrantKnowledgeService>());
builder.Services.AddScoped<KnowledgeIndexService>();
builder.Services.AddScoped<KnowledgeCandidatePublishProcessor>();
builder.Services.AddHttpClient<IEmbeddingClient, OpenAiCompatibleEmbeddingClient>();
builder.Services.AddHttpClient<IChatCompletionClient, OpenAiCompatibleChatClient>();
builder.Services.AddHttpClient<IMemoryVectorIndex, QdrantMemoryVectorIndex>(client =>
{
    client.BaseAddress = new Uri(builder.Configuration["Qdrant:BaseUrl"] ?? "http://127.0.0.1:6333/");
    var apiKey = builder.Configuration["Qdrant:ApiKey"];
    if (!string.IsNullOrWhiteSpace(apiKey)) client.DefaultRequestHeaders.Add("api-key", apiKey);
});
builder.Services.AddHttpClient<IVectorStore, QdrantVectorStore>(client =>
{
    client.BaseAddress = new Uri(builder.Configuration["Qdrant:BaseUrl"] ?? "http://127.0.0.1:6333/");
    var apiKey = builder.Configuration["Qdrant:ApiKey"];
    if (!string.IsNullOrWhiteSpace(apiKey)) client.DefaultRequestHeaders.Add("api-key", apiKey);
});
var aliyunOcrOptions = builder.Configuration.GetSection(AliyunOcrOptions.SectionName).Get<AliyunOcrOptions>() ?? new AliyunOcrOptions();
aliyunOcrOptions.Validate();
var aliyunAccessKeyId = Environment.GetEnvironmentVariable(AliyunOcrOptions.AccessKeyIdEnvironmentVariable);
var aliyunAccessKeySecret = Environment.GetEnvironmentVariable(AliyunOcrOptions.AccessKeySecretEnvironmentVariable);
if (string.IsNullOrWhiteSpace(aliyunAccessKeyId) || string.IsNullOrWhiteSpace(aliyunAccessKeySecret))
    throw new InvalidOperationException("Alibaba Cloud OCR credentials must be configured in the dedicated environment variables.");
var ocrProcessingOptions = builder.Configuration.GetSection(AliyunOcrOptions.SectionName).Get<OcrProcessingOptions>() ?? new OcrProcessingOptions();
if (ocrProcessingOptions.MinimumExtractedTextCharacters < 0 || ocrProcessingOptions.MaximumPages <= 0 || ocrProcessingOptions.MaximumImagePixels <= 0 ||
    ocrProcessingOptions.MaximumRenderedBytes <= 0 || ocrProcessingOptions.RenderTimeoutSeconds <= 0 || ocrProcessingOptions.PageLeaseSeconds <= 0 ||
    string.IsNullOrWhiteSpace(ocrProcessingOptions.RendererExecutablePath))
    throw new InvalidOperationException("OCR processing limits are invalid.");
builder.Services.AddSingleton(aliyunOcrOptions);
builder.Services.AddSingleton(ocrProcessingOptions);
builder.Services.AddSingleton<IAliyunOcrProvider>(_ => new AlibabaSdkOcrProvider(aliyunOcrOptions, aliyunAccessKeyId, aliyunAccessKeySecret));
builder.Services.AddSingleton<IOcrClient, AliyunOcrClient>();
builder.Services.AddSingleton<IPdfPageRenderer>(_ =>
{
    if (!(OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()))
        throw new PlatformNotSupportedException("The pinned PDFium renderer supports Windows, Linux and macOS.");
    return new IsolatedPdfPageRenderer(ocrProcessingOptions);
});
builder.Services.AddScoped<ScannedPdfOcrService>();
builder.Services.AddScoped(services => new KnowledgePreviewService(services.GetRequiredService<WechatRobotDbContext>(), services.GetRequiredService<IDocumentSourceReader>(),
    services.GetRequiredService<DocumentParserSelector>(), services.GetRequiredService<ChunkingService>(), services.GetRequiredService<ChunkPreviewRepository>(),
    services.GetRequiredService<Microsoft.Extensions.Options.IOptions<DocumentParsingOptions>>().Value, services.GetRequiredService<TimeProvider>(),
    services.GetRequiredService<ScannedPdfOcrService>()));
builder.Services.AddScoped(services => new DocumentUploadService(
    services.GetRequiredService<Microsoft.Extensions.Options.IOptions<DocumentUploadOptions>>().Value,
    services.GetRequiredService<Microsoft.Extensions.Options.IOptions<OssOptions>>().Value.PublicReadRiskAccepted,
    services.GetRequiredService<IObjectStorage>(), services.GetRequiredService<IKnowledgeDocumentStore>()));
builder.Services.AddSingleton(TimeProvider.System);
var groundedAnswerOptions = builder.Configuration.GetSection(GroundedAnswerOptions.SectionName).Get<GroundedAnswerOptions>() ?? new();
groundedAnswerOptions.Validate();
builder.Services.AddSingleton(groundedAnswerOptions);
var retrievalQueryOptions = builder.Configuration.GetSection(RetrievalQueryOptions.SectionName).Get<RetrievalQueryOptions>() ?? new();
retrievalQueryOptions.Validate();
var conversationSummaryOptions = builder.Configuration.GetSection(ConversationSummaryOptions.SectionName).Get<ConversationSummaryOptions>() ?? new();
conversationSummaryOptions.Validate();
builder.Services.AddSingleton(retrievalQueryOptions);
builder.Services.AddSingleton(conversationSummaryOptions);
builder.Services.AddSingleton<ConversationContextService>();
builder.Services.AddSingleton<AnswerOutputFirewall>();
builder.Services.AddSingleton<RetrievalQueryBuilder>();
builder.Services.AddScoped<IConversationSummarizer, ChatConversationSummarizer>();
builder.Services.AddScoped<IGroundedConversationRepository, GroundedConversationRepository>();
builder.Services.AddScoped<IRetrievalEvidenceProvider, KnowledgeRetrievalEvidenceProvider>();
builder.Services.AddScoped<IKnowledgeTagScopeResolver, KnowledgeTagScopeResolver>();
builder.Services.AddScoped<GroundedAnswerService>();
builder.Services.AddScoped<InboundMessageProcessor>();
builder.Services.AddOptions<WorkToolRateLimitOptions>()
    .BindConfiguration(WorkToolRateLimitOptions.SectionName)
    .ValidateOnStart();
builder.Services.AddSingleton<IWorkToolGlobalRateLimiter, MySqlWorkToolGlobalRateLimiter>();
builder.Services.AddTransient<WorkToolGlobalRateLimitHandler>();
builder.Services.AddHttpClient<IWorkToolClient, WorkToolClient>(client =>
{
    client.BaseAddress = new Uri(builder.Configuration["WorkTool:BaseUrl"] ?? "https://api.worktool.ymdyes.cn/");
})
    .AddHttpMessageHandler<WorkToolGlobalRateLimitHandler>()
    .ConfigurePrimaryHttpMessageHandler(WorkToolHttpTransport.CreatePrimaryHandler);
builder.Services.AddScoped<IWorkToolCredentialResolver, WorkToolCredentialResolver>();
builder.Services.AddScoped<WorkToolGroupImportService>();
builder.Services.AddHostedService<DurableJobWorker>();
builder.Services.AddHostedService<MemoryExtractionWorker>();
builder.Services.AddHostedService<MemoryMaintenanceWorker>();
builder.Services.AddHostedService<MemoryIndexWorker>();
builder.Services.AddHostedService<RobotSendWorker>();
builder.Services.AddHostedService<WorkToolGroupOperationWorker>();
builder.Services.AddHostedService<WorkToolGroupReconciliationWorker>();
builder.Services.AddHostedService<KnowledgeUploadWorker>();
builder.Services.AddHostedService<KnowledgeParseWorker>();
builder.Services.AddHostedService<KnowledgeIndexWorker>();
builder.Services.AddHostedService<KnowledgeCandidatePublishWorker>();
builder.Services.AddHostedService<KnowledgeDocumentCleanupWorker>();
builder.Services.AddHostedService<WorkerHeartbeatService>();

var host = builder.Build();
host.Run();
