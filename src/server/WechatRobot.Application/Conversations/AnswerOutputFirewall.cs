using System.Text.RegularExpressions;

namespace WechatRobot.Application.Conversations;

public sealed record OutputValidationResult(bool IsSafe, string? Reason = null);

public sealed class AnswerOutputFirewall
{
    private static readonly Regex GenericMarker = new(
        @"(?:\[\s*\d+\s*\]|\b(?:source|sources|reference|references|ref|page)\s*[:：#]?|(?:来源|参考|引用|页码|第\s*\d+\s*页)\s*[:：]?|https?://|www\.|\b[^\s]+\.(?:pdf|docx?|md|txt|xlsx?|pptx?)\b)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex InternalProtocolMarker = new(
        @"(?:<\s*\|?\s*(?:tool[_\s-]?(?:call|response|result)|function[_\s-]?call)\s*\|?\s*>|[\""'](?:tool[_\s-]?calls?|function[_\s-]?call)[\""']\s*:|<<<\s*(?:UNTRUSTED|ESCAPED_UNTRUSTED)_|\bsystem\s+prompt\b)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex MarkerlessWebSearchCall = new(
        @"(?:[\""']name[\""']\s*:\s*[\""']web_search[\""']\s*,\s*[\""']arguments[\""']\s*:|[\""']arguments[\""']\s*:\s*\{[\s\S]{0,512}?\}\s*,\s*[\""']name[\""']\s*:\s*[\""']web_search[\""'])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public OutputValidationResult Validate(string output, IReadOnlyList<RetrievalEvidence> evidence)
    {
        if (string.IsNullOrWhiteSpace(output)) return new(false, "empty_output");
        if (ContainsInternalProtocol(output)) return new(false, "internal_instruction_marker");
        if (GenericMarker.IsMatch(output)) return new(false, "generic_source_marker");
        foreach (var item in evidence)
        {
            if (!string.IsNullOrWhiteSpace(item.DocumentTitle) && output.Contains(item.DocumentTitle, StringComparison.OrdinalIgnoreCase))
                return new(false, "evidence_document_marker");
            if (!string.IsNullOrWhiteSpace(item.SourceUri) && output.Contains(item.SourceUri, StringComparison.OrdinalIgnoreCase))
                return new(false, "evidence_uri_marker");
            if (!string.IsNullOrWhiteSpace(item.SourceFileName) && output.Contains(item.SourceFileName, StringComparison.OrdinalIgnoreCase))
                return new(false, "evidence_filename_marker");
            if (ContainsId(output, item.DocumentId) || ContainsId(output, item.VersionId) || ContainsId(output, item.ChunkId))
                return new(false, "evidence_identifier_marker");
        }
        return new(true);
    }

    public OutputValidationResult ValidateUngrounded(string output)
    {
        if (string.IsNullOrWhiteSpace(output)) return new(false, "empty_output");
        if (output.Length > 8000) return new(false, "output_too_long");
        if (output.Any(character => char.IsControl(character)
            && character is not ('\r' or '\n' or '\t')))
            return new(false, "control_character");
        if (ContainsInternalProtocol(output))
            return new(false, "internal_instruction_marker");
        return new(true);
    }

    private static bool ContainsInternalProtocol(string output) =>
        InternalProtocolMarker.IsMatch(output)
        || MarkerlessWebSearchCall.IsMatch(output);

    private static bool ContainsId(string output, Guid id) => output.Contains(id.ToString("D"), StringComparison.OrdinalIgnoreCase) ||
        output.Contains(id.ToString("N"), StringComparison.OrdinalIgnoreCase);
}
