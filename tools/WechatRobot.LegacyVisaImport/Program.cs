using WechatRobot.Infrastructure.Configuration;
using System.Text;

namespace WechatRobot.LegacyVisaImport;

internal static class Program
{
    private const string RequiredTag = "签证知识";

    public static async Task<int> Main(string[] args)
    {
        try
        {
            Console.OutputEncoding = new UTF8Encoding(false);
            var apply = args.Contains("--apply", StringComparer.OrdinalIgnoreCase);
            var localDirectory = Path.GetFullPath(Option(args, "--local-dir") ?? Environment.CurrentDirectory);
            var envFile = Path.Combine(localDirectory, ".env");
            Environment.SetEnvironmentVariable(DotEnvFileLoader.EnvironmentFileVariable, envFile);
            DotEnvFileLoader.Load();

            var source = RequiredEnvironment("LEGACY_VISA_CONNECTION_STRING");
            var email = RequiredEnvironment("BootstrapAdmin__Email");
            var password = RequiredEnvironment("BootstrapAdmin__Password");
            var baseUrl = Option(args, "--base-url") ?? "http://127.0.0.1:5268";
            var stateDirectory = Path.Combine(localDirectory, "legacy-visa-import");
            var outputDirectory = Path.Combine(stateDirectory, "rendered");
            var checkpointPath = Path.Combine(stateDirectory, "checkpoint.json");

            Console.WriteLine(apply ? "mode=apply" : "mode=dry-run");
            Console.WriteLine($"target={new Uri(baseUrl).GetLeftPart(UriPartial.Authority)}");
            var extraction = await LegacyVisaExtractor.ExtractAsync(source, CancellationToken.None);
            Console.WriteLine($"source_products={extraction.Products.Count} source_skipped={extraction.Skipped.Count}");
            foreach (var skipped in extraction.Skipped)
            {
                Console.WriteLine(
                    $"source_skip\t{skipped.LegacyVisaId}\t{skipped.Reason}\t{skipped.Title}");
            }

            using var http = new HttpClient
            {
                BaseAddress = new Uri(baseUrl),
                Timeout = TimeSpan.FromMinutes(3)
            };
            var api = new KnowledgeApiClient(http);
            await api.LoginAsync(email, password, CancellationToken.None);
            var runner = new LegacyVisaImportRunner(
                api,
                new LegacyVisaImportOptions(
                    apply,
                    outputDirectory,
                    checkpointPath,
                    RequiredTag,
                    TimeSpan.FromMinutes(10)));
            var summary = await runner.RunAsync(extraction.Products, CancellationToken.None);
            Console.WriteLine(
                $"summary total={summary.Total} create={summary.Creates} update={summary.Updates} " +
                $"skip={summary.Skips} applied={summary.Applied}");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"legacy_visa_import_failed:{exception.Message}");
            return 1;
        }
    }

    private static string RequiredEnvironment(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException($"required_environment_missing:{name}");

    private static string? Option(string[] args, string name)
    {
        var index = Array.FindIndex(args, value =>
            string.Equals(value, name, StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 1 < args.Length
            ? args[index + 1]
            : null;
    }
}
