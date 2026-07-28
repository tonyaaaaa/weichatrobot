using System.Text.Json;
using System.Text.RegularExpressions;
using WechatRobot.Domain.Memory;

namespace WechatRobot.Application.Memory;

public sealed partial class MemoryExtractionValidator
{
    public const int MaximumContentLength = 1000;
    public const int MaximumMemories = 10;

    public MemoryExtractionResult Validate(string json, MemoryExtractionContext context)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            throw new MemoryExtractionException("memory_invalid_json");
        }

        using (document)
        {
            if (!document.RootElement.TryGetProperty("memories", out var memories) ||
                memories.ValueKind != JsonValueKind.Array ||
                memories.GetArrayLength() > MaximumMemories)
            {
                throw new MemoryExtractionException("memory_invalid_json");
            }

            var allowedSources = context.Messages.Select(x => x.Id).ToHashSet();
            var result = new List<ExtractedMemory>();
            foreach (var element in memories.EnumerateArray())
            {
                var typeValue = ReadRequiredString(element, "type");
                if (!Enum.TryParse<MemoryType>(typeValue, false, out var type))
                {
                    throw new MemoryExtractionException("memory_content_invalid");
                }

                var content = ReadRequiredString(element, "content").Trim();
                if (content.Length is 0 or > MaximumContentLength || SecretPattern().IsMatch(content))
                {
                    throw new MemoryExtractionException(
                        SecretPattern().IsMatch(content)
                            ? "memory_secret_detected"
                            : "memory_content_invalid");
                }

                if (!element.TryGetProperty("confidence", out var confidenceValue) ||
                    !confidenceValue.TryGetDouble(out var confidence) ||
                    confidence is < 0 or > 1)
                {
                    throw new MemoryExtractionException("memory_content_invalid");
                }

                if (!element.TryGetProperty("explicit", out var explicitValue) ||
                    explicitValue.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                {
                    throw new MemoryExtractionException("memory_content_invalid");
                }

                if (!element.TryGetProperty("sourceMessageIds", out var sources) ||
                    sources.ValueKind != JsonValueKind.Array)
                {
                    throw new MemoryExtractionException("memory_invalid_source");
                }

                var sourceIds = new List<Guid>();
                foreach (var source in sources.EnumerateArray())
                {
                    if (source.ValueKind != JsonValueKind.String ||
                        !Guid.TryParse(source.GetString(), out var sourceId) ||
                        !allowedSources.Contains(sourceId) ||
                        !sourceIds.AddIfAbsent(sourceId))
                    {
                        throw new MemoryExtractionException("memory_invalid_source");
                    }
                }

                if (sourceIds.Count == 0 || !ScopeSupports(context.Scope.Type, type))
                {
                    throw new MemoryExtractionException("memory_content_invalid");
                }

                result.Add(new ExtractedMemory(
                    type,
                    content,
                    confidence,
                    explicitValue.GetBoolean(),
                    sourceIds));
            }

            return new MemoryExtractionResult(result);
        }
    }

    private static string ReadRequiredString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) ||
            value.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new MemoryExtractionException("memory_content_invalid");
        }

        return value.GetString()!;
    }

    private static bool ScopeSupports(MemoryScopeType scope, MemoryType type) => type switch
    {
        MemoryType.UserPreference => scope is MemoryScopeType.User,
        MemoryType.GroupRule => scope is MemoryScopeType.Group or MemoryScopeType.User,
        MemoryType.RobotExperience => scope is not MemoryScopeType.Global,
        MemoryType.BusinessFact => scope is MemoryScopeType.Group or MemoryScopeType.User,
        _ => false
    };

    [GeneratedRegex(
        @"(?ix)(password\s*[:=]|passwd\s*[:=]|api[_ -]?key\s*[:=]|access[_ -]?key|secret\s*[:=]|bearer\s+[a-z0-9._\-]+|验证码\s*[:：]?\s*\d{4,8}|server\s*=.+password\s*=|connection\s*string)",
        RegexOptions.CultureInvariant)]
    private static partial Regex SecretPattern();
}

file static class MemoryCollectionExtensions
{
    public static bool AddIfAbsent<T>(this ICollection<T> values, T value)
    {
        if (values.Contains(value))
        {
            return false;
        }

        values.Add(value);
        return true;
    }
}
