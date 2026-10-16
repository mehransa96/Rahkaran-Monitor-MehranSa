using System.Buffers.Binary;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using RSM.Core.Model;
using RSM.Safety.Files;

namespace RSM.Storage.History;

/// <summary>Resolution of a history read.</summary>
public enum HistoryTier
{
    Raw,
    OneMinute,
    FifteenMinutes,
}

/// <summary>One point read from history: a raw sample (N = 1) or a bucket aggregate.</summary>
public readonly record struct HistoryPoint(DateTimeOffset AtUtc, double Avg, double Min, double Max, int N);

public sealed record HistorySeries(int Id, string Key, string? Instance);

/// <summary>Retention per tier (spec §15 defaults: raw 24 h → 1-min 7 days → 15-min 90 days).</summary>
public sealed record HistoryRetention(TimeSpan Raw, TimeSpan OneMinute, TimeSpan FifteenMinutes)
{
    public static HistoryRetention Default { get; } = new(TimeSpan.FromHours(24), TimeSpan.FromDays(7), TimeSpan.FromDays(90));
}

/// <summary>
/// Local metric history without SQL (owner decision for P5): per node, append-only binary segments in the RSM data
/// folder, written only through <see cref="IDataFolder"/>.
/// <code>
/// history/&lt;site&gt;_&lt;node&gt;/series.json            series id ↔ (key, instance)
/// history/&lt;site&gt;_&lt;node&gt;/raw/yyyyMMddHH.bin      [ticks:int64][series:int32][value:double]            (UTC hour)
/// history/&lt;site&gt;_&lt;node&gt;/m1/yyyyMMdd.bin         [ticks][series][avg][min][max][n:int32] 1-min buckets  (UTC day)
/// history/&lt;site&gt;_&lt;node&gt;/m15/yyyyMMdd.bin        same layout, 15-min buckets                          (UTC day)
/// history/&lt;site&gt;_&lt;node&gt;/rollup.json           last raw hour / m1 day already rolled up
/// </code>
/// <see cref="MaintainAsync"/> rolls finished raw hours into 1-minute buckets and finished days into 15-minute
/// buckets, then deletes files older than the retention. Reads pick the tier from the span and add the part that is
/// not rolled up yet, so a chart never has a gap at "now".
/// </summary>
public sealed partial class HistoryStore(IDataFolder folder, HistoryRetention? retention = null)
{
    public const string Root = "history";
    private const int RawRecord = 20;
    private const int AggRecord = 40;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, Dictionary<(string Key, string? Instance), int>> _registry = new(StringComparer.Ordinal);

    public HistoryRetention Retention { get; } = retention ?? HistoryRetention.Default;

    public static string NodeFolder(string siteId, string nodeId) => $"{Root}/{Safe(siteId)}_{Safe(nodeId)}";

    private static string Safe(string id)
    {
        var s = UnsafeChars().Replace(id, "-");
        return s.Length == 0 ? "-" : s[..Math.Min(64, s.Length)];
    }

    private static string RawFile(string node, DateTimeOffset hour) => $"{node}/raw/{hour.UtcDateTime.ToString("yyyyMMddHH", CultureInfo.InvariantCulture)}.bin";

    private static string DayFile(string node, string tier, DateTimeOffset day) => $"{node}/{tier}/{day.UtcDateTime.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}.bin";

    private static DateTimeOffset Hour(DateTimeOffset t) => new(t.UtcDateTime.Year, t.UtcDateTime.Month, t.UtcDateTime.Day, t.UtcDateTime.Hour, 0, 0, TimeSpan.Zero);

    private static DateTimeOffset Day(DateTimeOffset t) => new(t.UtcDateTime.Date, TimeSpan.Zero);

    /// <summary>Appends samples (any order, finite values only) to the raw tier of a node.</summary>
    public async Task AppendAsync(string siteId, string nodeId, IReadOnlyList<MetricSample> samples, CancellationToken cancellationToken)
    {
        var valid = samples.Where(s => double.IsFinite(s.Value)).ToList();
        if (valid.Count == 0)
        {
            return;
        }

        var node = NodeFolder(siteId, nodeId);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var registry = await RegistryAsync(node, cancellationToken).ConfigureAwait(false);
            var added = false;
            foreach (var s in valid)
            {
                if (!registry.ContainsKey((s.Key, s.Instance)))
                {
                    registry[(s.Key, s.Instance)] = registry.Count + 1;
                    added = true;
                }
            }

            if (added)
            {
                var list = registry.Select(r => new HistorySeries(r.Value, r.Key.Key, r.Key.Instance)).OrderBy(r => r.Id).ToList();
                await folder.WriteAllTextAsync($"{node}/series.json", JsonSerializer.Serialize(list, Json), cancellationToken).ConfigureAwait(false);
            }

            foreach (var hour in valid.GroupBy(s => Hour(s.AtUtc)))
            {
                var bytes = new byte[RawRecord * hour.Count()];
                var offset = 0;
                foreach (var s in hour)
                {
                    BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(offset), s.AtUtc.UtcTicks);
                    BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset + 8), registry[(s.Key, s.Instance)]);
                    BinaryPrimitives.WriteDoubleLittleEndian(bytes.AsSpan(offset + 12), s.Value);
                    offset += RawRecord;
                }

                await folder.AppendAllBytesAsync(RawFile(node, hour.Key), bytes, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<HistorySeries>> SeriesAsync(string siteId, string nodeId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return (await RegistryAsync(NodeFolder(siteId, nodeId), cancellationToken).ConfigureAwait(false))
                .Select(r => new HistorySeries(r.Value, r.Key.Key, r.Key.Instance)).OrderBy(r => r.Key, StringComparer.Ordinal).ThenBy(r => r.Instance, StringComparer.Ordinal).ToList();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Nodes with history (folder names "site_node").</summary>
    public IReadOnlyList<string> NodeFolders() => folder.ListDirectories(Root);

    private async Task<Dictionary<(string Key, string? Instance), int>> RegistryAsync(string node, CancellationToken cancellationToken)
    {
        if (_registry.TryGetValue(node, out var registry))
        {
            return registry;
        }

        registry = [];
        if (folder.Exists($"{node}/series.json"))
        {
            try
            {
                foreach (var s in JsonSerializer.Deserialize<List<HistorySeries>>(await folder.ReadAllTextAsync($"{node}/series.json", cancellationToken).ConfigureAwait(false), Json) ?? [])
                {
                    registry[(s.Key, s.Instance)] = s.Id;
                }
            }
            catch (JsonException)
            {
                // A damaged index starts a new one; old segments stay readable only for ids that are re-registered.
            }
        }

        _registry[node] = registry;
        return registry;
    }

    private sealed record RollupState(DateTimeOffset? LastRawHour, DateTimeOffset? LastM1Day);

    /// <summary>Rolls up finished hours/days and applies the retention. Safe to call often (idempotent).</summary>
    public async Task MaintainAsync(DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var node in folder.ListDirectories(Root))
            {
                var nodePath = node.Replace('\\', '/');
                var state = await StateAsync(nodePath, cancellationToken).ConfigureAwait(false);

                // Finished raw hours → 1-minute buckets.
                var currentHour = Hour(nowUtc);
                foreach (var (hour, file) in Segments(nodePath, "raw", "yyyyMMddHH").Where(s => s.Start < currentHour && (state.LastRawHour is null || s.Start > state.LastRawHour)))
                {
                    var raw = ReadRaw(await folder.ReadAllBytesAsync(file, cancellationToken).ConfigureAwait(false));
                    foreach (var day in Aggregate(raw.Select(r => (r.Ticks, r.Series, r.Value, r.Value, r.Value, 1)), TimeSpan.FromMinutes(1)).GroupBy(a => Day(new DateTimeOffset(a.Ticks, TimeSpan.Zero))))
                    {
                        await folder.AppendAllBytesAsync(DayFile(nodePath, "m1", day.Key), WriteAgg(day), cancellationToken).ConfigureAwait(false);
                    }

                    state = state with { LastRawHour = hour };
                    await SaveStateAsync(nodePath, state, cancellationToken).ConfigureAwait(false);
                }

                // Finished 1-minute days → 15-minute buckets.
                var today = Day(nowUtc);
                foreach (var (day, file) in Segments(nodePath, "m1", "yyyyMMdd").Where(s => s.Start < today && (state.LastM1Day is null || s.Start > state.LastM1Day)))
                {
                    var m1 = ReadAgg(await folder.ReadAllBytesAsync(file, cancellationToken).ConfigureAwait(false));
                    await folder.WriteAllBytesAsync(DayFile(nodePath, "m15", day), WriteAgg(Aggregate(m1, TimeSpan.FromMinutes(15))), cancellationToken).ConfigureAwait(false);
                    state = state with { LastM1Day = day };
                    await SaveStateAsync(nodePath, state, cancellationToken).ConfigureAwait(false);
                }

                // Retention: a segment goes when its whole period is older than the tier's retention (raw only once rolled up).
                foreach (var (start, file) in Segments(nodePath, "raw", "yyyyMMddHH").Where(s => s.Start + TimeSpan.FromHours(1) < nowUtc - Retention.Raw && s.Start <= state.LastRawHour))
                {
                    folder.Delete(file);
                }

                foreach (var (start, file) in Segments(nodePath, "m1", "yyyyMMdd").Where(s => s.Start + TimeSpan.FromDays(1) < nowUtc - Retention.OneMinute && s.Start <= state.LastM1Day))
                {
                    folder.Delete(file);
                }

                foreach (var (start, file) in Segments(nodePath, "m15", "yyyyMMdd").Where(s => s.Start + TimeSpan.FromDays(1) < nowUtc - Retention.FifteenMinutes))
                {
                    folder.Delete(file);
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private Task SaveStateAsync(string node, RollupState state, CancellationToken cancellationToken) =>
        folder.WriteAllTextAsync($"{node}/rollup.json", JsonSerializer.Serialize(state, Json), cancellationToken);

    private async Task<RollupState> StateAsync(string node, CancellationToken cancellationToken)
    {
        if (!folder.Exists($"{node}/rollup.json"))
        {
            return new RollupState(null, null);
        }

        try
        {
            return JsonSerializer.Deserialize<RollupState>(await folder.ReadAllTextAsync($"{node}/rollup.json", cancellationToken).ConfigureAwait(false), Json) ?? new RollupState(null, null);
        }
        catch (JsonException)
        {
            return new RollupState(null, null);
        }
    }

    private List<(DateTimeOffset Start, string File)> Segments(string node, string tier, string format) =>
        folder.ListFiles($"{node}/{tier}", "*.bin")
            .Select(f => (Name: Path.GetFileNameWithoutExtension(f), File: f.Replace('\\', '/')))
            .Select(f => (Ok: DateTime.TryParseExact(f.Name, format, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t), Start: new DateTimeOffset(DateTime.SpecifyKind(t, DateTimeKind.Utc)), f.File))
            .Where(f => f.Ok)
            .Select(f => (f.Start, f.File))
            .OrderBy(f => f.Start)
            .ToList();

    /// <summary>Tier used when none is given: ≤ 6 h raw, ≤ 3 days 1-minute, else 15-minute.</summary>
    public static HistoryTier TierFor(TimeSpan span) => span <= TimeSpan.FromHours(6) ? HistoryTier.Raw : span <= TimeSpan.FromDays(3) ? HistoryTier.OneMinute : HistoryTier.FifteenMinutes;

    /// <summary>
    /// Reads one series between two instants. <paramref name="tier"/> null = <see cref="TierFor"/> the span. Points not
    /// rolled up yet are aggregated on the fly from the finer tier, so there is no gap at "now".
    /// </summary>
    public async Task<IReadOnlyList<HistoryPoint>> ReadAsync(string siteId, string nodeId, string key, string? instance, DateTimeOffset fromUtc, DateTimeOffset toUtc,
        HistoryTier? tier, CancellationToken cancellationToken)
    {
        var all = await ReadManyAsync(siteId, nodeId, s => s.Key == key && s.Instance == instance, fromUtc, toUtc, tier, cancellationToken).ConfigureAwait(false);
        return all.Count == 0 ? [] : all.Single().Value;
    }

    /// <summary>Reads every series matching <paramref name="filter"/> in one pass over the segment files.</summary>
    public async Task<IReadOnlyDictionary<HistorySeries, IReadOnlyList<HistoryPoint>>> ReadManyAsync(string siteId, string nodeId, Func<HistorySeries, bool> filter,
        DateTimeOffset fromUtc, DateTimeOffset toUtc, HistoryTier? tier, CancellationToken cancellationToken)
    {
        var node = NodeFolder(siteId, nodeId);
        var chosen = tier ?? TierFor(toUtc - fromUtc);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var series = (await RegistryAsync(node, cancellationToken).ConfigureAwait(false))
                .Select(r => new HistorySeries(r.Value, r.Key.Key, r.Key.Instance)).Where(filter).ToDictionary(s => s.Id);
            if (series.Count == 0)
            {
                return new Dictionary<HistorySeries, IReadOnlyList<HistoryPoint>>();
            }

            var ids = series.Keys.ToHashSet();
            var state = await StateAsync(node, cancellationToken).ConfigureAwait(false);
            var raw = await ReadRawRangeAsync(node, ids, fromUtc, toUtc, chosen == HistoryTier.Raw ? null : state.LastRawHour ?? DateTimeOffset.MinValue, cancellationToken).ConfigureAwait(false);
            IEnumerable<(long Ticks, int Series, double Avg, double Min, double Max, int N)> points;
            if (chosen == HistoryTier.Raw)
            {
                points = raw.Select(r => (r.Ticks, r.Series, r.Value, r.Value, r.Value, 1));
            }
            else
            {
                var m1 = (await ReadAggRangeAsync(node, "m1", ids, fromUtc, toUtc, chosen == HistoryTier.OneMinute ? null : state.LastM1Day ?? DateTimeOffset.MinValue, cancellationToken).ConfigureAwait(false))
                    .Concat(Aggregate(raw.Select(r => (r.Ticks, r.Series, r.Value, r.Value, r.Value, 1)), TimeSpan.FromMinutes(1)))
                    .ToList();
                points = chosen == HistoryTier.OneMinute
                    ? m1
                    : (await ReadAggRangeAsync(node, "m15", ids, fromUtc, toUtc, null, cancellationToken).ConfigureAwait(false)).Concat(Aggregate(m1, TimeSpan.FromMinutes(15)));
            }

            return points.Where(p => p.Ticks >= fromUtc.UtcTicks && p.Ticks <= toUtc.UtcTicks)
                .GroupBy(p => p.Series)
                .ToDictionary(g => series[g.Key], g => (IReadOnlyList<HistoryPoint>)g.GroupBy(p => p.Ticks).Select(t => t.First()).OrderBy(p => p.Ticks)
                    .Select(p => new HistoryPoint(new DateTimeOffset(p.Ticks, TimeSpan.Zero), p.Avg, p.Min, p.Max, p.N)).ToList());
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Raw records of some series; with <paramref name="after"/> only hours later than that (not rolled up).</summary>
    private async Task<List<(long Ticks, int Series, double Value)>> ReadRawRangeAsync(string node, HashSet<int> ids, DateTimeOffset from, DateTimeOffset to, DateTimeOffset? after,
        CancellationToken cancellationToken)
    {
        var result = new List<(long, int, double)>();
        foreach (var (start, file) in Segments(node, "raw", "yyyyMMddHH").Where(s => s.Start + TimeSpan.FromHours(1) > from && s.Start <= to && (after is null || s.Start > after)))
        {
            result.AddRange(ReadRaw(await folder.ReadAllBytesAsync(file, cancellationToken).ConfigureAwait(false)).Where(r => ids.Contains(r.Series) && r.Ticks >= from.UtcTicks && r.Ticks <= to.UtcTicks));
        }

        return result;
    }

    /// <summary>Bucket records of some series; with <paramref name="after"/> only days later than that (not rolled up).</summary>
    private async Task<List<(long Ticks, int Series, double Avg, double Min, double Max, int N)>> ReadAggRangeAsync(string node, string tier, HashSet<int> ids, DateTimeOffset from, DateTimeOffset to,
        DateTimeOffset? after, CancellationToken cancellationToken)
    {
        var result = new List<(long, int, double, double, double, int)>();
        foreach (var (start, file) in Segments(node, tier, "yyyyMMdd").Where(s => s.Start + TimeSpan.FromDays(1) > from && s.Start <= to && (after is null || s.Start > after)))
        {
            result.AddRange(ReadAgg(await folder.ReadAllBytesAsync(file, cancellationToken).ConfigureAwait(false)).Where(r => ids.Contains(r.Series)));
        }

        return result;
    }

    internal static List<(long Ticks, int Series, double Value)> ReadRaw(byte[] bytes)
    {
        var list = new List<(long, int, double)>(bytes.Length / RawRecord);
        for (var o = 0; o + RawRecord <= bytes.Length; o += RawRecord)
        {
            list.Add((BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(o)), BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(o + 8)), BinaryPrimitives.ReadDoubleLittleEndian(bytes.AsSpan(o + 12))));
        }

        return list;
    }

    internal static List<(long Ticks, int Series, double Avg, double Min, double Max, int N)> ReadAgg(byte[] bytes)
    {
        var list = new List<(long, int, double, double, double, int)>(bytes.Length / AggRecord);
        for (var o = 0; o + AggRecord <= bytes.Length; o += AggRecord)
        {
            var s = bytes.AsSpan(o);
            list.Add((BinaryPrimitives.ReadInt64LittleEndian(s), BinaryPrimitives.ReadInt32LittleEndian(s[8..]), BinaryPrimitives.ReadDoubleLittleEndian(s[12..]),
                BinaryPrimitives.ReadDoubleLittleEndian(s[20..]), BinaryPrimitives.ReadDoubleLittleEndian(s[28..]), BinaryPrimitives.ReadInt32LittleEndian(s[36..])));
        }

        return list;
    }

    private static byte[] WriteAgg(IEnumerable<(long Ticks, int Series, double Avg, double Min, double Max, int N)> items)
    {
        var list = items.ToList();
        var bytes = new byte[AggRecord * list.Count];
        for (var i = 0; i < list.Count; i++)
        {
            var s = bytes.AsSpan(i * AggRecord);
            var (ticks, series, avg, min, max, n) = list[i];
            BinaryPrimitives.WriteInt64LittleEndian(s, ticks);
            BinaryPrimitives.WriteInt32LittleEndian(s[8..], series);
            BinaryPrimitives.WriteDoubleLittleEndian(s[12..], avg);
            BinaryPrimitives.WriteDoubleLittleEndian(s[20..], min);
            BinaryPrimitives.WriteDoubleLittleEndian(s[28..], max);
            BinaryPrimitives.WriteInt32LittleEndian(s[36..], n);
        }

        return bytes;
    }

    /// <summary>Weighted re-aggregation into buckets of <paramref name="bucket"/> (avg weighted by N, min of mins, max of maxes).</summary>
    internal static List<(long Ticks, int Series, double Avg, double Min, double Max, int N)> Aggregate(IEnumerable<(long Ticks, int Series, double Avg, double Min, double Max, int N)> items, TimeSpan bucket) =>
        items.GroupBy(i => (Bucket: i.Ticks - (i.Ticks % bucket.Ticks), i.Series))
            .Select(g =>
            {
                var n = g.Sum(i => i.N);
                return (g.Key.Bucket, g.Key.Series, g.Sum(i => i.Avg * i.N) / n, g.Min(i => i.Min), g.Max(i => i.Max), n);
            })
            .OrderBy(a => a.Bucket).ThenBy(a => a.Series)
            .ToList();

    [GeneratedRegex("[^A-Za-z0-9_-]")]
    private static partial Regex UnsafeChars();
}
