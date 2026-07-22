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
using WechatRobot.Infrastructure.Storage;
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
    .Validate(options => options.MaximumSourceBytes > 0 && options.MaximumPages > 0 && options.MaximumMemoryBytes >= options.MaximumSourceBytes && options.ExecutionTimeoutSeconds > 0, "Document parsing limits are invalid.")
    .ValidateOnStart();
builder.Services.AddHttpClient<IDocumentSourceReader, HttpDocumentSourceReader>();
builder.Services.AddSingleton<MarkdownTextParser>();
builder.Services.AddSingleton<DocxParser>();
builder.Services.AddSingleton<PdfTextParser>();
builder.Services.AddSingleton<DocumentParserSelector>(services => new DocumentParserSelector([
    services.GetRequiredService<MarkdownTextParser>(), services.GetRequiredService<DocxParser>(), services.GetRequiredService<PdfTextParser>()]));
builder.Services.AddSingleton<ChunkingService>();
builder.Services.AddScoped<ChunkPreviewRepository>();
builder.Services.AddScoped(services => new KnowledgePreviewService(services.GetRequiredService<WechatRobotDbContext>(), services.GetRequiredService<IDocumentSourceReader>(),
    services.GetRequiredService<DocumentParserSelector>(), services.GetRequiredService<ChunkingService>(), services.GetRequiredService<ChunkPreviewRepository>(),
    services.GetRequiredService<Microsoft.Extensions.Options.IOptions<DocumentParsingOptions>>().Value));
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

var host = builder.Build();
host.Run();
