using RSM.Core.Model;

namespace RSM.Parsers.Sql;

public sealed record WaitRow(string WaitType, long WaitingTasks, long WaitMs, long SignalMs);

public enum WaitCategory
{
    Cpu,
    Parallelism,
    Worker,
    IoData,
    IoLog,
    Locking,
    Latch,
    Memory,
    Network,
    Backup,
    Other,
}

public sealed record WaitDelta(string WaitType, WaitCategory Category, long Tasks, long WaitMs, long SignalMs);

public sealed record CategoryShare(WaitCategory Category, long WaitMs, double Percent);

/// <summary>Waits accumulated between two samples, excluding idle waits.</summary>
public sealed record WaitInterval(
    double Seconds,
    long TotalWaitMs,
    long TotalSignalMs,
    double SignalPercent,
    IReadOnlyList<WaitDelta> Waits,
    IReadOnlyList<CategoryShare> Categories)
{
    /// <summary>The category with the largest share, when it holds at least 40 % of a non-trivial interval.</summary>
    public CategoryShare? Dominant(double minWaitMsPerSecond = 100, double minPercent = 40) =>
        Seconds > 0 && TotalWaitMs / Seconds >= minWaitMsPerSecond && Categories.FirstOrDefault() is { } top && top.Percent >= minPercent ? top : null;
}

/// <summary>
/// Wait-statistics interval maths (SQL_CATALOG.md §5): Δ of cumulative <c>sys.dm_os_wait_stats</c>, idle waits removed
/// with the same list the query uses (general guidance), grouped into categories with fa/en explanations.
/// </summary>
public static class WaitStats
{
    private static readonly string[] BenignPrefixes = ["SLEEP_", "BROKER_", "XE_", "QDS_", "HADR_", "PARALLEL_REDO_", "PWAIT_"];

    private static readonly HashSet<string> Benign = new(StringComparer.OrdinalIgnoreCase)
    {
        "CHECKPOINT_QUEUE", "CHKPT", "CLR_AUTO_EVENT", "CLR_MANUAL_EVENT", "CLR_SEMAPHORE", "DBMIRROR_DBM_EVENT",
        "DBMIRROR_EVENTS_QUEUE", "DBMIRROR_WORKER_QUEUE", "DBMIRRORING_CMD", "DIRTY_PAGE_POLL", "DISPATCHER_QUEUE_SEMAPHORE",
        "EXECSYNC", "FSAGENT", "FT_IFTS_SCHEDULER_IDLE_WAIT", "FT_IFTSHC_MUTEX", "KSOURCE_WAKEUP", "LAZYWRITER_SLEEP",
        "LOGMGR_QUEUE", "MEMORY_ALLOCATION_EXT", "ONDEMAND_TASK_QUEUE", "PREEMPTIVE_XE_GETTARGETSTATE",
        "PREEMPTIVE_OS_FLUSHFILEBUFFERS", "REDO_THREAD_PENDING_WORK", "REQUEST_FOR_DEADLOCK_SEARCH", "RESOURCE_QUEUE",
        "SERVER_IDLE_CHECK", "SNI_HTTP_ACCEPT", "SOS_WORK_DISPATCHER", "SP_SERVER_DIAGNOSTICS_SLEEP", "SQLTRACE_BUFFER_FLUSH",
        "SQLTRACE_INCREMENTAL_FLUSH_SLEEP", "SQLTRACE_WAIT_ENTRIES", "VDI_CLIENT_OTHER", "WAIT_FOR_RESULTS", "WAITFOR",
        "WAITFOR_TASKSHUTDOWN", "WAIT_XTP_RECOVERY", "WAIT_XTP_HOST_WAIT", "WAIT_XTP_OFFLINE_CKPT_NEW_LOG", "WAIT_XTP_CKPT_CLOSE",
    };

    public static bool IsBenign(string waitType) =>
        Benign.Contains(waitType) || BenignPrefixes.Any(p => waitType.StartsWith(p, StringComparison.OrdinalIgnoreCase));

    public static WaitCategory Categorize(string waitType)
    {
        var w = waitType.ToUpperInvariant();
        return w switch
        {
            "SOS_SCHEDULER_YIELD" => WaitCategory.Cpu,
            "THREADPOOL" => WaitCategory.Worker,
            "CXPACKET" or "CXCONSUMER" or "CXSYNC_PORT" or "CXSYNC_CONSUMER" or "EXCHANGE" => WaitCategory.Parallelism,
            "WRITELOG" or "LOGBUFFER" or "LOG_RATE_GOVERNOR" => WaitCategory.IoLog,
            "IO_COMPLETION" or "ASYNC_IO_COMPLETION" or "WRITE_COMPLETION" => WaitCategory.IoData,
            "RESOURCE_SEMAPHORE" or "RESOURCE_SEMAPHORE_QUERY_COMPILE" or "CMEMTHREAD" => WaitCategory.Memory,
            "ASYNC_NETWORK_IO" or "NET_WAITFOR_PACKET" => WaitCategory.Network,
            _ when w.StartsWith("PAGEIOLATCH_", StringComparison.Ordinal) => WaitCategory.IoData,
            _ when w.StartsWith("LCK_M_", StringComparison.Ordinal) => WaitCategory.Locking,
            _ when w.StartsWith("PAGELATCH_", StringComparison.Ordinal) || w.StartsWith("LATCH_", StringComparison.Ordinal) => WaitCategory.Latch,
            _ when w.StartsWith("BACKUP", StringComparison.Ordinal) => WaitCategory.Backup,
            _ => WaitCategory.Other,
        };
    }

    /// <summary>Δ between two cumulative samples; a negative Δ (restart, DBCC SQLPERF clear) drops that wait type.</summary>
    public static WaitInterval Interval(IReadOnlyList<WaitRow> previous, IReadOnlyList<WaitRow> current, double seconds)
    {
        var before = previous.GroupBy(r => r.WaitType, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var deltas = new List<WaitDelta>();
        foreach (var row in current)
        {
            if (IsBenign(row.WaitType))
            {
                continue;
            }

            var prev = before.GetValueOrDefault(row.WaitType) ?? new WaitRow(row.WaitType, 0, 0, 0);
            var ms = row.WaitMs - prev.WaitMs;
            if (ms <= 0 || row.SignalMs < prev.SignalMs)
            {
                continue;
            }

            deltas.Add(new WaitDelta(row.WaitType, Categorize(row.WaitType), Math.Max(0, row.WaitingTasks - prev.WaitingTasks), ms, row.SignalMs - prev.SignalMs));
        }

        deltas.Sort((a, b) => b.WaitMs.CompareTo(a.WaitMs));
        var total = deltas.Sum(d => d.WaitMs);
        var signal = deltas.Sum(d => d.SignalMs);
        var categories = deltas.GroupBy(d => d.Category)
            .Select(g => new CategoryShare(g.Key, g.Sum(d => d.WaitMs), total == 0 ? 0 : 100.0 * g.Sum(d => d.WaitMs) / total))
            .OrderByDescending(c => c.WaitMs)
            .ToList();
        return new WaitInterval(seconds, total, signal, total == 0 ? 0 : 100.0 * signal / total, deltas, categories);
    }

    /// <summary>What a dominant category usually means for Rahkaran, and where to look next (general guidance).</summary>
    public static LocalizedText Explain(WaitCategory category) => category switch
    {
        WaitCategory.Cpu => new("انتظار برای CPU: پرس‌وجوهای پرمصرف CPU یا کمبود هسته؛ صفحهٔ «پرس‌وجوهای پرهزینه» را ببینید.",
            "Waiting for CPU: CPU-heavy queries or too few cores; check Top queries by CPU."),
        WaitCategory.Parallelism => new("انتظار موازی‌سازی: MAXDOP و cost threshold را با توصیهٔ ستاد فنی مقایسه کنید.",
            "Parallelism waits: compare MAXDOP and cost threshold with the ستاد فنی baseline."),
        WaitCategory.Worker => new("کمبود Worker (THREADPOOL): درخواست‌ها برای اجرا Thread پیدا نمی‌کنند؛ Blocking طولانی را بررسی کنید.",
            "Worker starvation (THREADPOOL): requests cannot get a thread; look for long blocking chains."),
        WaitCategory.IoData => new("انتظار دیسک داده (PAGEIOLATCH): کندی دیسک یا خواندن زیاد از دیسک؛ Latency فایل‌ها و حافظه را ببینید.",
            "Data-file I/O waits (PAGEIOLATCH): slow disks or memory pressure; check file latency and PLE."),
        WaitCategory.IoLog => new("انتظار نوشتن Log (WRITELOG): دیسک Log کند است یا تراکنش‌های کوچک زیاد؛ Latency نوشتن Log را ببینید.",
            "Log write waits (WRITELOG): slow log disk or many tiny commits; check log write latency."),
        WaitCategory.Locking => new("انتظار قفل (LCK_M_*): Blocking؛ صفحهٔ «Blocking» و تراکنش‌های باز را ببینید.",
            "Lock waits (LCK_M_*): blocking; check the Blocking page and open transactions."),
        WaitCategory.Latch => new("انتظار Latch صفحه: در tempdb یعنی رقابت روی صفحات تخصیص؛ تعداد فایل‌های tempdb را ببینید.",
            "Page latch waits: on tempdb this is allocation contention; check tempdb file count."),
        WaitCategory.Memory => new("انتظار حافظه (RESOURCE_SEMAPHORE): پرس‌وجوها منتظر Memory Grant هستند؛ پرس‌وجوهای با Grant بزرگ را ببینید.",
            "Memory waits (RESOURCE_SEMAPHORE): queries wait for memory grants; check large-grant queries."),
        WaitCategory.Network => new("ASYNC_NETWORK_IO: برنامه نتایج را کند مصرف می‌کند — معمولاً لایهٔ برنامهٔ راهکاران یا شبکه، نه SQL.",
            "ASYNC_NETWORK_IO: the application consumes results slowly — usually the Rahkaran app tier or network, not SQL."),
        WaitCategory.Backup => new("انتظار پشتیبان‌گیری: Job پشتیبان در حال اجراست.", "Backup waits: a backup is running."),
        _ => new("سایر انتظارها.", "Other waits."),
    };
}
