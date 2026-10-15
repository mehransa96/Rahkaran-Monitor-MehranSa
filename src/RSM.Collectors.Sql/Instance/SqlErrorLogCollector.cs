using System.Globalization;
using RSM.Core.Model;
using RSM.Parsers.ErrorLog;
using RSM.Safety.Files;
using RSM.Safety.Sql;

namespace RSM.Collectors.Sql.Instance;

/// <summary>
/// §7.15 SQL Server error log: <c>xp_readerrorlog</c> (current + archives, last <see cref="WindowDays"/> days) or,
/// if not permitted, the ERRORLOG file itself (locally or through the admin share). Classified by ErrorLogParser.
/// </summary>
public sealed class SqlErrorLogCollector(SafeSqlExecutor executor) : SqlCollectorBase(executor)
{
    public const int WindowDays = 7;
    public const int MaxArchives = 6;
    public const long MaxFileBytes = 32L * 1024 * 1024;

    private static readonly string[] TrackedCodes = ["823", "824", "825", "833", "17883", "17884", "701", "802", "9002", "1205", "18456", "3041", "stackdump", "cacheflush"];

    public override string Id => "sql.errorlog";

    protected override async Task<IReadOnlyDictionary<string, object?>> CollectCoreAsync(INodeContext node, CollectContext context, SqlQueryRunner runner, List<Fact> facts, List<UnifiedEvent> events)
    {
        var since = DateTime.Now.AddDays(-WindowDays);
        var entries = new List<ErrorLogEntry>();
        string? source = null;
        string? unavailableReason = null;

        var current = await runner.QueryAsync<ErrorLogRow>("sql.log.errorlog", new { LogNumber = 0, LogType = 1, Since = since }).ConfigureAwait(false);
        if (current is not null)
        {
            source = "xp_readerrorlog";
            entries.AddRange(current.Select(r => r.ToEntry()));
            for (var archive = 1; archive <= MaxArchives; archive++)
            {
                // An archive older than the window returns no rows; a missing archive fails — both end the scan.
                var rows = await runner.QueryOptionalAsync<ErrorLogRow>("sql.log.errorlog", new { LogNumber = archive, LogType = 1, Since = since }).ConfigureAwait(false);
                if (rows is null || rows.Count == 0)
                {
                    break;
                }

                entries.AddRange(rows.Select(r => r.ToEntry()));
            }
        }
        else
        {
            var path = (await runner.QueryAsync<SqlInventoryCollector.ServerPropertiesRow>("sql.inv.server_properties").ConfigureAwait(false))?.FirstOrDefault()?.ErrorLogFileName;
            (source, unavailableReason) = await ReadFileFallbackAsync(node.Node.Host, path, entries).ConfigureAwait(false);
        }

        Add(facts, "sql.errorlog.available", source is not null);
        if (source is null)
        {
            Add(facts, "sql.errorlog.unavailable_reason", unavailableReason);
            return new Dictionary<string, object?> { ["source"] = null, ["reason"] = unavailableReason };
        }

        var summary = ErrorLogParser.Analyze(entries, since);
        Add(facts, "sql.errorlog.source", source);
        Add(facts, "sql.errorlog.window_days", WindowDays);
        foreach (var code in TrackedCodes)
        {
            Add(facts, $"sql.errorlog.e{code}", summary.CountsByCode.GetValueOrDefault(code));
        }

        var loginsPerHour = summary.Events.Where(e => e.Category == ErrorLogCategory.FailedLogin)
            .GroupBy(e => new DateTime(e.LoggedAt.Year, e.LoggedAt.Month, e.LoggedAt.Day, e.LoggedAt.Hour, 0, 0))
            .Select(g => g.Count()).DefaultIfEmpty(0).Max();
        Add(facts, "sql.errorlog.failed_logins_max_per_hour", loginsPerHour);
        Add(facts, "sql.errorlog.failed_login_groups", summary.FailedLogins.Take(10)
            .Select(g => string.Create(CultureInfo.InvariantCulture, $"{g.Login} (state {g.State}) from {g.Client ?? "?"} ×{g.Count}")).ToList());
        Add(facts, "sql.errorlog.trace_flags", summary.TraceFlags.Select(t => (object?)t).ToList());

        foreach (var e in summary.Events)
        {
            events.Add(new UnifiedEvent(new DateTimeOffset(e.LoggedAt), node.Node.Id, Layer.Sql, "MSSQLSERVER", e.Code, e.Severity, e.Message));
        }

        return new Dictionary<string, object?>
        {
            ["source"] = source,
            ["events"] = summary.Events,
            ["failedLogins"] = summary.FailedLogins,
            ["traceFlags"] = summary.TraceFlags,
            ["oldestEntry"] = summary.OldestEntry,
        };
    }

    /// <summary>Reads ERRORLOG locally, or via \\host\D$\… when RSM runs elsewhere (Windows + admin share).</summary>
    private static async Task<(string? Source, string? Reason)> ReadFileFallbackAsync(string host, string? serverPath, List<ErrorLogEntry> entries)
    {
        if (string.IsNullOrWhiteSpace(serverPath))
        {
            return (null, "xp_readerrorlog not permitted and ERRORLOG path unknown");
        }

        var path = ResolvePath(host, serverPath);
        if (path is null)
        {
            return (null, "xp_readerrorlog not permitted; ERRORLOG file not reachable from this machine");
        }

        try
        {
            var (text, _) = await ReadOnlyFile.ReadTailTextAsync(path, MaxFileBytes, CancellationToken.None).ConfigureAwait(false);
            entries.AddRange(ErrorLogParser.ParseFileText(text));
            return ("file", null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (null, $"xp_readerrorlog not permitted; ERRORLOG file not readable ({ex.GetType().Name})");
        }
    }

    internal static string? ResolvePath(string host, string serverPath)
    {
        if (IsLocal(host))
        {
            return ReadOnlyFile.Exists(serverPath) ? serverPath : null;
        }

        // D:\Log\ERRORLOG → \\host\D$\Log\ERRORLOG (admin share; Windows only).
        if (OperatingSystem.IsWindows() && serverPath.Length > 2 && serverPath[1] == ':')
        {
            var unc = $@"\\{host}\{serverPath[0]}${serverPath[2..]}";
            return ReadOnlyFile.Exists(unc) ? unc : null;
        }

        return null;
    }

    internal static bool IsLocal(string host) =>
        host is "." or "localhost" or "127.0.0.1" or "::1" || string.Equals(host, Environment.MachineName, StringComparison.OrdinalIgnoreCase);

    public sealed class ErrorLogRow
    {
        public DateTime? LogDate { get; set; }

        public string? ProcessInfo { get; set; }

        public string? Text { get; set; }

        public ErrorLogEntry ToEntry() => new(LogDate ?? DateTime.MinValue, ProcessInfo ?? string.Empty, Text ?? string.Empty);
    }
}
