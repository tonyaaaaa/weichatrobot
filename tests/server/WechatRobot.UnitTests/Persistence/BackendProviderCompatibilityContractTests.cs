using System.Text.RegularExpressions;

namespace WechatRobot.UnitTests.Persistence;

public sealed partial class BackendProviderCompatibilityContractTests
{
    private static readonly BulkMutationKey[] ApprovedBulkMutations = [];
    private static readonly string[] NullableCaptureNames =
        ["nextAttempt", "groupProfileId", "result", "completedAtUtc"];

    [Fact]
    public void Every_bulk_mutation_is_explicitly_audited()
    {
        var actual = ScanBulkMutations(RepositoryRoot());

        Assert.Equal(74, actual.Count);
        Assert.Empty(actual.Select(item => item.Key).Except(ApprovedBulkMutations));
    }

    [Fact]
    public void Approved_bulk_mutations_do_not_assign_null_or_nullable_captures()
    {
        var risky = ScanBulkMutations(RepositoryRoot())
            .Where(item => item.Invocation.Contains(")null", StringComparison.Ordinal)
                || NullableCaptureNames.Any(name =>
                    item.Invocation.Contains($", {name})", StringComparison.Ordinal)))
            .Select(item => item.Key)
            .ToArray();

        Assert.Empty(risky);
    }

    [Fact]
    public void Ef_queries_do_not_use_unapproved_runtime_guid_contains()
    {
        Assert.Empty(ScanRuntimeGuidContains(RepositoryRoot()));
    }

    private static IReadOnlyList<ScannedBulkMutation> ScanBulkMutations(string repositoryRoot)
    {
        var serverRoot = Path.Combine(repositoryRoot, "src", "server");
        var results = new List<ScannedBulkMutation>();
        foreach (var path in Directory.EnumerateFiles(serverRoot, "*.cs", SearchOption.AllDirectories)
                     .Where(path => !IsGeneratedPath(path)))
        {
            var source = File.ReadAllText(path);
            var ordinals = new Dictionary<(string Method, string Operation), int>();
            foreach (Match match in BulkMutationCallRegex().Matches(source))
            {
                var method = EnclosingMethod(source, match.Index);
                var operation = match.Groups[1].Value;
                var ordinalKey = (method, operation);
                ordinals.TryGetValue(ordinalKey, out var ordinal);
                ordinal++;
                ordinals[ordinalKey] = ordinal;
                results.Add(new ScannedBulkMutation(
                    new BulkMutationKey(
                        Path.GetRelativePath(repositoryRoot, path).Replace('\\', '/'),
                        method,
                        ordinal,
                        operation),
                    ExtractInvocation(source, match.Index)));
            }
        }

        return results
            .OrderBy(item => item.Key.Path, StringComparer.Ordinal)
            .ThenBy(item => item.Key.Method, StringComparer.Ordinal)
            .ThenBy(item => item.Key.Operation, StringComparer.Ordinal)
            .ThenBy(item => item.Key.Ordinal)
            .ToArray();
    }

    private static string EnclosingMethod(string source, int callIndex)
    {
        var matches = MethodDeclarationRegex().Matches(source[..callIndex]);
        return matches.Count == 0
            ? throw new InvalidOperationException($"Bulk mutation at offset {callIndex} is not inside a recognized method.")
            : matches[^1].Groups[1].Value;
    }

    private static IReadOnlyList<string> ScanRuntimeGuidContains(string repositoryRoot)
    {
        var serverRoot = Path.Combine(repositoryRoot, "src", "server");
        var results = new List<string>();
        foreach (var path in Directory.EnumerateFiles(serverRoot, "*.cs", SearchOption.AllDirectories)
                     .Where(path => !IsGeneratedPath(path)))
        {
            var source = File.ReadAllText(path);
            foreach (Match match in RuntimeGuidContainsRegex().Matches(source))
            {
                results.Add($"{Path.GetRelativePath(repositoryRoot, path).Replace('\\', '/')}:{match.Groups[1].Value}");
            }
        }

        return results.Order(StringComparer.Ordinal).ToArray();
    }

    private static string ExtractInvocation(string source, int callIndex)
    {
        var openParenthesis = source.IndexOf('(', callIndex);
        if (openParenthesis < 0)
            throw new InvalidOperationException($"Bulk mutation at offset {callIndex} has no argument list.");

        var depth = 0;
        for (var index = openParenthesis; index < source.Length; index++)
        {
            switch (source[index])
            {
                case '(':
                    depth++;
                    break;
                case ')':
                    depth--;
                    if (depth == 0)
                        return source[callIndex..(index + 1)];
                    break;
            }
        }

        throw new InvalidOperationException($"Bulk mutation at offset {callIndex} has an unbalanced argument list.");
    }

    private static bool IsGeneratedPath(string path) =>
        path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
        || path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
        || path.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase);

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "WechatRobot.slnx")))
                return directory.FullName;
        }

        throw new InvalidOperationException("Repository root was not found.");
    }

    private sealed record BulkMutationKey(
        string Path,
        string Method,
        int Ordinal,
        string Operation);

    private sealed record ScannedBulkMutation(
        BulkMutationKey Key,
        string Invocation);

    [GeneratedRegex(@"\.Execute(Update|Delete)Async\s*\(")]
    private static partial Regex BulkMutationCallRegex();

    [GeneratedRegex(@"(?m)^\s*(?:public|private|internal|protected)\s+(?:static\s+)?(?:async\s+)?[^\r\n=;{}]+?\s+([A-Za-z_][A-Za-z0-9_]*)\s*\(")]
    private static partial Regex MethodDeclarationRegex();

    [GeneratedRegex(@"\b(expectedVersionIds|documentIds|newVersionIds|memoryIds)\.Contains\s*\(")]
    private static partial Regex RuntimeGuidContainsRegex();
}
