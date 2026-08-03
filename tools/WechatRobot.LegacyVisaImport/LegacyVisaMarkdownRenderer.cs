using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace WechatRobot.LegacyVisaImport;

public static class LegacyVisaMarkdownRenderer
{
    private static readonly IReadOnlyDictionary<string, string> ApplicantLabels =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["alllist"] = "通用材料",
            ["worklist"] = "在职人员",
            ["studentlist"] = "18岁以上学生",
            ["student2list"] = "18岁以下学生",
            ["finishlist"] = "退休人员",
            ["retirelist"] = "退休人员",
            ["freelist"] = "无业人员",
            ["childlist"] = "学龄前儿童"
        };

    public static RenderedVisaDocument Render(
        LegacyVisaProduct product,
        DateOnly snapshotDate)
    {
        ArgumentNullException.ThrowIfNull(product);
        if (string.IsNullOrWhiteSpace(product.LegacyVisaId))
            throw new ArgumentException("legacy_visa_id_required", nameof(product));
        if (string.IsNullOrWhiteSpace(product.Title))
            throw new ArgumentException("visa_title_required", nameof(product));
        if (string.IsNullOrWhiteSpace(product.CountryName))
            throw new ArgumentException("country_name_required", nameof(product));

        var text = new StringBuilder();
        text.Append("# ").AppendLine(Escape(product.Title.Trim()));
        text.AppendLine();
        text.AppendLine("## 签证基本信息");
        AppendValue(text, "签证名称", product.Title);
        AppendValue(text, "国家名称", product.CountryName);
        AppendValue(text, "受理范围", product.AreaRule);
        AppendValue(text, "签证中心", product.VisaCenter);
        AppendValue(text, "办理时间", product.WorkDay);
        AppendValue(text, "停留时间", product.StayDay);
        text.Append("- 数据导入日期：")
            .AppendLine(snapshotDate.ToString("yyyy-MM-dd"));

        var notice = HtmlToText(product.NoticeDescription);
        if (!string.IsNullOrWhiteSpace(notice))
        {
            text.AppendLine();
            text.AppendLine("## 注意事项");
            AppendValue(text, "注意事项", notice);
        }

        var sets = product.ApplicantMaterials
            .OrderBy(set => set.ApplicantTypeCode, StringComparer.Ordinal)
            .ToArray();
        foreach (var set in sets)
        {
            var label = ApplicantLabel(set.ApplicantTypeCode);
            text.Append("- 申请人类型：").Append(label)
                .Append("（原始代码：").Append(Escape(set.ApplicantTypeCode)).AppendLine("）");
        }
        if (sets.Length == 0)
            text.AppendLine("- 材料状态：旧系统未配置材料");

        foreach (var set in sets)
        {
            var label = ApplicantLabel(set.ApplicantTypeCode);
            text.AppendLine();
            text.Append("## ").Append(label).Append("（")
                .Append(Escape(set.ApplicantTypeCode)).AppendLine("）");
            foreach (var material in set.Materials
                         .Distinct()
                         .OrderBy(item => item.MaterialName, StringComparer.Ordinal))
            {
                text.AppendLine();
                text.Append("### ").AppendLine(Escape(material.MaterialName));
                text.Append("- 材料名称：").AppendLine(Escape(material.MaterialName));
                text.Append("- 是否必须：").AppendLine(material.IsMandatory ? "必须" : "可选");
                AppendValue(text, "材料类型", material.OriginalType);
                AppendValue(text, "补充说明", HtmlToText(material.ExtendInfo));
                AppendValue(text, "模板下载地址", material.TemplateDownloadUrl);
                AppendValue(text, "示例图片地址", material.ExampleSampleUrl);
            }
        }

        var markdown = text.ToString().ReplaceLineEndings("\n");
        var hash = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(markdown)));
        return new(StableFileName(product), markdown, hash);
    }

    private static string StableFileName(LegacyVisaProduct product)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var title = new string(product.Title.Trim()
            .Select(character => invalid.Contains(character) ? '-' : character)
            .ToArray()).Trim(' ', '.', '-');
        if (title.Length > 80) title = title[..80].TrimEnd();
        return $"legacy-visa-{product.LegacyVisaId.Trim()}-{title}.md";
    }

    private static string ApplicantLabel(string code) =>
        ApplicantLabels.TryGetValue(code, out var label) ? label : "其他申请人";

    private static void AppendValue(StringBuilder text, string label, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        text.Append("- ").Append(label).Append('：').AppendLine(Escape(value));
    }

    private static string Escape(string value) => Regex.Replace(value.Trim(), @"\s+", " ")
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal)
        .Replace("#", "\\#", StringComparison.Ordinal);

    private static string? HtmlToText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return value;
        var separated = Regex.Replace(
            value,
            @"</?(?:p|div|li|ul|ol|br|tr|td|th|h[1-6])\b[^>]*>",
            " ",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return WebUtility.HtmlDecode(
            Regex.Replace(separated, @"<[^>]+>", " ", RegexOptions.CultureInvariant));
    }
}
