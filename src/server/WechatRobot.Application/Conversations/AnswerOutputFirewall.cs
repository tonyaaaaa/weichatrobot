using System.Text.RegularExpressions;

namespace WechatRobot.Application.Conversations;

public sealed record OutputValidationResult(bool IsSafe, string? Reason = null);

public sealed class AnswerOutputFirewall
{
    private static readonly Regex GenericMarker = new(
        @"(?:\[\s*\d+\s*\]|\b(?:source|sources|reference|references|ref|page)\s*[:：#]?|(?:来源|参考|引用|页码|第\s*\d+\s*页)\s*[:：]?|https?://|www\.|\b[^\s]+\.(?:pdf|docx?|md|txt|xlsx?|pptx?)\b)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public OutputValidationResult Validate(string output, IReadOnlyList<RetrievalEvidence> evidence)
    {
        if (string.IsNullOrWhiteSpace(output)) return new(false, "empty_output");
        if (GenericMarker.IsMatch(output)) return new(false, "generic_source_marker");
        foreach (var item in evidence)
        {
            if (!string.IsNullOrWhiteSpace(item.DocumentTitle) && output.Contains(item.DocumentTitle, StringComparison.OrdinalIgnoreCase))
                return new(false, "evidence_document_marker");
            if (!string.IsNullOrWhiteSpace(item.SourceUri) && output.Contains(item.SourceUri, StringComparison.OrdinalIgnoreCase))
                return new(false, "evidence_uri_marker");
            if (!string.IsNullOrWhiteSpace(item.SourceFileName) && output.Contains(item.SourceFileName, StringComparison.OrdinalIgnoreCase))
                return new(false, "evidence_filename_marker");
            if (output.Contains(item.ChunkId.ToString("D"), StringComparison.OrdinalIgnoreCase) ||
                output.Contains(item.VersionId.ToString("D"), StringComparison.OrdinalIgnoreCase))
                return new(false, "evidence_identifier_marker");
        }
        return new(true);
    }
}
