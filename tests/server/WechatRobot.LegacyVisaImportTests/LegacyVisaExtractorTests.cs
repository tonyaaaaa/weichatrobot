using WechatRobot.LegacyVisaImport;

namespace WechatRobot.LegacyVisaImportTests;

public sealed class LegacyVisaExtractorTests
{
    [Fact]
    public void Assemble_groups_join_rows_without_losing_products_that_have_no_materials()
    {
        var rows = new[]
        {
            Row("123", "日本三年多次签证", "worklist,studentlist", "护照"),
            Row("123", "日本三年多次签证", "worklist", "在职证明"),
            Row("456", "法国商务签证", null, null)
        };

        var result = LegacyVisaExtractor.Assemble(rows);
        var products = result.Products;

        Assert.Equal(2, products.Count);
        Assert.Empty(result.Skipped);
        Assert.Equal("日本", products[0].CountryName);
        Assert.Equal("请提前核对领区", products[0].NoticeDescription);
        Assert.Equal(["studentlist", "worklist"], products[0].ApplicantMaterials.Select(x => x.ApplicantTypeCode));
        Assert.Equal(2, products[0].ApplicantMaterials.Single(x => x.ApplicantTypeCode == "worklist").Materials.Count);
        Assert.Empty(products[1].ApplicantMaterials);
    }

    [Fact]
    public void Assemble_rejects_conflicting_product_metadata()
    {
        var rows = new[]
        {
            Row("123", "日本三年多次签证", "worklist", "护照"),
            Row("123", "冲突标题", "worklist", "护照")
        };

        var error = Assert.Throws<InvalidDataException>(() => LegacyVisaExtractor.Assemble(rows));

        Assert.Equal("legacy_visa_metadata_conflict:123", error.Message);
    }

    [Fact]
    public void Assemble_skips_products_whose_country_relation_is_missing_and_reports_reason()
    {
        var rows = new[]
        {
            Row("123", "日本三年多次签证", "worklist", "护照"),
            new LegacyVisaSourceRow(
                "456", "请选择---电子签证", "164", null,
                null, null, null, null, 0,
                null, null, null, false, null, null, null, null)
        };

        var result = LegacyVisaExtractor.Assemble(rows);

        var product = Assert.Single(result.Products);
        Assert.Equal("123", product.LegacyVisaId);
        var skipped = Assert.Single(result.Skipped);
        Assert.Equal("456", skipped.LegacyVisaId);
        Assert.Equal("请选择---电子签证", skipped.Title);
        Assert.Equal("country_relation_missing", skipped.Reason);
    }

    private static LegacyVisaSourceRow Row(
        string id,
        string title,
        string? applicantTypes,
        string? materialName) => new(
        id, title, "81", title.StartsWith("日本", StringComparison.Ordinal) ? "日本" : "法国",
        "全国受理", "广州", "8-10个工作日", "90天", 1288.50m,
        applicantTypes, materialName, "原件", true, "有效期至少六个月", null, null,
        "请提前核对领区");
}
