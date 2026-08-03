using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using WechatRobot.Application.Knowledge;
using WechatRobot.Infrastructure.Configuration;
using WechatRobot.Infrastructure.Knowledge;
using WechatRobot.Infrastructure.Persistence;

namespace WechatRobot.KnowledgeVectorMigration;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            var localDirectory = Path.GetFullPath(Option(args, "--local-dir") ?? Environment.CurrentDirectory);
            var envFile = Path.Combine(localDirectory, ".env");
            Environment.SetEnvironmentVariable(DotEnvFileLoader.EnvironmentFileVariable, envFile);
            DotEnvFileLoader.Load();
            var configuration = new ConfigurationBuilder()
                .SetBasePath(localDirectory)
                .AddJsonFile("appsettings.json", optional: false)
                .AddEnvironmentVariables()
                .Build();
            var connectionString = configuration.GetConnectionString("WechatRobot")
                ?? throw new InvalidOperationException("ConnectionStrings:WechatRobot must be configured.");
            var command = Command(args);
            var checkpointPath = Path.GetFullPath(CheckpointOption(args, command)
                ?? Option(args, "--checkpoint")
                ?? Path.Combine(localDirectory, "knowledge-vector-migration", "checkpoint.json"));

            var options = new DbContextOptionsBuilder<WechatRobotDbContext>()
                .UseMySQL(connectionString)
                .Options;
            await using var database = new WechatRobotDbContext(options);
            using var http = new HttpClient
            {
                BaseAddress = new Uri(configuration["Qdrant:BaseUrl"] ?? "http://127.0.0.1:6333/"),
                Timeout = TimeSpan.FromMinutes(2)
            };
            if (configuration["Qdrant:ApiKey"] is { Length: > 0 } apiKey)
                http.DefaultRequestHeaders.Add("api-key", apiKey);
            IVectorStore vectors = new QdrantVectorStore(http);
            var runner = new KnowledgeVectorMigrationRunner(
                database,
                vectors,
                new KnowledgeVectorMigrationPlanner(),
                checkpointPath);

            MigrationSummary summary;
            if (command == "dry-run")
            {
                summary = await runner.DryRunAsync(CancellationToken.None);
            }
            else
            {
                var checkpoint = File.Exists(checkpointPath)
                    ? await MigrationCheckpointStore.LoadAsync(checkpointPath, CancellationToken.None)
                    : command == "apply"
                        ? await CreateCheckpointAsync(runner, checkpointPath)
                        : throw new InvalidOperationException("The requested checkpoint does not exist.");
                summary = command switch
                {
                    "apply" or "resume" => await runner.ApplyAsync(checkpoint, CancellationToken.None),
                    "verify" => await runner.VerifyAsync(checkpoint, CancellationToken.None),
                    "rollback" => await runner.RollbackAsync(checkpoint, CancellationToken.None),
                    _ => throw new InvalidOperationException("Unsupported migration command.")
                };
            }
            Console.WriteLine($"state={summary.State} versions={summary.VersionCount} sources={summary.SourceCollectionCount} destinations={summary.DestinationCollectionCount} points={summary.PointCount} mismatches={summary.MismatchCount}");
            Console.WriteLine($"checkpoint={checkpointPath}");
            return summary.MismatchCount == 0 ? 0 : 2;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"knowledge_vector_migration_failed:{exception.Message}");
            return 1;
        }
    }

    private static async Task<MigrationCheckpoint> CreateCheckpointAsync(
        KnowledgeVectorMigrationRunner runner,
        string checkpointPath)
    {
        var summary = await runner.DryRunAsync(CancellationToken.None);
        if (summary.MismatchCount != 0)
            throw new InvalidOperationException("The generated dry run contains mismatches.");
        return await MigrationCheckpointStore.LoadAsync(checkpointPath, CancellationToken.None);
    }

    private static string Command(string[] args)
    {
        var commands = new[] { "dry-run", "apply", "resume", "verify", "rollback" }
            .Where(command => args.Contains("--" + command, StringComparer.OrdinalIgnoreCase))
            .ToArray();
        return commands.Length == 1
            ? commands[0]
            : throw new InvalidOperationException("Specify exactly one of --dry-run, --apply, --resume, --verify, or --rollback.");
    }

    private static string? Option(string[] args, string name)
    {
        var index = Array.FindIndex(args, value => string.Equals(value, name, StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static string? CheckpointOption(string[] args, string command) =>
        command is "resume" or "verify" or "rollback"
            ? Option(args, "--" + command)
            : null;
}
