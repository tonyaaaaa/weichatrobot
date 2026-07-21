using Microsoft.EntityFrameworkCore;
using WechatRobot.Application.Jobs;
using WechatRobot.Application.Messaging;
using WechatRobot.Application.WorkTool;
using WechatRobot.Infrastructure.Persistence;
using WechatRobot.Infrastructure.WorkTool;
using WechatRobot.Worker.Jobs;

var builder = Host.CreateApplicationBuilder(args);
var connectionString = builder.Configuration.GetConnectionString("WechatRobot")
    ?? throw new InvalidOperationException("ConnectionStrings:WechatRobot must be configured.");
builder.Services.AddDbContext<WechatRobotDbContext>(options => options.UseMySQL(connectionString));
builder.Services.AddScoped<IDurableJobRepository, DurableJobRepository>();
builder.Services.AddScoped<SendCommandService>();
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

var host = builder.Build();
host.Run();
