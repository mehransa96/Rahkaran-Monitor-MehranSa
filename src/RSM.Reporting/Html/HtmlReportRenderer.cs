using System.Globalization;
using System.Text;
using RSM.Reporting.Model;
using Scriban;
using Scriban.Runtime;

namespace RSM.Reporting.Html;

/// <summary>
/// Renders the report as one self-contained HTML file: Vazirmatn embedded as base64, inline CSS and SVG, no scripts,
/// no external references (a CSP meta tag forbids them), RTL for Persian and LTR for English.
/// </summary>
public static class HtmlReportRenderer
{
    private static readonly Lazy<Template> Template = new(() =>
    {
        var template = Scriban.Template.Parse(Resource("report.sbn"), "report.sbn");
        if (template.HasErrors)
        {
            throw new InvalidOperationException("Report template: " + string.Join("; ", template.Messages));
        }

        return template;
    });

    private static readonly Lazy<string> Fonts = new(() =>
        FontFace("Vazirmatn-Regular.ttf", 400) + FontFace("Vazirmatn-Bold.ttf", 700));

    public static string Render(ReportModel model)
    {
        var globals = new ScriptObject
        {
            ["m"] = model,
            ["css"] = Resource("report.css"),
            ["fonts"] = Fonts.Value,
            ["chart"] = SeverityChart(model),
            ["sep"] = model.Lang == "fa" ? "، " : ", ",
        };
        var context = new TemplateContext
        {
            MemberRenamer = member => member.Name,
            LoopLimit = 1_000_000,
            StrictVariables = true,
            EnableRelaxedMemberAccess = false,
        };
        context.PushGlobal(globals);
        return Template.Value.Render(context);
    }

    /// <summary>Horizontal bar of finding counts by severity (numbers only — no server data).</summary>
    internal static string SeverityChart(ReportModel model)
    {
        var total = Math.Max(1, model.Critical + model.Warning + model.Info);
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"<svg class=\"chart\" role=\"img\" aria-label=\"{model.L["Findings"]}\" viewBox=\"0 0 600 28\" width=\"100%\" height=\"28\" preserveAspectRatio=\"none\">");
        double x = 0;
        foreach (var (count, color) in new[] { (model.Critical, "#b42318"), (model.Warning, "#b54708"), (model.Info, "#175cd3") })
        {
            if (count == 0)
            {
                continue;
            }

            var width = 600.0 * count / total;
            sb.Append(CultureInfo.InvariantCulture, $"<rect x=\"{x:0.##}\" y=\"0\" width=\"{width:0.##}\" height=\"28\" fill=\"{color}\"/>");
            x += width;
        }

        if (model.Critical + model.Warning + model.Info == 0)
        {
            sb.Append("<rect x=\"0\" y=\"0\" width=\"600\" height=\"28\" fill=\"#067647\"/>");
        }

        sb.Append("</svg>");
        return sb.ToString();
    }

    private static string FontFace(string file, int weight)
    {
        using var stream = typeof(HtmlReportRenderer).Assembly.GetManifestResourceStream($"RSM.Reporting.Resources.{file}")
            ?? throw new InvalidOperationException($"Missing embedded font {file}.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return string.Create(CultureInfo.InvariantCulture,
            $"@font-face {{ font-family:Vazirmatn; font-weight:{weight}; src:url(data:font/ttf;base64,{Convert.ToBase64String(buffer.ToArray())}) format(\"truetype\"); }}\n");
    }

    private static string Resource(string name)
    {
        using var stream = typeof(HtmlReportRenderer).Assembly.GetManifestResourceStream($"RSM.Reporting.Html.{name}")
            ?? throw new InvalidOperationException($"Missing embedded resource {name}.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
