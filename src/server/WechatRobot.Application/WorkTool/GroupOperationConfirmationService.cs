using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace WechatRobot.Application.WorkTool;

public sealed class GroupOperationConfirmationService(string signingKey)
{
    private readonly byte[] _signingKey = SHA256.HashData(Encoding.UTF8.GetBytes(signingKey));

    public string Issue(string operatorName, string payloadJson, DateTime nowUtc, TimeSpan lifetime)
    {
        var expiresAtUtc = new DateTimeOffset(DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc).Add(lifetime)).ToUnixTimeSeconds();
        var payloadHash = Hash(Normalize(payloadJson));
        var message = $"{operatorName}\n{expiresAtUtc}\n{payloadHash}";
        return $"{expiresAtUtc}.{payloadHash}.{Base64Url(HMACSHA256.HashData(_signingKey, Encoding.UTF8.GetBytes(message)))}";
    }

    public bool IsValid(string token, string operatorName, string payloadJson, DateTime nowUtc)
    {
        var parts = token.Split('.', StringSplitOptions.None);
        if (parts.Length != 3 || !long.TryParse(parts[0], out var expiresAtUnix) || expiresAtUnix < new DateTimeOffset(DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc)).ToUnixTimeSeconds()) return false;
        var payloadHash = Hash(Normalize(payloadJson));
        if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(parts[1]), Encoding.UTF8.GetBytes(payloadHash))) return false;
        var expected = HMACSHA256.HashData(_signingKey, Encoding.UTF8.GetBytes($"{operatorName}\n{expiresAtUnix}\n{payloadHash}"));
        return TryFromBase64Url(parts[2], out var signature) && CryptographicOperations.FixedTimeEquals(expected, signature);
    }

    public static string Normalize(string json)
    {
        using var document = JsonDocument.Parse(json);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) WriteCanonical(document.RootElement, writer);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteCanonical(JsonElement element, Utf8JsonWriter writer)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject().OrderBy(item => item.Name, StringComparer.Ordinal)) { writer.WritePropertyName(property.Name); WriteCanonical(property.Value, writer); }
                writer.WriteEndObject(); break;
            case JsonValueKind.Array:
                writer.WriteStartArray(); foreach (var item in element.EnumerateArray()) WriteCanonical(item, writer); writer.WriteEndArray(); break;
            default: element.WriteTo(writer); break;
        }
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static string Base64Url(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static bool TryFromBase64Url(string value, out byte[] bytes)
    {
        try { bytes = Convert.FromBase64String(value.Replace('-', '+').Replace('_', '/') + new string('=', (4 - value.Length % 4) % 4)); return true; }
        catch (FormatException) { bytes = []; return false; }
    }
}
