using System.Diagnostics;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using WechatRobot.Application.Security;
using WechatRobot.Application.WorkTool;
using WechatRobot.Infrastructure.Configuration;
using WechatRobot.Infrastructure.Persistence;
using WechatRobot.Infrastructure.Security;
using WechatRobot.Infrastructure.WorkTool;

return await RunAsync(args);

static async Task<int> RunAsync(string[] args)
{
    DotEnvFileLoader.Load();
    if (!string.Equals(
            Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT"),
            Environments.Development,
            StringComparison.Ordinal))
    {
        Console.Error.WriteLine(
            "Evidence collection is restricted to DOTNET_ENVIRONMENT=Development.");
        return 2;
    }

    if (!TryReadArguments(args, out var robotConfigId, out var groupName,
            out var outputDirectory))
    {
        Console.Error.WriteLine(
            "Required: --robot-config-id <guid> --group-name <name> --output-directory <path>");
        return 2;
    }

    var repositoryRoot = FindRepositoryRoot(Environment.CurrentDirectory);
    if (repositoryRoot is null)
    {
        Console.Error.WriteLine("Repository root was not found.");
        return 2;
    }
    var localRoot = Path.GetFullPath(Path.Combine(repositoryRoot, ".local"))
        .TrimEnd(Path.DirectorySeparatorChar)
        + Path.DirectorySeparatorChar;
    var output = Path.GetFullPath(outputDirectory!, repositoryRoot);
    if (!output.StartsWith(
            localRoot,
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal))
    {
        Console.Error.WriteLine(
            "Output directory must be located under the repository .local directory.");
        return 2;
    }

    var builder = Host.CreateApplicationBuilder([]);
    var connectionString = builder.Configuration
        .GetConnectionString("WechatRobot");
    if (string.IsNullOrWhiteSpace(connectionString))
    {
        Console.Error.WriteLine(
            "ConnectionStrings:WechatRobot is required.");
        return 2;
    }
    builder.Services.AddDbContextFactory<WechatRobotDbContext>(
        options => options.UseMySQL(connectionString));
    builder.Services.AddSingleton<ISecretProtector, AesGcmSecretProtector>();
    builder.Services.AddScoped<IWorkToolCredentialResolver,
        WorkToolCredentialResolver>();
    builder.Services.AddOptions<WorkToolRateLimitOptions>()
        .BindConfiguration(WorkToolRateLimitOptions.SectionName)
        .ValidateOnStart();
    builder.Services.AddSingleton<IWorkToolGlobalRateLimiter,
        MySqlWorkToolGlobalRateLimiter>();
    builder.Services.AddTransient<WorkToolGlobalRateLimitHandler>();
    builder.Services.AddHttpClient<IWorkToolClient, WorkToolClient>(client =>
        {
            client.BaseAddress = new Uri(
                builder.Configuration["WorkTool:BaseUrl"]
                ?? "https://api.worktool.ymdyes.cn/");
        })
        .AddHttpMessageHandler<WorkToolGlobalRateLimitHandler>()
        .ConfigurePrimaryHttpMessageHandler(
            WorkToolHttpTransport.CreatePrimaryHandler);

    using var host = builder.Build();
    await using var scope = host.Services.CreateAsyncScope();
    var client = scope.ServiceProvider.GetRequiredService<IWorkToolClient>();
    var submittedAt = DateTimeOffset.UtcNow;
    var submission = await client.RequestGroupMemberSnapshotAsync(
        robotConfigId,
        groupName!,
        CancellationToken.None);
    if (!submission.Accepted || string.IsNullOrWhiteSpace(submission.MessageId))
    {
        Console.Error.WriteLine("WorkTool did not accept the evidence request.");
        return 3;
    }

    WorkToolRawCommandResult? matched = null;
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
    try
    {
        while (!timeout.IsCancellationRequested)
        {
            var results = await client.ListGroupMemberSnapshotResultsAsync(
                robotConfigId,
                submission.MessageId,
                submittedAt.AddMinutes(-5),
                DateTimeOffset.UtcNow.AddMinutes(5),
                timeout.Token);
            matched = results.FirstOrDefault(result =>
                result.Type == 512
                && string.Equals(
                    result.MessageId,
                    submission.MessageId,
                    StringComparison.Ordinal));
            if (matched is not null)
                break;
            await Task.Delay(TimeSpan.FromSeconds(5), timeout.Token);
        }
    }
    catch (OperationCanceledException) when (timeout.IsCancellationRequested)
    {
        // Report the bounded timeout below without echoing any WorkTool value.
    }
    if (matched is null)
    {
        Console.Error.WriteLine(
            "No matching type=512 result was returned before timeout.");
        return 4;
    }

    Directory.CreateDirectory(output);
    var rawPath = Path.Combine(output, "raw.json");
    var shapePath = Path.Combine(output, "shape.json");
    try
    {
        await WriteNewAsync(
            rawPath,
            JsonSerializer.Serialize(matched,
                new JsonSerializerOptions { WriteIndented = true }));
        var shape = Type512EvidenceSanitizer.Create(
            [matched],
            submission.MessageId);
        await WriteNewAsync(
            shapePath,
            JsonSerializer.Serialize(shape,
                new JsonSerializerOptions { WriteIndented = true }));
        if (!RestrictToCurrentWindowsUser(rawPath)
            || !RestrictToCurrentWindowsUser(shapePath))
        {
            throw new InvalidOperationException(
                "Evidence ACL restriction failed.");
        }
    }
    catch
    {
        DeleteIfExists(rawPath);
        DeleteIfExists(shapePath);
        throw;
    }

    Console.WriteLine($"Evidence completed: {rawPath}");
    Console.WriteLine($"Sanitized shape: {shapePath}");
    return 0;
}

static bool TryReadArguments(
    string[] args,
    out Guid robotConfigId,
    out string? groupName,
    out string? outputDirectory)
{
    robotConfigId = Guid.Empty;
    groupName = null;
    outputDirectory = null;
    var values = new Dictionary<string, string>(StringComparer.Ordinal);
    for (var index = 0; index + 1 < args.Length; index += 2)
    {
        if (!args[index].StartsWith("--", StringComparison.Ordinal))
            return false;
        values[args[index]] = args[index + 1];
    }
    return args.Length % 2 == 0
           && values.TryGetValue("--robot-config-id", out var robot)
           && Guid.TryParse(robot, out robotConfigId)
           && values.TryGetValue("--group-name", out groupName)
           && !string.IsNullOrWhiteSpace(groupName)
           && values.TryGetValue("--output-directory", out outputDirectory)
           && !string.IsNullOrWhiteSpace(outputDirectory);
}

static string? FindRepositoryRoot(string start)
{
    for (var directory = new DirectoryInfo(Path.GetFullPath(start));
         directory is not null;
         directory = directory.Parent)
    {
        if (File.Exists(Path.Combine(directory.FullName, "WechatRobot.slnx")))
            return directory.FullName;
    }
    return null;
}

static async Task WriteNewAsync(string path, string content)
{
    await using var stream = new FileStream(
        path,
        FileMode.CreateNew,
        FileAccess.Write,
        FileShare.None);
    await using var writer = new StreamWriter(stream);
    await writer.WriteAsync(content);
}

static bool RestrictToCurrentWindowsUser(string path)
{
    if (!OperatingSystem.IsWindows())
        return false;
    var identity = WindowsIdentity.GetCurrent().Name;
    using var process = Process.Start(new ProcessStartInfo
    {
        FileName = "icacls.exe",
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        ArgumentList =
        {
            path,
            "/inheritance:r",
            "/grant:r",
            $"{identity}:(R,W)"
        }
    });
    process?.WaitForExit();
    return process?.ExitCode == 0;
}

static void DeleteIfExists(string path)
{
    if (File.Exists(path))
        File.Delete(path);
}
