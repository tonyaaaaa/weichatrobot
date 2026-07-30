using System.Text;
using System.Text.RegularExpressions;

namespace WechatRobot.Infrastructure.Configuration;

public static partial class DotEnvFileLoader
{
    public const string EnvironmentFileVariable = "WECHATROBOT_ENV_FILE";
    public const string DefaultPath = @"C:\wxrobot\config\.env";

    public static string? Load(string? defaultPath = null)
    {
        var configuredPath = Environment.GetEnvironmentVariable(EnvironmentFileVariable);
        var isExplicit = !string.IsNullOrWhiteSpace(configuredPath);
        var selectedPath = isExplicit ? configuredPath! : defaultPath ?? DefaultPath;

        if (!Path.IsPathFullyQualified(selectedPath))
            throw new InvalidOperationException(
                $"{(isExplicit ? EnvironmentFileVariable : "The .env path")} must be an absolute file path.");

        var fullPath = Path.GetFullPath(selectedPath);
        if (!File.Exists(fullPath))
        {
            if (isExplicit)
                throw new InvalidOperationException(
                    $"{EnvironmentFileVariable} points to a file that does not exist: {fullPath}");
            return null;
        }

        var values = Parse(fullPath);
        foreach (var pair in values)
        {
            if (Environment.GetEnvironmentVariable(pair.Key) is null)
                Environment.SetEnvironmentVariable(pair.Key, pair.Value);
        }
        return fullPath;
    }

    private static IReadOnlyDictionary<string, string> Parse(string path)
    {
        var comparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        var values = new Dictionary<string, string>(comparer);
        var lineNumber = 0;

        try
        {
            using var reader = new StreamReader(path, new UTF8Encoding(
                encoderShouldEmitUTF8Identifier: false,
                throwOnInvalidBytes: true));
            while (reader.ReadLine() is { } sourceLine)
            {
                lineNumber++;
                var line = sourceLine.Trim();
                if (line.Length == 0 || line.StartsWith('#'))
                    continue;

                if (line.StartsWith("export ", StringComparison.Ordinal))
                    line = line["export ".Length..].TrimStart();

                var separator = line.IndexOf('=');
                if (separator <= 0)
                    throw InvalidLine(path, lineNumber, "expected NAME=VALUE");

                var name = line[..separator].Trim();
                if (!EnvironmentName().IsMatch(name))
                    throw InvalidLine(path, lineNumber, "invalid variable name");
                if (!values.TryAdd(name, ParseValue(path, lineNumber, line[(separator + 1)..].Trim())))
                    throw InvalidLine(path, lineNumber, $"duplicate variable name {name}");
            }
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidOperationException(
                $"Invalid .env file {path}: the file must be encoded as UTF-8.",
                exception);
        }

        return values;
    }

    private static string ParseValue(string path, int lineNumber, string value)
    {
        if (value.Length == 0)
            return string.Empty;

        var startsQuoted = value[0] is '\'' or '"';
        var endsQuoted = value[^1] is '\'' or '"';
        if (!startsQuoted && !endsQuoted)
            return value;
        if (!startsQuoted || !endsQuoted || value[0] != value[^1] || value.Length < 2)
            throw InvalidLine(path, lineNumber, "unmatched quote");

        return value[1..^1];
    }

    private static InvalidOperationException InvalidLine(string path, int lineNumber, string reason) =>
        new($"Invalid .env file {path} at line {lineNumber}: {reason}.");

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant)]
    private static partial Regex EnvironmentName();
}
