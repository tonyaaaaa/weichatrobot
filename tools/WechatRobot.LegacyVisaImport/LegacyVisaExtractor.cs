using System.Data.Common;
using MySql.Data.MySqlClient;

namespace WechatRobot.LegacyVisaImport;

public sealed record LegacyVisaSourceRow(
    string LegacyVisaId,
    string Title,
    string? CountryId,
    string? CountryName,
    string? AreaRule,
    string? VisaCenter,
    string? WorkDay,
    string? StayDay,
    decimal BasePrice,
    string? ApplicantTypeCodes,
    string? MaterialName,
    string? OriginalType,
    bool IsMandatory,
    string? ExtendInfo,
    string? TemplateDownloadUrl,
    string? ExampleSampleUrl,
    string? NoticeDescription);

public sealed record LegacyVisaSkippedProduct(
    string LegacyVisaId,
    string Title,
    string Reason);

public sealed record LegacyVisaExtractionResult(
    IReadOnlyList<LegacyVisaProduct> Products,
    IReadOnlyList<LegacyVisaSkippedProduct> Skipped);

public static class LegacyVisaExtractor
{
    private const string Query = """
        SELECT p.Id AS LegacyVisaId, p.VisaTitle AS Title, p.CountryId,
               c.zh_name AS CountryName, p.AreaRule,
               p.VisaCenter, p.WorkDay, p.StayDay, p.Price2 AS BasePrice,
               p.NoticeDesc AS NoticeDescription,
               v.PeopleType AS ApplicantTypeCodes, d.DocName AS MaterialName,
               d.DocType AS OriginalType, v.IsNeed AS IsMandatory,
               d.Extend AS ExtendInfo, d.FilePath AS TemplateDownloadUrl,
               d.DocImgPath AS ExampleSampleUrl
        FROM visa p
        LEFT JOIN country c ON c.id = p.CountryId
        LEFT JOIN visadoc v ON v.VisaId = p.Id
        LEFT JOIN docsecond d ON d.Id = v.DocSecondId
        WHERE p.IsDel = 0 OR p.IsDel IS NULL
        ORDER BY p.Id, v.PeopleType, d.DocName, d.Id
        """;

    public static async Task<LegacyVisaExtractionResult> ExtractAsync(
        string connectionString,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new ArgumentException("legacy_connection_string_required", nameof(connectionString));

        try
        {
            await using var connection = new MySqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = new MySqlCommand(Query, connection)
            {
                CommandTimeout = 120
            };
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var rows = new List<LegacyVisaSourceRow>();
            while (await reader.ReadAsync(cancellationToken)) rows.Add(Read(reader));
            return Assemble(rows);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is MySqlException or DbException or InvalidCastException)
        {
            throw new InvalidOperationException("legacy_source_query_failed", exception);
        }
    }

    public static LegacyVisaExtractionResult Assemble(
        IEnumerable<LegacyVisaSourceRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var products = new List<LegacyVisaProduct>();
        var skipped = new List<LegacyVisaSkippedProduct>();
        foreach (var group in rows.GroupBy(row => row.LegacyVisaId, StringComparer.Ordinal)
                     .OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            var first = group.First();
            if (group.Any(row => !SameMetadata(first, row)))
                throw new InvalidDataException($"legacy_visa_metadata_conflict:{group.Key}");
            if (string.IsNullOrWhiteSpace(first.CountryName))
            {
                skipped.Add(new(first.LegacyVisaId, first.Title, "country_relation_missing"));
                continue;
            }

            var materials = group
                .Where(row => !string.IsNullOrWhiteSpace(row.MaterialName))
                .Select(row => new LegacyRawMaterial(
                    row.ApplicantTypeCodes,
                    new LegacyMaterialRequirement(
                        row.MaterialName!.Trim(), row.OriginalType, row.IsMandatory,
                        row.ExtendInfo, row.TemplateDownloadUrl, row.ExampleSampleUrl)));

            products.Add(new LegacyVisaProduct(
                first.LegacyVisaId, first.Title, first.CountryId, first.AreaRule,
                first.VisaCenter, first.WorkDay, first.StayDay, first.BasePrice,
                LegacyVisaNormalizer.GroupMaterials(materials))
            {
                CountryName = first.CountryName,
                NoticeDescription = first.NoticeDescription
            });
        }

        return new(products, skipped);
    }

    private static LegacyVisaSourceRow Read(DbDataReader reader) => new(
        Text(reader, "LegacyVisaId") ?? throw new InvalidDataException("legacy_visa_id_missing"),
        Text(reader, "Title") ?? throw new InvalidDataException("legacy_visa_title_missing"),
        Text(reader, "CountryId"), Text(reader, "CountryName"), Text(reader, "AreaRule"), Text(reader, "VisaCenter"),
        Text(reader, "WorkDay"), Text(reader, "StayDay"),
        reader.IsDBNull(reader.GetOrdinal("BasePrice")) ? 0 : Convert.ToDecimal(reader["BasePrice"]),
        Text(reader, "ApplicantTypeCodes"), Text(reader, "MaterialName"),
        Text(reader, "OriginalType"), Boolean(reader, "IsMandatory"),
        Text(reader, "ExtendInfo"), Text(reader, "TemplateDownloadUrl"),
        Text(reader, "ExampleSampleUrl"), Text(reader, "NoticeDescription"));

    private static string? Text(DbDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : Convert.ToString(reader.GetValue(ordinal))?.Trim();
    }

    private static bool Boolean(DbDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        if (reader.IsDBNull(ordinal)) return false;
        var value = reader.GetValue(ordinal);
        return value switch
        {
            bool boolean => boolean,
            byte number => number != 0,
            sbyte number => number != 0,
            short number => number != 0,
            int number => number != 0,
            long number => number != 0,
            _ => string.Equals(Convert.ToString(value), "true", StringComparison.OrdinalIgnoreCase)
                 || string.Equals(Convert.ToString(value), "1", StringComparison.Ordinal)
        };
    }

    private static bool SameMetadata(LegacyVisaSourceRow left, LegacyVisaSourceRow right) =>
        left.LegacyVisaId == right.LegacyVisaId
        && left.Title == right.Title
        && left.CountryId == right.CountryId
        && left.CountryName == right.CountryName
        && left.AreaRule == right.AreaRule
        && left.VisaCenter == right.VisaCenter
        && left.WorkDay == right.WorkDay
        && left.StayDay == right.StayDay
        && left.NoticeDescription == right.NoticeDescription
        && left.BasePrice == right.BasePrice;
}
