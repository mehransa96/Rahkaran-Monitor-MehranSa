using System.Text.Json;
using RSM.Core.History;
using RSM.Safety.Files;

namespace RSM.Storage.History;

/// <summary>Baselines of one node and when they were computed.</summary>
public sealed record NodeBaselines(DateTimeOffset ComputedUtc, int Weeks, string TimeZoneId, IReadOnlyList<SeriesBaseline> Series)
{
    public SeriesBaseline? For(string key, string? instance) => Series.FirstOrDefault(s => s.Key == key && s.Instance == instance);
}

/// <summary>
/// Computes the §15 baselines from the 15-minute history tier (last <see cref="Weeks"/> weeks, default 4) and keeps
/// them next to the node's history as <c>baseline.json</c>; recomputed when older than a day.
/// </summary>
public sealed class BaselineStore(HistoryStore history, IDataFolder folder)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public int Weeks { get; init; } = 4;

    public TimeSpan MaxAge { get; init; } = TimeSpan.FromDays(1);

    private sealed record SeriesDto(string Key, string? Instance, double?[] Median, double?[] P95, int[] N);

    private sealed record FileDto(DateTimeOffset ComputedUtc, int Weeks, string TimeZoneId, List<SeriesDto> Series);

    private static string FileOf(string siteId, string nodeId) => $"{HistoryStore.NodeFolder(siteId, nodeId)}/baseline.json";

    public async Task<NodeBaselines> ComputeAsync(string siteId, string nodeId, DateTimeOffset nowUtc, TimeZoneInfo zone, CancellationToken cancellationToken)
    {
        var data = await history.ReadManyAsync(siteId, nodeId, _ => true, nowUtc.AddDays(-7 * Weeks), nowUtc, HistoryTier.FifteenMinutes, cancellationToken).ConfigureAwait(false);
        var series = data
            .Select(d => Baselines.Build(d.Key.Key, d.Key.Instance, d.Value.Select(p => (p.AtUtc, p.Avg)), zone))
            .Where(b => b.Slots.Any(s => s is not null))
            .OrderBy(b => b.Key, StringComparer.Ordinal).ThenBy(b => b.Instance, StringComparer.Ordinal)
            .ToList();
        var result = new NodeBaselines(nowUtc, Weeks, zone.Id, series);
        var dto = new FileDto(nowUtc, Weeks, zone.Id, series.Select(b => new SeriesDto(b.Key, b.Instance,
            b.Slots.Select(s => s?.Median).ToArray(), b.Slots.Select(s => s?.P95).ToArray(), b.Slots.Select(s => s?.N ?? 0).ToArray())).ToList());
        await folder.WriteAllTextAsync(FileOf(siteId, nodeId), JsonSerializer.Serialize(dto, Json), cancellationToken).ConfigureAwait(false);
        return result;
    }

    /// <summary>The stored baselines, or null when none were computed yet (or the file is damaged).</summary>
    public async Task<NodeBaselines?> LoadAsync(string siteId, string nodeId, CancellationToken cancellationToken)
    {
        var path = FileOf(siteId, nodeId);
        if (!folder.Exists(path))
        {
            return null;
        }

        try
        {
            var dto = JsonSerializer.Deserialize<FileDto>(await folder.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false), Json);
            return dto is null
                ? null
                : new NodeBaselines(dto.ComputedUtc, dto.Weeks, dto.TimeZoneId, dto.Series
                    .Where(s => s.Median.Length == SeriesBaseline.SlotCount && s.P95.Length == SeriesBaseline.SlotCount && s.N.Length == SeriesBaseline.SlotCount)
                    .Select(s => new SeriesBaseline(s.Key, s.Instance, Enumerable.Range(0, SeriesBaseline.SlotCount)
                        .Select(i => s.Median[i] is { } m && s.P95[i] is { } p ? new BaselineSlot(m, p, s.N[i]) : (BaselineSlot?)null).ToList()))
                    .ToList());
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Loads the baselines and recomputes them first when missing, older than <see cref="MaxAge"/> or built for another time zone.</summary>
    public async Task<NodeBaselines> RefreshIfStaleAsync(string siteId, string nodeId, DateTimeOffset nowUtc, TimeZoneInfo zone, CancellationToken cancellationToken)
    {
        var current = await LoadAsync(siteId, nodeId, cancellationToken).ConfigureAwait(false);
        return current is not null && nowUtc - current.ComputedUtc < MaxAge && current.TimeZoneId == zone.Id && current.Weeks == Weeks
            ? current
            : await ComputeAsync(siteId, nodeId, nowUtc, zone, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Replays a stored period against the baselines (history view "anomalies" band and the acceptance test): every
    /// flagged run per series, ordered by start.
    /// </summary>
    public async Task<IReadOnlyList<Anomaly>> AnomaliesAsync(string siteId, string nodeId, DateTimeOffset fromUtc, DateTimeOffset toUtc, TimeZoneInfo zone, AnomalyOptions? options,
        CancellationToken cancellationToken)
    {
        if (await LoadAsync(siteId, nodeId, cancellationToken).ConfigureAwait(false) is not { } baselines || baselines.Series.Count == 0)
        {
            return [];
        }

        var keys = baselines.Series.Select(b => (b.Key, b.Instance)).ToHashSet();
        var data = await history.ReadManyAsync(siteId, nodeId, s => keys.Contains((s.Key, s.Instance)), fromUtc, toUtc, null, cancellationToken).ConfigureAwait(false);
        return data
            .SelectMany(d => AnomalyDetector.Detect(baselines.For(d.Key.Key, d.Key.Instance)!, d.Value.Select(p => (p.AtUtc, p.Avg)), zone, options))
            .OrderBy(a => a.FromUtc).ThenBy(a => a.Key, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Compares two periods of every stored series of the node (spec §15 "compare periods"); periods are half-open [From, To).</summary>
    public async Task<IReadOnlyList<PeriodComparisonRow>> CompareAsync(string siteId, string nodeId, (DateTimeOffset From, DateTimeOffset To) a, (DateTimeOffset From, DateTimeOffset To) b,
        CancellationToken cancellationToken)
    {
        var tier = HistoryStore.TierFor(TimeSpan.FromTicks(Math.Max((a.To - a.From).Ticks, (b.To - b.From).Ticks)));
        var left = await history.ReadManyAsync(siteId, nodeId, _ => true, a.From, a.To.AddTicks(-1), tier, cancellationToken).ConfigureAwait(false);
        var right = await history.ReadManyAsync(siteId, nodeId, _ => true, b.From, b.To.AddTicks(-1), tier, cancellationToken).ConfigureAwait(false);
        return PeriodCompare.Rows(left.Keys.Concat(right.Keys).Select(s => (s.Key, s.Instance)).Distinct()
            .Select(k => new PeriodComparisonRow(k.Key, k.Instance, Stats(left, k), Stats(right, k))));

        static PeriodStats? Stats(IReadOnlyDictionary<HistorySeries, IReadOnlyList<HistoryPoint>> data, (string Key, string? Instance) k) =>
            data.FirstOrDefault(d => d.Key.Key == k.Key && d.Key.Instance == k.Instance).Value is { } points
                ? PeriodStats.Of(points.Select(p => (p.Avg, p.Max, p.N)).ToList())
                : null;
    }
}
