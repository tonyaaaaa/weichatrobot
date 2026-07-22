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
using WechatRobot.Application.Security;
using WechatRobot.Infrastructure.Models;
using WechatRobot.Infrastructure.Security;
using WechatRobot.Infrastructure.WorkTool;
using WechatRobot.Worker.Jobs;

var builder = Host.CreateApplicationBuilder(args);
var connectionString = builder.Configuration.GetConnectionString("WechatRobot")
    ?? throw new InvalidOperationException("ConnectionStrings:WechatRobot must be configured.");
builder.Services.AddDbContext<WechatRobotDbContext>(options => options.UseMySQL(connectionString));
builder.Services.AddScoped<IDurableJobRepository, DurableJobRepository>();
builder.Services.AddScoped<SendCommandService>();
builder.Services.AddOptions<DocumentUploadOptions>().BindConfiguration(DocumentUploadOptions.SectionName)
    .Validate(options => options.MaximumBytes is > 0 and <= int.MaxValue && options.MaximumArchiveEntries > 0 &&
        options.MaximumExpandedArchiveBytes > 0 && options.MaximumArchiveExpansionRatio > 0, "Document upload limits are invalid.")
    .ValidateOnStart();
builder.Services.AddOptions<OssOptions>().BindConfiguration(OssOptions.SectionName);
builder.Services.AddSingleton<IOssTransport, AliyunOssTransport>();
builder.Services.AddSingleton<IObjectStorage, AliyunOssStorage>();
builder.Services.AddScoped<IKnowledgeDocumentStore, KnowledgeDocumentStore>();
builder.Services.AddOptions<DocumentParsingOptions>().BindConfiguration(DocumentParsingOptions.SectionName)
    .Validate(options => options.MaximumSourceBytes > 0 && options.MaximumPages > 0 && options.MaximumMemoryBytes > 0 && options.ExecutionTimeoutSeconds > 0 &&
        options.MaximumPageCharacters > 0 && options.MaximumExpandedEntryBytes > 0 && options.MaximumResultCharacters > 0, "Document parsing limits are invalid.")
    .ValidateOnStart();
builder.Services.AddHttpClient<IDocumentSourceReader, HttpDocumentSourceReader>();
builder.Services.AddSingleton<MarkdownTextParser>();
builder.Services.AddSingleton<DocxParser>();
builder.Services.AddSingleton<PdfTextParser>();
builder.Services.AddSingleton<DocumentParserSelector>(services => new DocumentParserSelector([
    services.GetRequiredService<MarkdownTextParser>(), services.GetRequiredService<DocxParser>(), services.GetRequiredService<PdfTextParser>()]));
builder.Services.AddSingleton<ChunkingService>();
builder.Services.AddScoped<ChunkPreviewRepository>();
var knowledgeIndexOptions = builder.Configuration.GetSection(KnowledgeIndexOptions.SectionName).Get<KnowledgeIndexOptions>()
    ?? new KnowledgeIndexOptions(1536, VectorDistance.Cosine);
if (knowledgeIndexOptions.Dimension <= 0 || knowledgeIndexOptions.BatchSize <= 0 || knowledgeIndexOptions.MaximumAttempts <= 0)
    throw new InvalidOperationException("Knowledge index configuration is invalid.");
builder.Services.AddSingleton(knowledgeIndexOptions);
builder.Services.AddSingleton<ISecretProtector, AesGcmSecretProtector>();
builder.Services.AddScoped<ModelConfigurationService>();
builder.Services.AddScoped<QdrantKnowledgeService>();
builder.Services.AddScoped<IKnowledgeService>(services => services.GetRequiredService<QdrantKnowledgeService>());
builder.Services.AddScoped<KnowledgeIndexService>();
builder.Services.AddHttpClient<IEmbeddingClient, OpenAiCompatibleEmbeddingClient>();
builder.Services.AddHttpClient<IVectorStore, QdrantVectorStore>(client =>
{
    client.BaseAddress = new Uri(builder.Configuration["Qdrant:BaseUrl"] ?? "http://127.0.0.1:6333/");
    var apiKey = builder.Configuration["Qdrant:ApiKey"];
    if (!string.IsNullOrWhiteSpace(apiKey)) client.DefaultRequestHeaders.Add("api-key", apiKey);
});
var ocrClientOptions = builder.Configuration.GetSection(OcrClientOptions.SectionName).Get<OcrClientOptions>() ?? new OcrClientOptions();
if (ocrClientOptions.Timeout <= TimeSpan.Zero || ocrClientOptions.MaximumResponseBytes <= 0 || !OcrEndpointPolicy.IsAllowed(ocrClientOptions.BaseAddress))
    throw new InvalidOperationException("OCR client configuration must use a private Compose name or localhost and positive limits.");
var ocrProcessingOptions = builder.Configuration.GetSection(OcrClientOptions.SectionName).Get<OcrProcessingOptions>() ?? new OcrProcessingOptions();
if (ocrProcessingOptions.MinimumExtractedTextCharacters < 0 || ocrProcessingOptions.MaximumPages <= 0 || ocrProcessingOptions.MaximumImagePixels <= 0 ||
    ocrProcessingOptions.MaximumRenderedBytes <= 0 || ocrProcessingOptions.RenderTimeoutSeconds <= 0 || ocrProcessingOptions.PageLeaseSeconds <= 0 ||
    string.IsNullOrWhiteSpace(ocrProcessingOptions.RendererExecutablePath))
    throw new InvalidOperationException("OCR processing limits are invalid.");
builder.Services.AddSingleton(ocrClientOptions);
builder.Services.AddSingleton(ocrProcessingOptions);
builder.Services.AddHttpClient<IOcrClient, HttpOcrClient>(client => client.BaseAddress = ocrClientOptions.BaseAddress);
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
builder.Services.AddOptions<FixedReplyOptions>()
    .BindConfiguration(FixedReplyOptions.SectionName)
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddScoped<InboundMessageProcessor>(services => new InboundMessageProcessor(
    services.GetRequiredService<SendCommandService>(),
    services.GetRequiredService<Microsoft.Extensions.Options.IOptions<FixedReplyOptions>>().Value));
builder.Services.AddHttpClient<IWorkToolClient, WorkToolClient>(client =>
{
    client.BaseAddress = new Uri(builder.Configuration["WorkTool:BaseUrl"] ?? "https://api.worktool.ymdyes.cn/");
});
builder.Services.AddHostedService<DurableJobWorker>();
builder.Services.AddHostedService<RobotSendWorker>();
builder.Services.AddHostedService<KnowledgeUploadWorker>();
builder.Services.AddHostedService<KnowledgeParseWorker>();
builder.Services.AddHostedService<KnowledgeIndexWorker>();

var host = builder.Build();
host.Run();
