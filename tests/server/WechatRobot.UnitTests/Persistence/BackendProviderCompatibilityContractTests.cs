using System.Text.RegularExpressions;

namespace WechatRobot.UnitTests.Persistence;

public sealed partial class BackendProviderCompatibilityContractTests
{
    private static readonly BulkMutationKey[] ApprovedBulkMutations =
    [
        new("src/server/WechatRobot.Infrastructure/Conversations/GroundedConversationRepository.cs", "EvaluateInboundPolicyAsync", 1, "Update"),
        new("src/server/WechatRobot.Infrastructure/Conversations/GroundedConversationRepository.cs", "LeaseForProcessingAsync", 1, "Update"),
        new("src/server/WechatRobot.Infrastructure/Conversations/GroundedConversationRepository.cs", "LeaseForProcessingAsync", 2, "Update"),
        new("src/server/WechatRobot.Infrastructure/Conversations/GroundedConversationRepository.cs", "LeaseForProcessingAsync", 3, "Update"),
        new("src/server/WechatRobot.Infrastructure/Conversations/GroundedConversationRepository.cs", "RenewLeaseAsync", 1, "Update"),
        new("src/server/WechatRobot.Infrastructure/Knowledge/KnowledgeCandidatePublishProcessor.cs", "ProcessAsync", 1, "Update"),
        new("src/server/WechatRobot.Infrastructure/Knowledge/QdrantKnowledgeService.cs", "ActivateVersionAsync", 1, "Update"),
        new("src/server/WechatRobot.Infrastructure/Knowledge/QdrantKnowledgeService.cs", "LeaseNextAsync", 1, "Update"),
        new("src/server/WechatRobot.Infrastructure/Knowledge/QdrantKnowledgeService.cs", "RenewLeaseAsync", 1, "Update"),
        new("src/server/WechatRobot.Infrastructure/Persistence/DurableJobRepository.cs", "LeaseNextJobAsync", 1, "Update"),
        new("src/server/WechatRobot.Infrastructure/Persistence/DurableJobRepository.cs", "LeaseNextSendCommandAsync", 1, "Update"),
        new("src/server/WechatRobot.Infrastructure/Persistence/DurableJobRepository.cs", "LeaseNextSendCommandAsync", 2, "Update"),
        new("src/server/WechatRobot.Infrastructure/Persistence/DurableJobRepository.cs", "MarkSendDispatchingAsync", 1, "Update"),
        new("src/server/WechatRobot.Infrastructure/Persistence/DurableJobRepository.cs", "RenewJobLeaseAsync", 1, "Update"),
        new("src/server/WechatRobot.Infrastructure/Persistence/DurableJobRepository.cs", "RenewSendLeasesAsync", 1, "Update"),
        new("src/server/WechatRobot.Infrastructure/Persistence/DurableJobRepository.cs", "RenewSendLeasesAsync", 2, "Update"),
        new("src/server/WechatRobot.Infrastructure/Persistence/DurableJobRepository.cs", "UpdateRelatedMessageStateAsync", 1, "Update"),
        new("src/server/WechatRobot.Worker/Jobs/WorkToolGroupOperationWorker.cs", "ProcessOnceAsync", 1, "Update"),
        new("src/server/WechatRobot.Worker/Jobs/WorkToolGroupReconciliationWorker.cs", "ProcessOnceAsync", 1, "Update")
    ];
    private static readonly string[] NullableCaptureNames =
        ["nextAttempt", "groupProfileId", "result", "completedAtUtc"];

    [Fact]
    public void Every_bulk_mutation_is_explicitly_audited()
    {
        var actual = ScanBulkMutations(RepositoryRoot());

        Assert.Equal(ApprovedBulkMutations, actual.Select(item => item.Key));
    }

    [Fact]
    public void Audit_ledger_records_the_original_inventory_and_current_allowlist()
    {
        var ledger = File.ReadAllText(Path.Combine(
            RepositoryRoot(),
            "docs",
            "runbooks",
            "backend-mysql-ef-provider-audit.md"));
        var originalInventory = Section(ledger, "## Bulk mutation 清单", "## Residual atomic CAS allowlist");
        var originalRows = OriginalAuditRowRegex().Matches(originalInventory);
        Assert.Equal(74, originalRows.Count);
        Assert.DoesNotContain(originalRows.Cast<Match>(), match =>
            match.Value.Contains("| Unreviewed |", StringComparison.Ordinal));

        var residualSection = Section(ledger, "## Residual atomic CAS allowlist", "## Runtime Guid 查询清单");
        var documented = ResidualAuditRowRegex().Matches(residualSection)
            .Select(match => new BulkMutationKey(
                match.Groups["path"].Value,
                match.Groups["method"].Value,
                int.Parse(match.Groups["ordinal"].Value),
                match.Groups["operation"].Value))
            .OrderBy(item => item.Path, StringComparer.Ordinal)
            .ThenBy(item => item.Method, StringComparer.Ordinal)
            .ThenBy(item => item.Operation, StringComparer.Ordinal)
            .ThenBy(item => item.Ordinal)
            .ToArray();
        Assert.Equal(ApprovedBulkMutations, documented);
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

    private static string Section(string source, string heading, string nextHeading)
    {
        var start = source.IndexOf(heading, StringComparison.Ordinal);
        var end = source.IndexOf(nextHeading, start + heading.Length, StringComparison.Ordinal);
        if (start < 0 || end < 0)
            throw new InvalidOperationException($"Audit ledger section '{heading}' was not found.");
        return source[start..end];
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

    [GeneratedRegex(@"(?m)^\| `src/server/[^`]+` \| `[^`]+` \| \d+ \| (?:Update|Delete) \| (?:ReplaceTracked|KeepAtomic|RemoveGuidContains) \|")]
    private static partial Regex OriginalAuditRowRegex();

    [GeneratedRegex(@"(?m)^\| `(?<path>src/server/[^`]+)` \| `(?<method>[^`]+)` \| (?<ordinal>\d+) \| (?<operation>Update|Delete) \|$")]
    private static partial Regex ResidualAuditRowRegex();
}
