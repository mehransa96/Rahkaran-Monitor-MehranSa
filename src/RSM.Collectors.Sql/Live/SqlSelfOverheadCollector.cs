using RSM.Core.Model;
using RSM.Safety.Sql;

namespace RSM.Collectors.Sql.Live;

/// <summary>Per-node state of <see cref="SqlSelfOverheadCollector"/>.</summary>
public sealed class OverheadState
{
    internal object Gate { get; } = new();

    public SqlSelfOverheadCollector.OverheadRow? Previous { get; internal set; }
}

/// <summary>
/// RSM's own load on the SQL Server (P2 acceptance: &lt; 2 % CPU): CPU time of the sessions named
/// <c>RSM-ReadOnlyMonitor</c> over server time × cores, plus the round-trip time of a trivial query.
/// </summary>
public sealed class SqlSelfOverheadCollector(SafeSqlExecutor executor) : SqlLiveCollectorBase<OverheadState>(executor)
{
    public override string Id => "rsm.overhead.sql";

    public override TimeSpan DefaultInterval => TimeSpan.FromSeconds(30);

    protected override async Task CollectCoreAsync(INodeContext node, CollectContext context, SqlQueryRunner runner, OverheadState state, LiveOutput output)
    {
        await runner.QueryAsync<RttRow>("cap.rtt").ConfigureAwait(false);
        var rtt = runner.Outcomes.LastOrDefault(o => o.Key == "cap.rtt" && o.Status == SqlQueryStatus.Ok)?.Duration;
        var row = (await runner.QueryAsync<OverheadRow>("cap.self_overhead").ConfigureAwait(false))?.FirstOrDefault();
        Apply(state, row, rtt, Clock.GetUtcNow(), output);
    }

    internal static void Apply(OverheadState state, OverheadRow? row, TimeSpan? rtt, DateTimeOffset at, LiveOutput output)
    {
        if (rtt is { } r)
        {
            output.Fact("rsm.sql_rtt_ms", r.TotalMilliseconds);
            output.Sample("rsm.sql_rtt_ms", null, r.TotalMilliseconds, at);
        }

        if (row is null)
        {
            return;
        }

        output.Fact("rsm.sql_sessions", row.Sessions);
        output.Fact("rsm.sql_memory_kb", row.MemoryKb);
        OverheadRow? previous;
        lock (state.Gate)
        {
            previous = state.Previous;
            state.Previous = row;
        }

        // Pooled sessions keep cumulative cpu_time; a pool trim lowers the sum → skip that interval and re-base.
        if (previous is not null && row.MsTicks > previous.MsTicks && row.CpuMs >= previous.CpuMs && row.CpuCount > 0)
        {
            var pct = 100d * (row.CpuMs - previous.CpuMs) / ((row.MsTicks - previous.MsTicks) * (double)row.CpuCount);
            output.Fact("rsm.sql_cpu_pct", pct);
            output.Sample("rsm.sql_cpu_pct", null, pct, at);
        }
    }

    public sealed class RttRow
    {
        public int One { get; set; }
    }

    public sealed class OverheadRow
    {
        public int Sessions { get; set; }

        public long CpuMs { get; set; }

        public long Reads { get; set; }

        public long MemoryKb { get; set; }

        public int CpuCount { get; set; }

        public long MsTicks { get; set; }
    }
}
