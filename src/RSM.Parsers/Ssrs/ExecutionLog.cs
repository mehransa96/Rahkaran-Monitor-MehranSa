namespace RSM.Parsers.Ssrs;

/// <summary>One row of <c>dbo.ExecutionLog3</c> as RSM reads it (no parameters column: report parameters may hold personal data).</summary>
public sealed class ExecutionLogRow
{
    public string? ItemPath { get; set; }

    public string? UserName { get; set; }

    public string? RequestType { get; set; }

    public string? Format { get; set; }

    public string? Source { get; set; }

    public string? Status { get; set; }

    public int? AgeS { get; set; }

    public int? HourOfDay { get; set; }

    public int? DurationMs { get; set; }

    public int? DataMs { get; set; }

    public int? ProcessingMs { get; set; }

    public int? RenderingMs { get; set; }

    public long? RowsReturned { get; set; }

    public long? Bytes { get; set; }

    public bool Failed => !string.Equals(Status, "rsSuccess", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Where a report spends its time: retrieving data (SQL), processing, or rendering.</summary>
public enum ReportPhase
{
    Data,
    Processing,
    Rendering,
}

/// <param name="Dominant">The phase with the most time; data-dominant slowness is a SQL problem, render-dominant is SSRS/format.</param>
public sealed record ReportStats(string ItemPath, int Executions, int Failures, int P50Ms, int P95Ms, int MaxMs, double AvgDataMs, double AvgProcessingMs, double AvgRenderingMs,
    ReportPhase Dominant, string? TopUser);

public sealed record CountBy(string Key, int Count);

public sealed record ExecutionSummary(int Executions, int Failures, IReadOnlyList<ReportStats> Reports, IReadOnlyList<CountBy> ByHour, IReadOnlyList<CountBy> TopUsers,
    IReadOnlyList<CountBy> FailureStatuses, IReadOnlyList<CountBy> BySource)
{
    public double FailurePct => Executions == 0 ? 0 : 100.0 * Failures / Executions;
}

/// <summary>
/// Aggregates ExecutionLog3 rows (spec §11): per report runs, failures, nearest-rank p50/p95/max of the total time and the
/// average split between data retrieval, processing and rendering; executions per hour, top users, failure statuses.
/// </summary>
public static class ExecutionLogAggregator
{
    public static ExecutionSummary Aggregate(IEnumerable<ExecutionLogRow> rows, int topReports = 50)
    {
        var list = rows.Where(r => !string.IsNullOrEmpty(r.ItemPath)).ToList();
        var reports = list.GroupBy(r => r.ItemPath!, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var durations = g.Select(r => r.DurationMs ?? 0).Order().ToList();
                double data = g.Average(r => r.DataMs ?? 0), processing = g.Average(r => r.ProcessingMs ?? 0), rendering = g.Average(r => r.RenderingMs ?? 0);
                var dominant = data >= processing && data >= rendering ? ReportPhase.Data : rendering >= processing ? ReportPhase.Rendering : ReportPhase.Processing;
                return new ReportStats(g.Key, g.Count(), g.Count(r => r.Failed), Percentile(durations, 50), Percentile(durations, 95), durations[^1],
                    data, processing, rendering, dominant,
                    g.Where(r => !string.IsNullOrEmpty(r.UserName)).GroupBy(r => r.UserName!, StringComparer.OrdinalIgnoreCase).MaxBy(u => u.Count())?.Key);
            })
            .OrderByDescending(r => r.P95Ms)
            .Take(topReports)
            .ToList();

        static IReadOnlyList<CountBy> Count(IEnumerable<string?> keys, int top) =>
            keys.Where(k => !string.IsNullOrEmpty(k)).GroupBy(k => k!, StringComparer.OrdinalIgnoreCase)
                .Select(g => new CountBy(g.Key, g.Count())).OrderByDescending(c => c.Count).ThenBy(c => c.Key, StringComparer.Ordinal).Take(top).ToList();

        return new ExecutionSummary(list.Count, list.Count(r => r.Failed), reports,
            list.Where(r => r.HourOfDay is not null).GroupBy(r => r.HourOfDay!.Value).OrderBy(g => g.Key)
                .Select(g => new CountBy(g.Key.ToString("00", System.Globalization.CultureInfo.InvariantCulture), g.Count())).ToList(),
            Count(list.Select(r => r.UserName), 10),
            Count(list.Where(r => r.Failed).Select(r => r.Status), 10),
            Count(list.Select(r => r.Source), 10));
    }

    /// <summary>Nearest-rank percentile of a sorted list (same definition as the IIS log analysis).</summary>
    public static int Percentile(IReadOnlyList<int> sorted, double p) =>
        sorted.Count == 0 ? 0 : sorted[Math.Clamp((int)Math.Ceiling(p / 100 * sorted.Count) - 1, 0, sorted.Count - 1)];

    /// <summary>SSRS writes free text into LastStatus; failures say "Failure …", "Error …" or "… failed …".</summary>
    public static bool IsFailedSubscriptionStatus(string? lastStatus) =>
        lastStatus is not null && (lastStatus.Contains("fail", StringComparison.OrdinalIgnoreCase) || lastStatus.Contains("error", StringComparison.OrdinalIgnoreCase));
}
