namespace RSM.Core.History;

/// <summary>Summary of one series over one period (from history points: averages weighted by N, max of maxes).</summary>
public sealed record PeriodStats(double Avg, double P50, double P95, double Max, int Points)
{
    public static PeriodStats? Of(IReadOnlyCollection<(double Avg, double Max, int N)> points)
    {
        var valid = points.Where(p => double.IsFinite(p.Avg) && p.N > 0).ToList();
        if (valid.Count == 0)
        {
            return null;
        }

        var avgs = valid.Select(p => p.Avg).ToList();
        return new PeriodStats(valid.Sum(p => p.Avg * p.N) / valid.Sum(p => p.N), Baselines.Percentile(avgs, 0.5), Baselines.Percentile(avgs, 0.95),
            valid.Max(p => p.Max), valid.Count);
    }
}

/// <summary>"This week vs last week" / "before vs after update": one row per series present in either period.</summary>
public sealed record PeriodComparisonRow(string Key, string? Instance, PeriodStats? A, PeriodStats? B)
{
    /// <summary>Relative change of the average from A to B (0.25 = +25 %); null when either side is missing or A is 0.</summary>
    public double? AvgChange => A is { } a && B is { } b && a.Avg != 0 ? (b.Avg - a.Avg) / Math.Abs(a.Avg) : null;

    /// <summary>Same for p95.</summary>
    public double? P95Change => A is { } a && B is { } b && a.P95 != 0 ? (b.P95 - a.P95) / Math.Abs(a.P95) : null;
}

public static class PeriodCompare
{
    /// <summary>Two named periods; <c>ThisWeekVsLastWeek</c> ends at <paramref name="nowUtc"/>.</summary>
    public static ((DateTimeOffset From, DateTimeOffset To) A, (DateTimeOffset From, DateTimeOffset To) B) ThisWeekVsLastWeek(DateTimeOffset nowUtc) =>
        ((nowUtc.AddDays(-14), nowUtc.AddDays(-7)), (nowUtc.AddDays(-7), nowUtc));

    /// <summary>Rows ordered by the largest absolute change of the average first (unknown changes last).</summary>
    public static IReadOnlyList<PeriodComparisonRow> Rows(IEnumerable<PeriodComparisonRow> rows) =>
        rows.OrderByDescending(r => r.AvgChange is { } c ? Math.Abs(c) : -1).ThenBy(r => r.Key, StringComparer.Ordinal).ThenBy(r => r.Instance, StringComparer.Ordinal).ToList();
}
