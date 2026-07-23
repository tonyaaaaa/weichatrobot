using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace WechatRobot.Infrastructure.Logging;

public static partial class RedactionEnricher
{
    public const string Mask = "[REDACTED]";

    private static readonly HashSet<string> SensitiveNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "apikey", "accesskey", "accesskeyid", "accesskeysecret", "ossaccesskeyid", "secret",
        "callbacksecret", "callbacktoken", "token", "authorization", "robotid", "worktoolrobotid",
        "cipher", "ciphertext", "encrypted", "encryptedciphertext", "password", "signingkey",
        "masterkey", "privatekey", "signature", "securitytoken", "credential"
    };

    private static readonly HashSet<string> SensitiveSignedQueryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "ossaccesskeyid", "signature", "securitytoken", "xosssecuritytoken", "xosssignature",
        "xosscredential", "xamzsecuritytoken", "xamzsignature", "xamzcredential",
        "googleaccessid"
    };

    public static string? RedactValue(string propertyName, string? value) =>
        IsSensitiveName(propertyName) && value is not null ? Mask : RedactMessage(value ?? string.Empty);

    public static string RedactMessage(string message)
    {
        if (string.IsNullOrEmpty(message)) return message;
        if (TryRedactJson(message, out var json)) return json;
        return RedactTextFallback(message);
    }

    public static bool IsSensitiveName(string name)
    {
        var leaf = name.Split([':', '.'], StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? name;
        return SensitiveNames.Contains(NormalizeName(leaf));
    }

    private static bool TryRedactJson(string text, out string redacted)
    {
        try
        {
            var node = JsonNode.Parse(text);
            if (node is null)
            {
                redacted = "null";
                return true;
            }
            RedactJsonNode(node);
            redacted = node.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
            return true;
        }
        catch (JsonException)
        {
            redacted = string.Empty;
            return false;
        }
    }

    private static void RedactJsonNode(JsonNode node)
    {
        if (node is JsonObject jsonObject)
        {
            foreach (var property in jsonObject.ToList())
            {
                if (IsSensitiveName(property.Key))
                {
                    jsonObject[property.Key] = Mask;
                }
                else if (property.Value is JsonValue value && value.TryGetValue<string>(out var text))
                {
                    jsonObject[property.Key] = RedactMessage(text);
                }
                else if (property.Value is not null)
                {
                    RedactJsonNode(property.Value);
                }
            }
            return;
        }

        if (node is JsonArray jsonArray)
        {
            for (var index = 0; index < jsonArray.Count; index++)
            {
                if (jsonArray[index] is JsonValue value && value.TryGetValue<string>(out var text))
                    jsonArray[index] = RedactMessage(text);
                else if (jsonArray[index] is not null)
                    RedactJsonNode(jsonArray[index]!);
            }
        }
    }

    private static string RedactTextFallback(string text)
    {
        var redacted = HttpUrlRegex().Replace(text, match => RedactSignedUrl(match.Value));
        redacted = JsonStringSecretRegex().Replace(redacted, "${prefix}" + Mask + "${suffix}");
        redacted = AuthorizationRegex().Replace(redacted, "${prefix}" + Mask);
        return NamedSecretRegex().Replace(redacted, "${prefix}" + Mask);
    }

    private static string RedactSignedUrl(string candidate)
    {
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            string.IsNullOrEmpty(uri.Query))
            return candidate;

        var changed = false;
        var query = uri.Query.TrimStart('?').Split('&', StringSplitOptions.None)
            .Select(part =>
            {
                var separator = part.IndexOf('=');
                var rawName = separator < 0 ? part : part[..separator];
                var rawValue = separator < 0 ? string.Empty : part[(separator + 1)..];
                string decodedName;
                try { decodedName = Uri.UnescapeDataString(rawName.Replace("+", "%20", StringComparison.Ordinal)); }
                catch (UriFormatException) { decodedName = rawName; }
                if (!IsSensitiveSignedQueryName(decodedName)) return part;
                changed = true;
                return separator < 0 ? rawName : $"{rawName}={Uri.EscapeDataString(Mask)}";
            })
            .ToArray();
        if (!changed) return candidate;
        var builder = new UriBuilder(uri) { Query = string.Join('&', query) };
        return builder.Uri.AbsoluteUri;
    }

    private static bool IsSensitiveSignedQueryName(string name)
    {
        var normalized = NormalizeName(name);
        return SensitiveSignedQueryNames.Contains(normalized) || SensitiveNames.Contains(normalized);
    }

    private static string NormalizeName(string name) => Regex.Replace(name, "[^A-Za-z0-9]", string.Empty);

    [GeneratedRegex("""(?<prefix>"(?:api[-_]?key|access[-_]?key(?:[-_]?id|[-_]?secret)?|ossaccesskeyid|callback[-_]?(?:secret|token)|token|authorization|robot[-_]?id|worktoolrobotid|cipher|ciphertext|encrypted(?:ciphertext)?|password|signing[-_]?key|master[-_]?key|private[-_]?key|signature|security[-_]?token|credential)"\s*:\s*")(?:(?:\\.)|[^"\\])*(?<suffix>")""", RegexOptions.IgnoreCase)]
    private static partial Regex JsonStringSecretRegex();

    [GeneratedRegex(@"(?<prefix>\bAuthorization\s*[:=]\s*(?:Bearer\s+)?)[^\s,;""}\]]+", RegexOptions.IgnoreCase)]
    private static partial Regex AuthorizationRegex();

    [GeneratedRegex(@"(?<prefix>\b(?:api[-_]?key|access[-_]?key(?:[-_]?id|[-_]?secret)?|ossaccesskeyid|callback[-_]?(?:secret|token)|token|robot[-_]?id|worktoolrobotid|cipher|ciphertext|encrypted(?:ciphertext)?|password|signing[-_]?key|master[-_]?key|private[-_]?key|signature|security[-_]?token|credential)\s*[:=]\s*)[^\s,;&""}\]]+", RegexOptions.IgnoreCase)]
    private static partial Regex NamedSecretRegex();

    [GeneratedRegex(@"https?://[^\s""'<>]+", RegexOptions.IgnoreCase)]
    private static partial Regex HttpUrlRegex();
}
