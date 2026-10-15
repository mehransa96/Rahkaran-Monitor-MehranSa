using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using RSM.Core.Model;
using RSM.Parsers.Xml;

namespace RSM.Parsers.Iis;

public sealed record HttpErrReasonCount(string Reason, long Count, DateTimeOffset First, DateTimeOffset Last, string? TopQueue, LocalizedText? Meaning);

public sealed record HttpErrHour(DateTimeOffset Hour, string Reason, long Count);

/// <summary>
/// HTTP.sys error log (<c>%SystemRoot%\System32\LogFiles\HTTPERR</c>, spec §10.6): counts by <c>s-reason</c> over time.
/// Same W3C framing as IIS logs (<c>#Fields:</c> header, UTC times).
/// </summary>
public sealed class HttpErrAnalyzer(DateTimeOffset? fromUtc = null)
{
    private readonly Dictionary<string, (long Count, DateTimeOffset First, DateTimeOffset Last, Dictionary<string, long> Queues)> _reasons = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(DateTimeOffset, string), long> _hours = [];
    private string[] _fields = [];

    public void AddLine(string line)
    {
        if (line.Length == 0)
        {
            return;
        }

        if (line[0] == '#')
        {
            if (line.StartsWith("#Fields:", StringComparison.Ordinal))
            {
                _fields = line[8..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            }

            return;
        }

        var values = line.Split(' ');
        if (values.Length != _fields.Length)
        {
            return;
        }

        string? Get(string name)
        {
            var i = Array.FindIndex(_fields, f => string.Equals(f, name, StringComparison.OrdinalIgnoreCase));
            return i < 0 || values[i] == "-" ? null : values[i];
        }

        if (!DateTime.TryParseExact($"{Get("date")} {Get("time")}", "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var t))
        {
            return;
        }

        var at = new DateTimeOffset(t, TimeSpan.Zero);
        if (fromUtc is { } from && at < from)
        {
            return;
        }

        var reason = Get("s-reason") ?? "?";
        var queue = Get("s-queuename") ?? "-";
        if (!_reasons.TryGetValue(reason, out var r))
        {
            r = (0, at, at, new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase));
        }

        r.Queues[queue] = r.Queues.GetValueOrDefault(queue) + 1;
        _reasons[reason] = (r.Count + 1, at < r.First ? at : r.First, at > r.Last ? at : r.Last, r.Queues);
        var hour = new DateTimeOffset(at.Year, at.Month, at.Day, at.Hour, 0, 0, TimeSpan.Zero);
        _hours[(hour, reason)] = _hours.GetValueOrDefault((hour, reason)) + 1;
    }

    public IReadOnlyList<HttpErrReasonCount> Reasons() =>
        _reasons.OrderByDescending(r => r.Value.Count)
            .Select(r => new HttpErrReasonCount(r.Key, r.Value.Count, r.Value.First, r.Value.Last,
                r.Value.Queues.OrderByDescending(q => q.Value).Select(q => q.Key).FirstOrDefault(q => q != "-"), Meaning(r.Key)))
            .ToList();

    public IReadOnlyList<HttpErrHour> Hours() => _hours.OrderBy(h => h.Key.Item1).Select(h => new HttpErrHour(h.Key.Item1, h.Key.Item2, h.Value)).ToList();

    /// <summary>Reasons that point at the application side (queue full, app offline, abandoned) — the rules count these.</summary>
    public static bool IsServerSide(string reason) =>
        reason.Equals("QueueFull", StringComparison.OrdinalIgnoreCase) || reason.Equals("AppOffline", StringComparison.OrdinalIgnoreCase)
        || reason.Equals("Connection_Abandoned_By_ReqQueue", StringComparison.OrdinalIgnoreCase) || reason.Equals("AppShutdown", StringComparison.OrdinalIgnoreCase)
        || reason.Equals("Disabled", StringComparison.OrdinalIgnoreCase);

    public static LocalizedText? Meaning(string reason) => reason switch
    {
        "QueueFull" => new("صف درخواست Application Pool پر است (برنامه کند یا قفل شده).", "The app pool's request queue is full (the app is slow or stuck)."),
        "AppOffline" => new("Application Pool به دلیل خطاهای پیاپی (Rapid-fail) خاموش شده است.", "The app pool was taken offline by rapid-fail protection."),
        "Connection_Abandoned_By_ReqQueue" => new("فرایند کارگر (w3wp) هنگام پردازش درخواست بسته شد (Crash یا Recycle).", "The worker process ended while requests were queued (crash or recycle)."),
        "Connection_Dropped" => new("اتصال قبل از پایان پاسخ قطع شد (کلاینت یا شبکه).", "The connection was dropped before the response finished (client or network)."),
        "Timer_ConnectionIdle" => new("اتصال بیکار پس از مهلت بسته شد — معمولاً عادی.", "Idle connection closed after its timeout — usually normal."),
        "Timer_MinBytesPerSecond" => new("کلاینت داده را خیلی کند دریافت می‌کرد.", "The client received data too slowly."),
        "AppShutdown" => new("Application Pool در حال خاموش شدن بود.", "The app pool was shutting down."),
        "Disabled" => new("Application Pool غیرفعال است.", "The app pool is disabled."),
        _ => null,
    };
}

public sealed record StdoutSummary(string File, long ErrorLines, long WarningLines, IReadOnlyList<string> LastErrors, bool Truncated);

/// <summary>ASP.NET Core stdout / ANCM log tail (spec §10.7): counts error and warning lines, keeps the last few errors.</summary>
public static partial class StdoutLog
{
    public static StdoutSummary Summarize(string file, string tailText, bool truncated, int keep = 10)
    {
        var errors = new List<string>();
        long errorLines = 0, warningLines = 0;
        foreach (var line in tailText.Split('\n'))
        {
            var l = line.TrimEnd('\r');
            if (ErrorLine().IsMatch(l))
            {
                errorLines++;
                errors.Add(l.Length > 300 ? l[..300] + "…" : l);
            }
            else if (WarningLine().IsMatch(l))
            {
                warningLines++;
            }
        }

        return new StdoutSummary(file, errorLines, warningLines, errors.TakeLast(keep).ToList(), truncated);
    }

    [GeneratedRegex(@"\b(fail|crit|error|exception|unhandled)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ErrorLine();

    [GeneratedRegex(@"\b(warn|warning)\b", RegexOptions.IgnoreCase)]
    private static partial Regex WarningLine();
}

/// <summary>One existing Failed Request Tracing file (RSM lists and reads them; it never enables tracing).</summary>
public sealed record FrebEntry(string File, string? Url, string? Verb, int? StatusCode, int? SubStatus, long? TimeTakenMs, string? AppPool, string? FailureReason, DateTimeOffset? AtUtc);

public static class FrebLog
{
    public static FrebEntry? Parse(string file, string xml)
    {
        var doc = SafeXml.TryParse(xml, 16 * 1024 * 1024);
        var root = doc?.Root;
        if (root is null || root.Name.LocalName != "failedRequest")
        {
            return null;
        }

        var status = (string?)root.Attribute("statusCode");
        var main = status?.Split('.');
        var started = root.Descendants().FirstOrDefault(e => e.Name.LocalName == "TimeCreated")?.Attribute("SystemTime");
        return new FrebEntry(
            file,
            (string?)root.Attribute("url"),
            (string?)root.Attribute("verb"),
            main is { Length: > 0 } && int.TryParse(main[0], out var s) ? s : null,
            main is { Length: > 1 } && int.TryParse(main[1], out var ss) ? ss : null,
            long.TryParse((string?)root.Attribute("timeTaken"), out var tt) ? tt : null,
            (string?)root.Attribute("appPoolId"),
            (string?)root.Attribute("failureReason"),
            DateTimeOffset.TryParse((string?)started, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var at) ? at : null);
    }
}
