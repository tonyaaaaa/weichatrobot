using System.Text;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using WechatRobot.Api.Auth;
using WechatRobot.Api.Groups;
using WechatRobot.Api.Knowledge;
using WechatRobot.Api.Models;
using WechatRobot.Api.WorkTool;
using WechatRobot.Application.Jobs;
using WechatRobot.Application.Knowledge;
using WechatRobot.Application.Knowledge.Chunking;
using WechatRobot.Application.Knowledge.Parsing;
using WechatRobot.Application.Storage;
using WechatRobot.Application.Groups;
using WechatRobot.Application.Messaging;
using WechatRobot.Application.Models;
using WechatRobot.Application.Security;
using WechatRobot.Application.WorkTool;
using WechatRobot.Infrastructure.Identity;
using WechatRobot.Infrastructure.Knowledge;
using WechatRobot.Infrastructure.Knowledge.Parsing;
using WechatRobot.Infrastructure.Models;
using WechatRobot.Infrastructure.Persistence;
using WechatRobot.Infrastructure.Security;
using WechatRobot.Infrastructure.Storage;
using WechatRobot.Infrastructure.WorkTool;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("WechatRobot")
    ?? throw new InvalidOperationException("ConnectionStrings:WechatRobot must be configured.");
var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
    ?? throw new InvalidOperationException("Cors:AllowedOrigins must be configured.");
if (allowedOrigins.Length == 0 || allowedOrigins.Any(string.IsNullOrWhiteSpace))
{
    throw new InvalidOperationException("Cors:AllowedOrigins must contain at least one explicit origin.");
}

builder.Services.AddDbContext<WechatRobotDbContext>(options => options.UseMySQL(connectionString));
var secretProtector = new AesGcmSecretProtector();
builder.Services.AddSingleton<ISecretProtector>(secretProtector);
builder.Services.AddScoped<ModelConfigurationService>();
builder.Services.AddScoped<GroupConfigurationService>();
builder.Services.AddSingleton(sp => new GroupOperationConfirmationService(builder.Configuration["Jwt:SigningKey"] ?? throw new InvalidOperationException("JWT signing key must be configured.")));
builder.Services.AddScoped<IDurableJobRepository, DurableJobRepository>();
builder.Services.AddOptions<DocumentUploadOptions>()
    .BindConfiguration(DocumentUploadOptions.SectionName)
    .Validate(options => options.MaximumBytes is > 0 and <= int.MaxValue && options.MaximumArchiveEntries > 0 && options.MaximumExpandedArchiveBytes > 0 && options.MaximumArchiveExpansionRatio > 0, "Document upload limits are invalid.")
    .ValidateOnStart();
builder.Services.Configure<FormOptions>(options =>
    options.MultipartBodyLengthLimit = checked(builder.Configuration.GetValue<long?>($"{DocumentUploadOptions.SectionName}:MaximumBytes") ?? 20 * 1024 * 1024) + 64 * 1024);
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
    services.GetRequiredService<IOptions<DocumentParsingOptions>>().Value));
builder.Services.AddScoped(services => new DocumentUploadService(
    services.GetRequiredService<IOptions<DocumentUploadOptions>>().Value,
    services.GetRequiredService<IOptions<OssOptions>>().Value.PublicReadRiskAccepted,
    services.GetRequiredService<IObjectStorage>(),
    services.GetRequiredService<IKnowledgeDocumentStore>()));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddOptions<WorkToolCallbackOptions>()
    .BindConfiguration(WorkToolCallbackOptions.SectionName)
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddScoped<InboundMessageService>(services => new InboundMessageService(
    services.GetRequiredService<IDurableJobRepository>(),
    services.GetRequiredService<TimeProvider>(),
    services.GetRequiredService<IOptions<WorkToolCallbackOptions>>().Value.FallbackDeduplicationWindow));
WorkToolCallbackRateLimitPolicy.Add(builder.Services);
builder.Services.AddHttpClient<IChatCompletionClient, OpenAiCompatibleChatClient>();
builder.Services.AddHttpClient<IEmbeddingClient, OpenAiCompatibleEmbeddingClient>();
builder.Services.AddHttpClient<IWorkToolClient, WorkToolClient>(client => client.BaseAddress = new Uri(builder.Configuration["WorkTool:BaseUrl"] ?? "https://api.worktool.ymdyes.cn/"));
builder.Services
    .AddIdentity<ApplicationUser, IdentityRole<Guid>>(options =>
    {
        options.Password.RequiredLength = 12;
        options.Password.RequireDigit = true;
        options.Password.RequireLowercase = true;
        options.Password.RequireUppercase = true;
        options.Password.RequireNonAlphanumeric = true;
        options.Lockout.AllowedForNewUsers = true;
        options.Lockout.MaxFailedAccessAttempts = 5;
        options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
        options.User.RequireUniqueEmail = true;
    })
    .AddEntityFrameworkStores<WechatRobotDbContext>()
    .AddDefaultTokenProviders();

builder.Services.AddOptions<JwtOptions>()
    .BindConfiguration(JwtOptions.SectionName)
    .ValidateDataAnnotations()
    .ValidateOnStart();
var jwt = builder.Configuration.GetSection(JwtOptions.SectionName).Get<JwtOptions>()
    ?? throw new InvalidOperationException("JWT settings must be configured.");
builder.Services.AddAuthentication(options =>
    {
        options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
        options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
        options.DefaultForbidScheme = JwtBearerDefaults.AuthenticationScheme;
    })
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwt.Issuer,
            ValidateAudience = true,
            ValidAudience = jwt.Audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.SigningKey)),
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(30),
            NameClaimType = ClaimTypes.Name,
            RoleClaimType = ClaimTypes.Role
        };
    });
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy(SystemRoles.Admin, policy => policy.RequireRole(SystemRoles.Admin));
    options.AddPolicy(SystemRoles.KnowledgeOperator, policy => policy.RequireRole(SystemRoles.Admin, SystemRoles.KnowledgeOperator));
    options.AddPolicy(SystemRoles.HumanAgent, policy => policy.RequireRole(SystemRoles.Admin, SystemRoles.HumanAgent));
});
builder.Services.AddCors(options => options.AddPolicy("AdminSpa", policy => policy
    .WithOrigins(allowedOrigins)
    .AllowAnyHeader()
    .AllowAnyMethod()));

var app = builder.Build();

if (builder.Configuration.GetValue<bool>("Database:ApplyMigrationsOnStartup"))
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<WechatRobotDbContext>();
    await db.Database.MigrateAsync();
    await IdentitySeeder.SeedAsync(scope.ServiceProvider, builder.Configuration);
}

app.UseCors("AdminSpa");
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.MapAuthEndpoints();
app.MapModelConfigurationEndpoints();
app.MapGroupEndpoints();
app.MapDocumentEndpoints();
app.MapChunkPreviewEndpoints();
app.MapWorkToolCallbackEndpoints();
app.MapWorkToolGroupOperationEndpoints();
app.MapGet("/", () => Results.Ok());

app.Run();

public partial class Program;
