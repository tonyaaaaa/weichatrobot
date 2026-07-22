using Microsoft.EntityFrameworkCore;
using WechatRobot.Application.Jobs;
using WechatRobot.Application.Messaging;
using WechatRobot.Application.WorkTool;
using WechatRobot.Application.Knowledge;
using WechatRobot.Application.Storage;
using WechatRobot.Infrastructure.Persistence;
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

var host = builder.Build();
host.Run();
