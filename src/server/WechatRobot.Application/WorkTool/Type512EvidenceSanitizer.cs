using System.Text.Json;
using System.Text.RegularExpressions;

namespace WechatRobot.Application.WorkTool;

public sealed record Type512EvidenceShape(
    int Type,
    bool MessageIdMatched,
    int ResultCount,
    string SuccessListJsonKind,
    string FailListJsonKind,
    IReadOnlyList<string> RawMessagePropertyNames,
    IReadOnlyList<string> SuccessListObjectPropertyNames,
    IReadOnlyList<string> FailListObjectPropertyNames);

public static partial class Type512EvidenceSanitizer
{
    public static Type512EvidenceShape Create(
        IReadOnlyList<WorkToolRawCommandResult> results,
        string expectedMessageId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedMessageId);
        var matched = results.FirstOrDefault(result =>
            result.Type == 512
            && string.Equals(
                result.MessageId,
                expectedMessageId,
                StringComparison.Ordinal));
        if (matched is null)
        {
            return new(
                512,
                false,
                results.Count,
                "Missing",
                "Missing",
                [],
                [],
                []);
        }

        var rawMessage = Inspect(matched.RawMessage);
        var success = Inspect(matched.SuccessListRaw);
        var fail = Inspect(matched.FailListRaw);
        return new(
            matched.Type,
            true,
            results.Count,
            success.Kind,
            fail.Kind,
            rawMessage.PropertyNames,
            success.PropertyNames,
            fail.PropertyNames);
    }

    private static InspectedJson Inspect(string? raw)
    {
        if (raw is null)
            return new("Missing", []);
        try
        {
            using var document = JsonDocument.Parse(raw);
            var names = new HashSet<string>(StringComparer.Ordinal);
            CollectSafePropertyNames(document.RootElement, names);
            return new(
                document.RootElement.ValueKind.ToString(),
                names.OrderBy(name => name, StringComparer.Ordinal).ToArray());
        }
        catch (JsonException)
        {
            return new("InvalidJson", []);
        }
    }

    private static void CollectSafePropertyNames(
        JsonElement element,
        ISet<string> names)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (SafePropertyName().IsMatch(property.Name))
                    names.Add(property.Name);
                CollectSafePropertyNames(property.Value, names);
            }
            return;
        }

        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                CollectSafePropertyNames(item, names);
        }
    }

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]{0,63}$",
        RegexOptions.CultureInvariant)]
    private static partial Regex SafePropertyName();

    private sealed record InspectedJson(
        string Kind,
        IReadOnlyList<string> PropertyNames);
}
