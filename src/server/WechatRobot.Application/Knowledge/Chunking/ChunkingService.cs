using System.Text.RegularExpressions;
using WechatRobot.Application.Knowledge.Parsing;

namespace WechatRobot.Application.Knowledge.Chunking;

public enum ChunkPolicyKind { Smart, Separator, Regex, Qa }
public sealed record QaEntry(string Question, IReadOnlyList<string> Synonyms, string Answer);
public sealed record ChunkPolicy(
    ChunkPolicyKind Kind,
    int TargetTokens = 800,
    int OverlapTokens = 120,
    int MaximumTokens = 1000,
    string? Separator = null,
    string? RegexPattern = null,
    IReadOnlyList<QaEntry>? QaEntries = null);

public sealed record ChunkPreview(
    Guid Id, int Sequence, string Text, int? PageNumber, IReadOnlyList<string> Headings,
    bool IsTable, int? TableRows, int? TableColumns,
    string? Question = null, IReadOnlyList<string>? Synonyms = null, string? Answer = null)
{
    public int EstimatedTokens => ChunkingService.Tokenize(Text).Count;
}

public sealed class ChunkingService
{
    private const int MaximumPatternLength = 512;
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);

    public IReadOnlyList<ChunkPreview> Generate(IReadOnlyList<ParsedBlock> blocks, ChunkPolicy policy)
    {
        Validate(policy);
        if (policy.Kind == ChunkPolicyKind.Qa)
            return (policy.QaEntries ?? []).Select((entry, index) => new ChunkPreview(Guid.NewGuid(), index,
                $"问题：{entry.Question}\n同义问法：{string.Join('；', entry.Synonyms)}\n答案：{entry.Answer}", null, [], false, null, null,
                entry.Question, entry.Synonyms.ToArray(), entry.Answer)).ToArray();

        var result = new List<ChunkPreview>();
        foreach (var block in blocks)
        {
            var sections = Split(block.Text, policy);
            foreach (var section in sections)
            {
                var tokens = TokenSpans(section);
                if (tokens.Count == 0) continue;
                var start = 0;
                while (start < tokens.Count)
                {
                    var take = Math.Min(policy.TargetTokens, Math.Min(policy.MaximumTokens, tokens.Count - start));
                    var first = tokens[start];
                    var last = tokens[start + take - 1];
                    var text = section[first.Index..(last.Index + last.Length)].Trim();
                    result.Add(new ChunkPreview(Guid.NewGuid(), result.Count, text, block.PageNumber, block.Headings.ToArray(),
                        block.IsTable, block.TableRows, block.TableColumns));
                    if (start + take >= tokens.Count) break;
                    start += Math.Max(1, take - policy.OverlapTokens);
                }
            }
        }
        return result;
    }

    private static IEnumerable<string> Split(string text, ChunkPolicy policy)
    {
        if (policy.Kind == ChunkPolicyKind.Separator)
            return text.Split(policy.Separator!, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (policy.Kind == ChunkPolicyKind.Regex)
            return Regex.Split(text, policy.RegexPattern!, RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, RegexTimeout)
                .Select(value => value.Trim()).Where(value => value.Length > 0);
        return text.Split(["\r\n\r\n", "\n\n"], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    }

    private static void Validate(ChunkPolicy policy)
    {
        if (policy.TargetTokens < 1 || policy.TargetTokens > 100_000 || policy.MaximumTokens < policy.TargetTokens || policy.MaximumTokens > 100_000 || policy.OverlapTokens < 0 || policy.OverlapTokens >= policy.TargetTokens)
            throw new ArgumentOutOfRangeException(nameof(policy), "Chunk lengths are invalid.");
        if (policy.Kind == ChunkPolicyKind.Separator && (string.IsNullOrEmpty(policy.Separator) || policy.Separator.Length > MaximumPatternLength)) throw new ArgumentException("A bounded separator is required.", nameof(policy));
        if (policy.Kind == ChunkPolicyKind.Regex)
        {
            if (string.IsNullOrWhiteSpace(policy.RegexPattern) || policy.RegexPattern.Length > MaximumPatternLength) throw new ArgumentException("A bounded regex is required.", nameof(policy));
            _ = new Regex(policy.RegexPattern, RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, RegexTimeout);
        }
        if (policy.Kind == ChunkPolicyKind.Qa && (policy.QaEntries is null || policy.QaEntries.Count == 0 || policy.QaEntries.Count > 10_000 ||
            policy.QaEntries.Any(entry => string.IsNullOrWhiteSpace(entry.Question) || string.IsNullOrWhiteSpace(entry.Answer) || entry.Question.Length > 2048 || entry.Answer.Length > 1_000_000 || entry.Synonyms.Count > 100 || entry.Synonyms.Any(value => value.Length > 2048))))
            throw new ArgumentException("QA entries are missing or exceed safety limits.", nameof(policy));
    }

    internal static IReadOnlyList<string> Tokenize(string text) => TokenSpans(text)
        .Select(match => match.Value).ToArray();
    private static IReadOnlyList<Match> TokenSpans(string text) => Regex.Matches(text, @"[\u3400-\u4DBF\u4E00-\u9FFF\uF900-\uFAFF]|[\p{L}\p{N}]+|[^\s]", RegexOptions.CultureInvariant, RegexTimeout)
        .ToArray();
}

public sealed class ChunkPreviewEditor
{
    private const int MaximumPreviewCharacters = 1_000_000;
    public IReadOnlyList<ChunkPreview> Edit(IReadOnlyList<ChunkPreview> previews, Guid id, string text)
    {
        EnsureExists(previews, id);
        return Normalize(previews.Select(item => item.Id == id ? item with { Text = Required(text) } : item));
    }
    public IReadOnlyList<ChunkPreview> Delete(IReadOnlyList<ChunkPreview> previews, Guid id)
    {
        EnsureExists(previews, id);
        return Normalize(previews.Where(item => item.Id != id));
    }
    public IReadOnlyList<ChunkPreview> Split(IReadOnlyList<ChunkPreview> previews, Guid id, int offset)
    {
        EnsureExists(previews, id);
        var result = new List<ChunkPreview>();
        foreach (var item in previews)
        {
            if (item.Id != id) { result.Add(item); continue; }
            if (offset <= 0 || offset >= item.Text.Length) throw new ArgumentOutOfRangeException(nameof(offset));
            result.Add(item with { Text = item.Text[..offset].TrimEnd() });
            result.Add(item with { Id = Guid.NewGuid(), Text = item.Text[offset..].TrimStart() });
        }
        return Normalize(result);
    }
    public IReadOnlyList<ChunkPreview> Merge(IReadOnlyList<ChunkPreview> previews, Guid firstId, Guid secondId)
    {
        EnsureExists(previews, firstId);
        EnsureExists(previews, secondId);
        var first = previews.Single(item => item.Id == firstId);
        var second = previews.Single(item => item.Id == secondId);
        if (second.Sequence != first.Sequence + 1) throw new InvalidOperationException("Only adjacent previews can be merged.");
        if (first.PageNumber != second.PageNumber || first.IsTable != second.IsTable || first.TableRows != second.TableRows || first.TableColumns != second.TableColumns || !first.Headings.SequenceEqual(second.Headings))
            throw new InvalidOperationException("Previews with different source metadata cannot be merged.");
        return Normalize(previews.Where(item => item.Id != secondId).Select(item => item.Id == firstId ? item with { Text = $"{first.Text}{second.Text}" } : item));
    }
    private static string Required(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("Preview text is required.");
        if (text.Length > MaximumPreviewCharacters) throw new ArgumentException("Preview text exceeds the configured safety limit.");
        return text.Trim();
    }
    private static void EnsureExists(IReadOnlyList<ChunkPreview> previews, Guid id)
    {
        if (!previews.Any(item => item.Id == id)) throw new KeyNotFoundException();
    }
    private static IReadOnlyList<ChunkPreview> Normalize(IEnumerable<ChunkPreview> previews) => previews.OrderBy(item => item.Sequence).Select((item, index) => item with { Sequence = index }).ToArray();
}
