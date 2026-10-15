namespace RSM.Parsers.Sql;

/// <summary>One row of <c>sys.dm_os_performance_counters</c> (catalog key <c>sql.perf.counters</c>).</summary>
public sealed record PerfCounterRow(string ObjectName, string CounterName, string InstanceName, long RawValue, int CounterType);

/// <summary>All counter rows read at one moment, plus the server start time (to detect restarts between samples).</summary>
public sealed record PerfCounterSnapshot(DateTimeOffset AtUtc, DateTime? ServerStartTime, IReadOnlyList<PerfCounterRow> Rows);

/// <summary>A computed counter value with its RSM metric key; <see cref="Instance"/> is a database or NUMA node.</summary>
public sealed record PerfCounterValue(string Key, string? Instance, double Value);

/// <summary>
/// Turns raw counter rows into values (SQL_CATALOG.md §3): per-second counters (cntr_type 272696576) become
/// Δvalue/Δt between two samples, raw counters (65792) are used as is, fractions (537003264) are divided by their
/// base (1073939712) and averages (1073874176) by Δbase. A restart or counter reset yields no rate for that sample.
/// </summary>
public static class PerfCounterMath
{
    public const int PerSecond = 272696576;
    public const int Raw = 65792;
    public const int RawLarge = 65536;
    public const int Fraction = 537003264;
    public const int AverageBulk = 1073874176;
    public const int Base = 1073939712;

    private static readonly Dictionary<(string Object, string Counter), (string Key, double Scale, bool PerInstance)> Map = new()
    {
        [("Buffer Manager", "Page life expectancy")] = ("sql.perf.ple_s", 1, false),
        [("Buffer Node", "Page life expectancy")] = ("sql.perf.ple_node_s", 1, true),
        [("Buffer Manager", "Lazy writes/sec")] = ("sql.perf.lazy_writes_sec", 1, false),
        [("Buffer Manager", "Free list stalls/sec")] = ("sql.perf.free_list_stalls_sec", 1, false),
        [("Buffer Manager", "Checkpoint pages/sec")] = ("sql.perf.checkpoint_pages_sec", 1, false),
        [("SQL Statistics", "Batch Requests/sec")] = ("sql.perf.batch_requests_sec", 1, false),
        [("SQL Statistics", "SQL Compilations/sec")] = ("sql.perf.compilations_sec", 1, false),
        [("SQL Statistics", "SQL Re-Compilations/sec")] = ("sql.perf.recompilations_sec", 1, false),
        [("Memory Manager", "Memory Grants Pending")] = ("sql.perf.memory_grants_pending", 1, false),
        [("Memory Manager", "Memory Grants Outstanding")] = ("sql.perf.memory_grants_outstanding", 1, false),
        [("Memory Manager", "Target Server Memory (KB)")] = ("sql.perf.target_memory_mb", 1.0 / 1024, false),
        [("Memory Manager", "Total Server Memory (KB)")] = ("sql.perf.total_memory_mb", 1.0 / 1024, false),
        [("General Statistics", "User Connections")] = ("sql.perf.user_connections", 1, false),
        [("General Statistics", "Processes blocked")] = ("sql.perf.processes_blocked", 1, false),
        [("Locks", "Lock Waits/sec")] = ("sql.perf.lock_waits_sec", 1, false),
        [("Locks", "Lock Timeouts/sec")] = ("sql.perf.lock_timeouts_sec", 1, false),
        [("Locks", "Number of Deadlocks/sec")] = ("sql.perf.deadlocks_sec", 1, false),
        [("Access Methods", "Full Scans/sec")] = ("sql.perf.full_scans_sec", 1, false),
        [("Access Methods", "Page Splits/sec")] = ("sql.perf.page_splits_sec", 1, false),
        [("Databases", "Log Flushes/sec")] = ("sql.perf.log_flushes_sec", 1, true),
        [("Databases", "Log Flush Wait Time")] = ("sql.perf.log_flush_wait_ms_sec", 1, true),
        [("Databases", "Transactions/sec")] = ("sql.perf.transactions_sec", 1, true),
        [("Databases", "Log Growths")] = ("sql.perf.log_growths", 1, true),
        [("Databases", "Log Shrinks")] = ("sql.perf.log_shrinks", 1, true),
        [("SQL Errors", "Errors/sec")] = ("sql.perf.errors_sec", 1, false),
    };

    /// <summary>Every metric key this class can produce (for the metric catalogue and UI).</summary>
    public static IReadOnlyCollection<string> Keys { get; } = Map.Values.Select(v => v.Key).Distinct().ToList();

    /// <summary>"SQLServer:Buffer Manager" / "MSSQL$RK:Buffer Manager" → "Buffer Manager".</summary>
    public static string ObjectKind(string objectName)
    {
        var colon = objectName.IndexOf(':', StringComparison.Ordinal);
        return (colon >= 0 ? objectName[(colon + 1)..] : objectName).Trim();
    }

    public static IReadOnlyList<PerfCounterValue> Compute(PerfCounterSnapshot? previous, PerfCounterSnapshot current)
    {
        var restarted = previous is null || previous.ServerStartTime != current.ServerStartTime;
        var seconds = previous is null ? 0 : (current.AtUtc - previous.AtUtc).TotalSeconds;
        var before = restarted || seconds <= 0
            ? new Dictionary<(string, string, string), PerfCounterRow>()
            : previous!.Rows.GroupBy(Id).ToDictionary(g => g.Key, g => g.First());
        var bases = current.Rows.Where(r => r.CounterType == Base).GroupBy(Id).ToDictionary(g => g.Key, g => g.First());

        var result = new List<PerfCounterValue>();
        foreach (var row in current.Rows)
        {
            if (!Map.TryGetValue((ObjectKind(row.ObjectName), row.CounterName), out var target))
            {
                continue;
            }

            var instance = target.PerInstance ? NormalizeInstance(row.InstanceName) : null;
            double? value = row.CounterType switch
            {
                Raw or RawLarge => row.RawValue,
                PerSecond when before.TryGetValue(Id(row), out var prev) && row.RawValue >= prev.RawValue => (row.RawValue - prev.RawValue) / seconds,
                Fraction when bases.TryGetValue(BaseId(row), out var b) && b.RawValue > 0 => 100.0 * row.RawValue / b.RawValue,
                AverageBulk when before.TryGetValue(Id(row), out var prev)
                                 && bases.TryGetValue(BaseId(row), out var b)
                                 && before.TryGetValue(BaseId(row), out var prevBase)
                                 && b.RawValue > prevBase.RawValue && row.RawValue >= prev.RawValue
                    => (double)(row.RawValue - prev.RawValue) / (b.RawValue - prevBase.RawValue),
                _ => null,
            };
            if (value is { } v)
            {
                result.Add(new PerfCounterValue(target.Key, instance, v * target.Scale));
            }
        }

        return result;
    }

    private static string NormalizeInstance(string instance) => instance.Trim() switch
    {
        "" => "_Total",
        var s => s,
    };

    private static (string, string, string) Id(PerfCounterRow r) =>
        (ObjectKind(r.ObjectName).ToUpperInvariant(), r.CounterName.Trim().ToUpperInvariant(), r.InstanceName.Trim().ToUpperInvariant());

    // The base row of "X" is named "X base" / "X Base" depending on the version.
    private static (string, string, string) BaseId(PerfCounterRow r) =>
        (ObjectKind(r.ObjectName).ToUpperInvariant(), (r.CounterName.Trim() + " base").ToUpperInvariant(), r.InstanceName.Trim().ToUpperInvariant());
}
