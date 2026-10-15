using RSM.Core.Model;
using RSM.Parsers.Sql;
using RSM.Safety.Sql;

namespace RSM.Collectors.Sql.Live;

/// <summary>Per-node state of <see cref="SqlCpuCollector"/>: the newest ring-buffer point already emitted as a sample.</summary>
public sealed class CpuState
{
    internal object Gate { get; } = new();

    public DateTimeOffset? LastEmittedUtc { get; internal set; }
}

/// <summary>
/// §7.4 CPU from the scheduler-monitor ring buffer (one point per minute, ~4 h of history on the first read):
/// SQL CPU vs everything else on the box — "is it SQL, or IIS/the app on the same server?".
/// </summary>
public sealed class SqlCpuCollector(SafeSqlExecutor executor) : SqlLiveCollectorBase<CpuState>(executor)
{
    /// <summary>Points needed for "sustained" facts (one per minute).</summary>
    public const int SustainedMinutes = 5;

    public override string Id => "sql.cpu";

    public override TimeSpan DefaultInterval => TimeSpan.FromMinutes(1);

    protected override async Task CollectCoreAsync(INodeContext node, CollectContext context, SqlQueryRunner runner, CpuState state, LiveOutput output)
    {
        var rows = await runner.QueryAsync<RingBufferQueryRow>("sql.cpu.ring_buffer").ConfigureAwait(false);
        if (rows is not null)
        {
            var points = CpuRingBuffer.Parse(rows.Select(r => new RingBufferRow(r.RecordXml ?? string.Empty, r.RecordTicks, r.NowTicks)), Clock.GetUtcNow());
            Apply(state, points, Clock.GetUtcNow(), output);
        }
    }

    /// <summary>Emits only points newer than the last tick (the first read back-fills the chart with ~4 h).</summary>
    internal static void Apply(CpuState state, IReadOnlyList<CpuPoint> points, DateTimeOffset now, LiveOutput output)
    {
        output.Payload["points"] = points;
        if (points.Count == 0)
        {
            return;
        }

        lock (state.Gate)
        {
            foreach (var p in points.Where(p => state.LastEmittedUtc is null || p.AtUtc > state.LastEmittedUtc.Value.AddSeconds(5)))
            {
                output.Sample("sql.cpu.sql_pct", null, p.SqlPercent, p.AtUtc);
                output.Sample("sql.cpu.other_pct", null, p.OtherPercent, p.AtUtc);
                output.Sample("sql.cpu.idle_pct", null, p.IdlePercent, p.AtUtc);
            }

            state.LastEmittedUtc = points[^1].AtUtc;
        }

        var latest = points[^1];
        output.Fact("sql.cpu.sql_pct", latest.SqlPercent);
        output.Fact("sql.cpu.other_pct", latest.OtherPercent);

        // "Sustained": the last N one-minute points, all recent (a gap means the ring buffer was not read for a while).
        var recent = points.Where(p => p.AtUtc >= now.AddMinutes(-(SustainedMinutes + 1))).TakeLast(SustainedMinutes).ToList();
        if (recent.Count >= SustainedMinutes)
        {
            output.Fact("sql.cpu.sql_pct_min5", recent.Min(p => p.SqlPercent));
            output.Fact("sql.cpu.other_pct_min5", recent.Min(p => p.OtherPercent));
        }

        var hour = points.Where(p => p.AtUtc >= now.AddHours(-1)).ToList();
        if (hour.Count > 0)
        {
            output.Fact("sql.cpu.sql_pct_avg_1h", hour.Average(p => p.SqlPercent));
            output.Fact("sql.cpu.sql_pct_max_1h", hour.Max(p => p.SqlPercent));
        }
    }

    public sealed class RingBufferQueryRow
    {
        public string? RecordXml { get; set; }

        public long RecordTicks { get; set; }

        public long NowTicks { get; set; }
    }
}
