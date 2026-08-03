namespace WechatRobot.LegacyVisaImport;

public sealed record LegacyMaterialRequirement(
    string MaterialName,
    string? OriginalType,
    bool IsMandatory,
    string? ExtendInfo,
    string? TemplateDownloadUrl,
    string? ExampleSampleUrl);

public sealed record LegacyApplicantMaterialSet(
    string ApplicantTypeCode,
    IReadOnlyList<LegacyMaterialRequirement> Materials);

public sealed record LegacyRawMaterial(
    string? ApplicantTypeCodes,
    LegacyMaterialRequirement Material);

public sealed record LegacyVisaProduct(
    string LegacyVisaId,
    string Title,
    string? CountryId,
    string? AreaRule,
    string? VisaCenter,
    string? WorkDay,
    string? StayDay,
    decimal BasePrice,
    IReadOnlyList<LegacyApplicantMaterialSet> ApplicantMaterials)
{
    public required string CountryName { get; init; }
    public string? NoticeDescription { get; init; }
}

public sealed record RenderedVisaDocument(
    string FileName,
    string Markdown,
    string Sha256);

public static class LegacyVisaNormalizer
{
    public static IReadOnlyList<LegacyApplicantMaterialSet> GroupMaterials(
        IEnumerable<LegacyRawMaterial> rows) => rows
        .SelectMany(row => SplitApplicantTypes(row.ApplicantTypeCodes)
            .Select(code => new { Code = code, row.Material }))
        .GroupBy(row => row.Code, StringComparer.Ordinal)
        .OrderBy(group => group.Key, StringComparer.Ordinal)
        .Select(group => new LegacyApplicantMaterialSet(
            group.Key,
            group.Select(row => row.Material)
                .Distinct()
                .OrderBy(material => material.MaterialName, StringComparer.Ordinal)
                .ThenBy(material => material.OriginalType, StringComparer.Ordinal)
                .ToArray()))
        .ToArray();

    public static IReadOnlyList<string> SplitApplicantTypes(string? value)
    {
        var values = value?.Split(
                [',', ';'],
                StringSplitOptions.RemoveEmptyEntries
                | StringSplitOptions.TrimEntries)
            .Where(item => item.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return values is { Length: > 0 } ? values : ["alllist"];
    }
}
