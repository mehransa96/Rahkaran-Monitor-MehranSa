using System.Globalization;
using System.Net;
using System.Text;
using RSM.Core.Model;
using RSM.Core.Slowness;

namespace RSM.Reporting.Html;

/// <summary>
/// HTML export of a slowness investigation (§12): ranked causes with confidence, evidence values with timestamps,
/// context, and the text-only next steps. Self-contained (inline CSS, no script, no external resource); every value is
/// HTML-encoded and every cause carries the general-guidance label.
/// </summary>
public static class InvestigationReport
{
    public static string Render(InvestigationResult result, SlownessCapture capture, string siteName, UiLanguage language, IReadOnlyList<DetectorDefinition>? detectors = null,
        TimeZoneInfo? zone = null)
    {
        var fa = language == UiLanguage.Fa;
        string T(string faText, string enText) => WebUtility.HtmlEncode(fa ? faText : enText);
        string E(string? text) => WebUtility.HtmlEncode(text ?? string.Empty);
        string Time(DateTimeOffset? utc) => utc is { } t ? E(TimeZoneInfo.ConvertTime(t, zone ?? TimeZoneInfo.Local).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)) : string.Empty;
        string Num(double? v) => v is { } d ? E(d.ToString("0.##", CultureInfo.InvariantCulture)) : string.Empty;
        var nodes = capture.Nodes.ToDictionary(n => n.NodeId, n => n.NodeName);

        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"<!DOCTYPE html><html lang=\"{(fa ? "fa" : "en")}\" dir=\"{(fa ? "rtl" : "ltr")}\"><head><meta charset=\"utf-8\">");
        sb.Append("<meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; style-src 'unsafe-inline'\">");
        sb.Append(CultureInfo.InvariantCulture, $"<title>{T("بررسی کندی", "Slowness investigation")} — {E(siteName)}</title>");
        sb.Append("<style>body{font-family:Tahoma,Segoe UI,sans-serif;margin:24px;color:#1f2328}h1{font-size:20px}h2{font-size:16px;margin-top:22px}")
          .Append("table{border-collapse:collapse;width:100%;margin:6px 0}td,th{border:1px solid #d0d7de;padding:4px 6px;font-size:12px;text-align:start}")
          .Append(".muted{color:#57606a;font-size:12px}.gen{background:#fff8c5;padding:1px 6px;border-radius:4px;font-size:11px}.conf{font-weight:bold}</style></head><body>");
        sb.Append(CultureInfo.InvariantCulture, $"<h1>{T("چرا راهکاران کند است؟", "Why is Rahkaran slow?")} — {E(siteName)}</h1>");
        sb.Append(CultureInfo.InvariantCulture, $"<p class=\"muted\">{T("ضبط", "Capture")} <code>{E(capture.CaptureId)}</code>: {Time(capture.FromUtc)} → {Time(capture.ToUtc)}; ")
          .Append(CultureInfo.InvariantCulture, $"{T("گره‌ها", "nodes")}: {E(string.Join(", ", capture.Nodes.Select(n => n.NodeName)))}</p>");
        sb.Append(CultureInfo.InvariantCulture, $"<p class=\"muted\">{T("RSM فقط خواندنی است؛ گام‌های بعدی متن هستند و RSM هیچ‌کدام را اجرا نمی‌کند.", "RSM is read-only; next steps are text and RSM never performs any of them.")}</p>");
        if (result.Causes.Count == 0)
        {
            sb.Append(CultureInfo.InvariantCulture, $"<p>{T("در این ضبط علت محتملی یافت نشد.", "No likely cause was found in this capture.")}</p>");
        }

        var rank = 0;
        foreach (var cause in result.Causes)
        {
            rank++;
            sb.Append(CultureInfo.InvariantCulture, $"<h2>{rank}. {E(fa ? cause.Cause.Title.Fa : cause.Cause.Title.En)} <span class=\"conf\">{E(cause.Confidence.ToString("P0", CultureInfo.InvariantCulture))}</span> ")
              .Append(CultureInfo.InvariantCulture, $"<code>{E(cause.Cause.Id)}</code> <span class=\"gen\">{T("راهنمای عمومی — با ستاد فنی تأیید کنید", "general guidance — confirm with ستاد فنی")}</span></h2>");
            sb.Append(CultureInfo.InvariantCulture, $"<p>{E(fa ? cause.Cause.Explanation.Fa : cause.Cause.Explanation.En)}</p>");
            sb.Append(CultureInfo.InvariantCulture, $"<table><tr><th>{T("شاهد", "Evidence")}</th><th>{T("گره", "Node")}</th><th>{T("نمونه", "Instance")}</th><th>{T("مقدار", "Value")}</th><th>{T("قوت", "Strength")}</th><th>{T("زمان", "Time")}</th></tr>");
            foreach (var s in cause.Evidence)
            {
                var label = detectors?.FirstOrDefault(d => d.Id == s.DetectorId) is { } d ? (fa ? d.Label.Fa : d.Label.En) : s.DetectorId;
                sb.Append(CultureInfo.InvariantCulture, $"<tr><td>{E(label)}</td><td>{E(nodes.GetValueOrDefault(s.NodeId, s.NodeId))}</td><td>{E(s.Instance ?? s.RuleId)}</td><td>{Num(s.Value)}</td>")
                  .Append(CultureInfo.InvariantCulture, $"<td>{E(s.Strength.ToString("P0", CultureInfo.InvariantCulture))}</td><td>{Time(s.AtUtc)}</td></tr>");
            }

            sb.Append("</table>");
            if (cause.Context.Count > 0)
            {
                sb.Append("<ul class=\"muted\">");
                foreach (var c in cause.Context)
                {
                    sb.Append(CultureInfo.InvariantCulture, $"<li>{E(nodes.GetValueOrDefault(c.NodeId, c.NodeId))} — <code>{E(c.Key)}</code>: {E(Convert.ToString(c.Value, CultureInfo.InvariantCulture))}</li>");
                }

                sb.Append("</ul>");
            }

            sb.Append(CultureInfo.InvariantCulture, $"<p><b>{T("گام‌های بعدی (فقط متن)", "Next steps (text only)")}</b></p><ol>");
            foreach (var step in cause.Cause.NextSteps)
            {
                sb.Append(CultureInfo.InvariantCulture, $"<li>{E(fa ? step.Fa : step.En)}</li>");
            }

            sb.Append("</ol>");
        }

        return sb.Append("</body></html>").ToString();
    }
}
