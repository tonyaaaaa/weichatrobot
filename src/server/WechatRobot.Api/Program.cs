using System.Text;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using WechatRobot.Api.Auth;
using WechatRobot.Api.Models;
using WechatRobot.Api.WorkTool;
using WechatRobot.Application.Jobs;
using WechatRobot.Application.Messaging;
using WechatRobot.Application.Models;
using WechatRobot.Application.Security;
using WechatRobot.Infrastructure.Identity;
using WechatRobot.Infrastructure.Models;
using WechatRobot.Infrastructure.Persistence;
using WechatRobot.Infrastructure.Security;

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
builder.Services.AddScoped<IDurableJobRepository, DurableJobRepository>();
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
app.MapWorkToolCallbackEndpoints();
app.MapGet("/", () => Results.Ok());

app.Run();

public partial class Program;
