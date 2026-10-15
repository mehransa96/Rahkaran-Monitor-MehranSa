using System.Globalization;
using System.Text.RegularExpressions;
using RSM.Core.Model;

namespace RSM.Parsers.ErrorLog;

/// <summary>One raw error-log line as returned by xp_readerrorlog (LogDate, ProcessInfo, Text) or read from the file.</summary>
public sealed record ErrorLogEntry(DateTime LoggedAt, string ProcessInfo, string Text);

public enum ErrorLogCategory
{
    IoCorruption,      // 823, 824
    IoRetry,           // 825
    SlowIo,            // 833
    NonYielding,       // 17883, 17884
    Memory,            // 701, 802
    LogFull,           // 9002
    Deadlock,          // 1205
    FailedLogin,       // 18456
    BackupFailure,     // 3041
    StackDump,
    CacheFlush,
    Other,
}

/// <summary>A classified error-log event. <see cref="Code"/> is the SQL error number or a synthetic code (e.g. "stackdump").</summary>
public sealed record ErrorLogEvent(
    DateTime LoggedAt,
    string Code,
    ErrorLogCategory Category,
    Severity Severity,
    string Message,
    IReadOnlyDictionary<string, string> Details);

/// <summary>Failed logins grouped by (login, state, client) — 18456 states explain the reason.</summary>
public sealed record FailedLoginGroup(string Login, int State, string? Client, int Count, DateTime First, DateTime Last);

public sealed record ErrorLogSummary(
    IReadOnlyList<ErrorLogEvent> Events,
    IReadOnlyDictionary<string, int> CountsByCode,
    IReadOnlyList<FailedLoginGroup> FailedLogins,
    IReadOnlyList<int> TraceFlags,
    DateTime? OldestEntry);

/// <summary>
/// Classifies SQL Server error-log text (spec §7.15). Works on xp_readerrorlog rows and on the raw ERRORLOG file
/// (fallback when xp_readerrorlog is not permitted). Pure function, no I/O.
/// </summary>
public static partial class ErrorLogParser
{
    private static readonly Dictionary<int, (ErrorLogCategory Category, Severity Severity)> KnownErrors = new()
    {
        [823] = (ErrorLogCategory.IoCorruption, Severity.Critical),
        [824] = (ErrorLogCategory.IoCorruption, Severity.Critical),
        [825] = (ErrorLogCategory.IoRetry, Severity.Warning),
        [833] = (ErrorLogCategory.SlowIo, Severity.Warning),
        [17883] = (ErrorLogCategory.NonYielding, Severity.Critical),
        [17884] = (ErrorLogCategory.NonYielding, Severity.Critical),
        [701] = (ErrorLogCategory.Memory, Severity.Critical),
        [802] = (ErrorLogCategory.Memory, Severity.Critical),
        [9002] = (ErrorLogCategory.LogFull, Severity.Critical),
        [1205] = (ErrorLogCategory.Deadlock, Severity.Info),
        [18456] = (ErrorLogCategory.FailedLogin, Severity.Warning),
        [3041] = (ErrorLogCategory.BackupFailure, Severity.Critical),
    };

    /// <summary>Splits ERRORLOG file text into entries; continuation lines are appended to the previous entry.</summary>
    public static IReadOnlyList<ErrorLogEntry> ParseFileText(string text)
    {
        var entries = new List<ErrorLogEntry>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var m = FileLine().Match(line);
            if (m.Success && DateTime.TryParseExact(m.Groups["ts"].Value, "yyyy-MM-dd HH:mm:ss.ff", CultureInfo.InvariantCulture, DateTimeStyles.None, out var ts))
            {
                entries.Add(new ErrorLogEntry(ts, m.Groups["proc"].Value, m.Groups["text"].Value.Trim()));
            }
            else if (entries.Count > 0 && line.Trim().Length > 0)
            {
                var last = entries[^1];
                entries[^1] = last with { Text = last.Text + "\n" + line.Trim() };
            }
        }

        return entries;
    }

    /// <summary>Classifies entries; entries before <paramref name="since"/> are ignored except for startup trace flags.</summary>
    public static ErrorLogSummary Analyze(IEnumerable<ErrorLogEntry> source, DateTime? since = null)
    {
        var entries = source.OrderBy(e => e.LoggedAt).ToList();
        var events = new List<ErrorLogEvent>();
        var traceFlags = new SortedSet<int>();

        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            CollectTraceFlags(entry.Text, traceFlags);
            if (since is { } s && entry.LoggedAt < s)
            {
                continue;
            }

            var header = ErrorHeader().Match(entry.Text);
            if (header.Success)
            {
                var number = int.Parse(header.Groups["n"].Value, CultureInfo.InvariantCulture);
                var state = int.Parse(header.Groups["state"].Value, CultureInfo.InvariantCulture);

                // The message text is the next entry of the same process logged at (almost) the same time.
                var message = string.Empty;
                if (i + 1 < entries.Count && entries[i + 1].ProcessInfo == entry.ProcessInfo
                    && (entries[i + 1].LoggedAt - entry.LoggedAt).Duration() <= TimeSpan.FromSeconds(1)
                    && !ErrorHeader().IsMatch(entries[i + 1].Text))
                {
                    message = entries[++i].Text;
                }

                if (KnownErrors.TryGetValue(number, out var known))
                {
                    var details = ExtractDetails(message);
                    details["state"] = state.ToString(CultureInfo.InvariantCulture);
                    events.Add(new ErrorLogEvent(entry.LoggedAt, number.ToString(CultureInfo.InvariantCulture), known.Category, known.Severity,
                        message.Length > 0 ? message : entry.Text, details));
                }

                continue;
            }

            if (ClassifyByText(entry) is { } byText)
            {
                events.Add(byText);
            }
        }

        var counts = events.GroupBy(e => e.Code).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var failed = events.Where(e => e.Category == ErrorLogCategory.FailedLogin)
            .GroupBy(e => (Login: e.Details.GetValueOrDefault("login") ?? "?", State: int.Parse(e.Details["state"], CultureInfo.InvariantCulture), Client: e.Details.GetValueOrDefault("client")))
            .Select(g => new FailedLoginGroup(g.Key.Login, g.Key.State, g.Key.Client, g.Count(), g.Min(e => e.LoggedAt), g.Max(e => e.LoggedAt)))
            .OrderByDescending(g => g.Count)
            .ToList();

        return new ErrorLogSummary(events, counts, failed, traceFlags.ToList(), entries.Count > 0 ? entries[0].LoggedAt : null);
    }

    /// <summary>Messages SQL Server writes without an "Error: n" header line.</summary>
    private static ErrorLogEvent? ClassifyByText(ErrorLogEntry entry)
    {
        var text = entry.Text;
        if (SlowIo().Match(text) is { Success: true } slow)
        {
            var d = new Dictionary<string, string>(StringComparer.Ordinal) { ["occurrences"] = slow.Groups["n"].Value };
            if (FileInBrackets().Match(text) is { Success: true } f)
            {
                d["file"] = f.Groups["file"].Value;
            }

            return new ErrorLogEvent(entry.LoggedAt, "833", ErrorLogCategory.SlowIo, Severity.Warning, text, d);
        }

        if (text.Contains("appears to be non-yielding on Scheduler", StringComparison.OrdinalIgnoreCase))
        {
            return new ErrorLogEvent(entry.LoggedAt, "17883", ErrorLogCategory.NonYielding, Severity.Critical, text, Empty());
        }

        if (text.Contains("have not been picked up by a worker thread", StringComparison.OrdinalIgnoreCase))
        {
            return new ErrorLogEvent(entry.LoggedAt, "17884", ErrorLogCategory.NonYielding, Severity.Critical, text, Empty());
        }

        if (text.Contains("Stack Dump being sent to", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("***Stack Dump", StringComparison.OrdinalIgnoreCase))
        {
            return new ErrorLogEvent(entry.LoggedAt, "stackdump", ErrorLogCategory.StackDump, Severity.Critical, text, Empty());
        }

        if (CacheFlush().Match(text) is { Success: true } flush)
        {
            return new ErrorLogEvent(entry.LoggedAt, "cacheflush", ErrorLogCategory.CacheFlush, Severity.Info, text,
                new Dictionary<string, string>(StringComparer.Ordinal) { ["cachestore"] = flush.Groups["store"].Value, ["reason"] = flush.Groups["reason"].Value });
        }

        return null;
    }

    private static Dictionary<string, string> ExtractDetails(string message)
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        if (LoginFailed().Match(message) is { Success: true } login)
        {
            d["login"] = login.Groups["login"].Value;
            if (login.Groups["reason"].Success)
            {
                d["reason"] = login.Groups["reason"].Value.Trim();
            }
        }

        if (Client().Match(message) is { Success: true } client)
        {
            d["client"] = client.Groups["ip"].Value;
        }

        if (DatabaseName().Match(message) is { Success: true } db)
        {
            d["database"] = db.Groups["db"].Value;
        }

        if (LogFullReason().Match(message) is { Success: true } reason)
        {
            d["log_reuse_wait"] = reason.Groups["reason"].Value;
        }

        if (FileInQuotes().Match(message) is { Success: true } file)
        {
            d["file"] = file.Groups["file"].Value;
        }

        return d;
    }

    private static void CollectTraceFlags(string text, SortedSet<int> flags)
    {
        foreach (Match m in StartupTraceFlag().Matches(text))
        {
            flags.Add(int.Parse(m.Groups["tf"].Value, CultureInfo.InvariantCulture));
        }

        foreach (Match m in DbccTraceOn().Matches(text))
        {
            foreach (var part in m.Groups["list"].Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                if (int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var tf))
                {
                    flags.Add(tf);
                }
            }
        }
    }

    private static Dictionary<string, string> Empty() => new(StringComparer.Ordinal);

    [GeneratedRegex(@"^(?<ts>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{2})\s+(?<proc>\S+)\s+(?<text>.*)$")]
    private static partial Regex FileLine();

    [GeneratedRegex(@"^Error:\s*(?<n>\d+),\s*Severity:\s*\d+,\s*State:\s*(?<state>\d+)\.?", RegexOptions.IgnoreCase)]
    private static partial Regex ErrorHeader();

    [GeneratedRegex(@"encountered\s+(?<n>\d+)\s+occurrence\(s\) of I/O requests taking longer than", RegexOptions.IgnoreCase)]
    private static partial Regex SlowIo();

    [GeneratedRegex(@"occurrence\(s\) of cachestore flush for the '(?<store>[^']+)' cachestore.*?due to '(?<reason>[^']+)'", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex CacheFlush();

    [GeneratedRegex(@"Login failed for user '(?<login>[^']*)'\.(?:\s*Reason:\s*(?<reason>[^\[]+))?", RegexOptions.IgnoreCase)]
    private static partial Regex LoginFailed();

    [GeneratedRegex(@"\[CLIENT:\s*(?<ip>[^\]]+)\]", RegexOptions.IgnoreCase)]
    private static partial Regex Client();

    [GeneratedRegex(@"database\s+'(?<db>[^']+)'", RegexOptions.IgnoreCase)]
    private static partial Regex DatabaseName();

    [GeneratedRegex(@"is full due to '(?<reason>[A-Z_]+)'", RegexOptions.IgnoreCase)]
    private static partial Regex LogFullReason();

    [GeneratedRegex(@"file\s+'(?<file>[^']+)'", RegexOptions.IgnoreCase)]
    private static partial Regex FileInQuotes();

    [GeneratedRegex(@"on file \[(?<file>[^\]]+)\]", RegexOptions.IgnoreCase)]
    private static partial Regex FileInBrackets();

    [GeneratedRegex(@"(?:^|\s)-T(?<tf>\d{1,5})\b")]
    private static partial Regex StartupTraceFlag();

    [GeneratedRegex(@"\bDBCC\s+TRACEON\s*\(?\s*(?<list>[\d,\s]+?)\s*\)?\s*,\s*server process ID", RegexOptions.IgnoreCase)]
    private static partial Regex DbccTraceOn();
}
