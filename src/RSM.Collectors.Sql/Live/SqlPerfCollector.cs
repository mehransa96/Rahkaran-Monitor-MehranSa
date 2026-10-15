using System.Globalization;
using RSM.Collectors.Sql.Instance;
using RSM.Core.Model;
using RSM.Parsers.Sql;
using RSM.Safety.Sql;

namespace RSM.Collectors.Sql.Live;

/// <summary>Cumulative file I/O counters of one database (data files or log files summed).</summary>
public sealed record FileIoSample(string Database, long DataReads, long DataStallReadMs, long LogWrites, long LogStallWriteMs);

/// <summary>Everything the perf collector reads in one pass; cumulative parts are turned into deltas.</summary>
public sealed record PerfSample(
    DateTimeOffset AtUtc,
    PerfCounterSnapshot? Counters,
    IReadOnlyList<WaitRow>? Waits,
    IReadOnlyList<FileIoSample>? Files,
    IReadOnlyList<WaitingTaskRow>? WaitingTasks = null,
    SqlStorageCollector.PendingRow? Pending = null);

public sealed record WaitingTaskRow(int SessionId, string? WaitType, long WaitMs, int BlockingSessionId, string? ResourceDescription);

/// <summary>Per-node state of <see cref="SqlPerfCollector"/>: the previous cumulative sample and short value windows.</summary>
public sealed class PerfState
{
    public const int WindowSize = 12;

    internal object Gate { get; } = new();

    public PerfSample? Previous { get; internal set; }

    public List<double> GrantsPending { get; } = [];

    public List<double> Ple { get; } = [];

    public int Samples { get; internal set; }
}

/// <summary>
/// §7.3 counters, §7.5 waits and §7.14 I/O latency as interval deltas (every 15 s live). On the first tick (or after a
/// long pause) it takes a baseline, waits <see cref="FirstSampleGap"/> and samples again, so a one-shot diagnosis
/// also gets interval values. Thresholds live in the rules (RULES.md §3.5); this class only measures.
/// </summary>
public sealed class SqlPerfCollector(SafeSqlExecutor executor) : SqlLiveCollectorBase<PerfState>(executor)
{
    /// <summary>A previous sample older than this is not used for deltas (interval too long to mean anything).</summary>
    public static readonly TimeSpan MaxBaselineAge = TimeSpan.FromMinutes(10);

    public override string Id => "sql.perf";

    public override TimeSpan DefaultInterval => TimeSpan.FromSeconds(15);

    public TimeSpan FirstSampleGap { get; init; } = TimeSpan.FromSeconds(5);

    protected override async Task CollectCoreAsync(INodeContext node, CollectContext context, SqlQueryRunner runner, PerfState state, LiveOutput output)
    {
        var current = await SampleAsync(runner).ConfigureAwait(false);
        PerfSample? previous;
        lock (state.Gate)
        {
            previous = state.Previous;
        }

        if (current.Counters is not null && NeedsBaseline(previous, current.AtUtc) && FirstSampleGap > TimeSpan.Zero)
        {
            lock (state.Gate)
            {
                state.Previous = current;
            }

            await Task.Delay(FirstSampleGap, Clock, runner.CancellationToken).ConfigureAwait(false);
            current = await SampleAsync(runner).ConfigureAwait(false);
        }

        Apply(state, current, output);
    }

    internal static bool NeedsBaseline(PerfSample? previous, DateTimeOffset now) =>
        previous is null || now - previous.AtUtc > MaxBaselineAge || now - previous.AtUtc < TimeSpan.FromSeconds(1);

    private async Task<PerfSample> SampleAsync(SqlQueryRunner runner)
    {
        var counters = await runner.QueryAsync<CounterQueryRow>("sql.perf.counters").ConfigureAwait(false);
        var waits = await runner.QueryAsync<WaitQueryRow>("sql.waits.wait_stats").ConfigureAwait(false);
        var files = await runner.QueryAsync<SqlStorageCollector.FileStatRow>("sql.io.file_stats").ConfigureAwait(false);
        var waiting = await runner.QueryAsync<WaitingTaskQueryRow>("sql.waits.waiting_tasks").ConfigureAwait(false);
        var pending = (await runner.QueryAsync<SqlStorageCollector.PendingRow>("sql.io.pending").ConfigureAwait(false))?.FirstOrDefault();
        var at = Clock.GetUtcNow();
        return new PerfSample(
            at,
            counters is null ? null : new PerfCounterSnapshot(at, counters.FirstOrDefault()?.StartTime,
                counters.Select(c => new PerfCounterRow(c.ObjectName ?? string.Empty, c.CounterName ?? string.Empty, c.InstanceName ?? string.Empty, c.RawValue, c.CounterType)).ToList()),
            waits?.Select(w => new WaitRow(w.WaitType ?? string.Empty, w.WaitingTasks, w.WaitMs, w.SignalMs)).ToList(),
            files is null ? null : ToFileSamples(files),
            waiting?.Select(w => new WaitingTaskRow(w.SessionId, w.WaitType, w.WaitMs, w.BlockingSessionId, w.ResourceDescription)).ToList(),
            pending);
    }

    internal static IReadOnlyList<FileIoSample> ToFileSamples(IEnumerable<SqlStorageCollector.FileStatRow> rows) =>
        rows.GroupBy(r => r.DatabaseName ?? r.DatabaseId.ToString(CultureInfo.InvariantCulture), StringComparer.OrdinalIgnoreCase)
            .Select(g => new FileIoSample(
                g.Key,
                g.Where(f => f.TypeDesc == "ROWS").Sum(f => f.NumOfReads),
                g.Where(f => f.TypeDesc == "ROWS").Sum(f => f.IoStallReadMs),
                g.Where(f => f.TypeDesc == "LOG").Sum(f => f.NumOfWrites),
                g.Where(f => f.TypeDesc == "LOG").Sum(f => f.IoStallWriteMs)))
            .ToList();

    /// <summary>Pure analysis of one sample against the node's previous one (unit-tested with fixture rows).</summary>
    internal static void Apply(PerfState state, PerfSample current, LiveOutput output)
    {
        PerfSample? previous;
        lock (state.Gate)
        {
            previous = state.Previous;
            state.Previous = current with { WaitingTasks = null, Pending = null };
            state.Samples++;
        }

        var at = current.AtUtc;
        var usable = previous is not null && !NeedsBaseline(previous, at);
        var seconds = usable ? (at - previous!.AtUtc).TotalSeconds : 0;
        var restarted = usable && previous!.Counters?.ServerStartTime is { } s1 && current.Counters?.ServerStartTime is { } s2 && s1 != s2;
        output.Fact("sql.perf.interval_s", usable && !restarted ? seconds : null);

        if (current.Counters is { } counters)
        {
            ApplyCounters(state, usable && !restarted ? previous!.Counters : null, counters, output);
        }

        if (usable && !restarted && previous!.Waits is { } beforeWaits && current.Waits is { } nowWaits)
        {
            ApplyWaits(WaitStats.Interval(beforeWaits, nowWaits, seconds), at, output);
        }

        if (usable && !restarted && previous!.Files is { } beforeFiles && current.Files is { } nowFiles)
        {
            ApplyFiles(beforeFiles, nowFiles, at, output);
        }

        if (current.WaitingTasks is { } waiting)
        {
            var allocation = waiting.Count(w => w.WaitType?.StartsWith("PAGELATCH_", StringComparison.OrdinalIgnoreCase) == true && IsTempdbAllocationPage(w.ResourceDescription));
            output.Fact("tempdb.alloc_contention_waiters", allocation);
            output.Sample("sql.tempdb.alloc_contention_waiters", null, allocation, at);
            output.Payload["waitingTasks"] = waiting;
        }

        if (current.Pending is { } pending)
        {
            output.Fact("io.pending_count", pending.PendingCount);
            output.Fact("io.max_pending_ms", pending.MaxPendingMs);
            output.Sample("sql.io.pending_count", null, pending.PendingCount, at);
        }
    }

    private static void ApplyCounters(PerfState state, PerfCounterSnapshot? previous, PerfCounterSnapshot current, LiveOutput output)
    {
        var values = PerfCounterMath.Compute(previous, current);
        foreach (var value in values)
        {
            output.Sample(value.Key, value.Instance, value.Value, current.AtUtc);
            if (value.Instance is null)
            {
                output.Fact(value.Key, value.Value);
            }
        }

        double? Get(string key) => values.FirstOrDefault(v => v.Key == key && v.Instance is null)?.Value;
        lock (state.Gate)
        {
            if (Get("sql.perf.memory_grants_pending") is { } pending)
            {
                Window.Push(state.GrantsPending, pending, PerfState.WindowSize);
            }

            if (Get("sql.perf.ple_s") is { } ple)
            {
                Window.Push(state.Ple, ple, PerfState.WindowSize);
            }

            output.Fact("sql.perf.memory_grants_pending_min3", Window.MinOfLast(state.GrantsPending, 3));
            output.Fact("sql.perf.ple_max3", Window.MaxOfLast(state.Ple, 3));
        }

        // Absolute PLE fallback until an hour-of-week baseline exists (P5): 300 s per 4 GB of buffer pool (general guidance).
        if (Get("sql.perf.total_memory_mb") is { } totalMb)
        {
            output.Fact("sql.perf.ple_floor_s", 300 * Math.Max(1, totalMb / 4096));
        }

        if (Get("sql.perf.target_memory_mb") is { } target && Get("sql.perf.total_memory_mb") is { } total && target > 0)
        {
            output.Fact("sql.perf.total_to_target_pct", 100 * total / target);
        }
    }

    internal static void ApplyWaits(WaitInterval interval, DateTimeOffset at, LiveOutput output)
    {
        output.Fact("sql.waits.interval_s", interval.Seconds);
        output.Fact("sql.waits.ms_per_sec", interval.TotalWaitMs / interval.Seconds);
        output.Fact("sql.waits.signal_pct", interval.TotalWaitMs > 0 ? interval.SignalPercent : 0d);
        output.Fact("sql.waits.threadpool_ms", interval.Waits.Where(w => w.Category == WaitCategory.Worker).Sum(w => w.WaitMs));
        output.Fact("sql.waits.top", interval.Waits.Take(5).Select(w => (object?)string.Create(CultureInfo.InvariantCulture, $"{w.WaitType} {w.WaitMs} ms")).ToList());
        output.Sample("sql.waits.ms_per_sec", "_Total", interval.TotalWaitMs / interval.Seconds, at);
        output.Sample("sql.waits.signal_pct", null, interval.TotalWaitMs > 0 ? interval.SignalPercent : 0, at);
        foreach (var category in interval.Categories)
        {
            output.Sample("sql.waits.ms_per_sec", category.Category.ToString(), category.WaitMs / interval.Seconds, at);
        }

        var dominant = interval.Dominant();
        output.Fact("sql.waits.has_dominant", dominant is not null);
        if (dominant is not null)
        {
            var explain = WaitStats.Explain(dominant.Category);
            output.Fact("sql.waits.dominant", dominant.Category.ToString());
            output.Fact("sql.waits.dominant_pct", dominant.Percent);
            output.Fact("sql.waits.dominant_explain_fa", explain.Fa);
            output.Fact("sql.waits.dominant_explain_en", explain.En);
        }

        output.Payload["waits"] = interval;
    }

    internal static void ApplyFiles(IReadOnlyList<FileIoSample> previous, IReadOnlyList<FileIoSample> current, DateTimeOffset at, LiveOutput output)
    {
        var before = previous.ToDictionary(f => f.Database, StringComparer.OrdinalIgnoreCase);
        foreach (var now in current)
        {
            if (!before.TryGetValue(now.Database, out var prev)
                || now.DataReads < prev.DataReads || now.LogWrites < prev.LogWrites
                || now.DataStallReadMs < prev.DataStallReadMs || now.LogStallWriteMs < prev.LogStallWriteMs)
            {
                continue; // new database or counters reset (offline/online, restart)
            }

            var key = $"db[{now.Database}]";
            var reads = now.DataReads - prev.DataReads;
            var writes = now.LogWrites - prev.LogWrites;
            output.Fact($"{key}.io.interval_reads", reads);
            output.Fact($"{key}.io.interval_log_writes", writes);
            if (reads > 0)
            {
                var ms = (double)(now.DataStallReadMs - prev.DataStallReadMs) / reads;
                output.Fact($"{key}.io.interval_read_ms", ms);
                output.Sample("sql.io.data_read_ms", now.Database, ms, at);
            }

            if (writes > 0)
            {
                var ms = (double)(now.LogStallWriteMs - prev.LogStallWriteMs) / writes;
                output.Fact($"{key}.io.interval_log_write_ms", ms);
                output.Sample("sql.io.log_write_ms", now.Database, ms, at);
            }
        }
    }

    /// <summary>
    /// "2:1:1" style page ids in tempdb (database 2) that are allocation pages: PFS (page 1 and every 8088 pages),
    /// GAM (2, then every 511232) and SGAM (3, then every 511232).
    /// </summary>
    internal static bool IsTempdbAllocationPage(string? resource)
    {
        if (string.IsNullOrEmpty(resource))
        {
            return false;
        }

        var parts = resource.Split(':', ' ', '(').Where(p => p.Length > 0).ToArray();
        if (parts.Length < 3 || parts[0] != "2" || !long.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var page))
        {
            return false;
        }

        return page == 1 || page % 8088 == 0 || page % 511232 is 2 or 3;
    }

    public sealed class CounterQueryRow
    {
        public string? ObjectName { get; set; }

        public string? CounterName { get; set; }

        public string? InstanceName { get; set; }

        public long RawValue { get; set; }

        public int CounterType { get; set; }

        public DateTime? StartTime { get; set; }
    }

    public sealed class WaitQueryRow
    {
        public string? WaitType { get; set; }

        public long WaitingTasks { get; set; }

        public long WaitMs { get; set; }

        public long SignalMs { get; set; }
    }

    public sealed class WaitingTaskQueryRow
    {
        public int SessionId { get; set; }

        public string? WaitType { get; set; }

        public long WaitMs { get; set; }

        public int BlockingSessionId { get; set; }

        public string? ResourceDescription { get; set; }
    }
}
