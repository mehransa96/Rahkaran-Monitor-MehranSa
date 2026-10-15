namespace RSM.Collectors.Windows;

/// <summary>Which counter instances a metric reads.</summary>
public enum CounterInstances
{
    /// <summary>A single-instance category (Memory, System).</summary>
    Single,

    /// <summary>The <c>_Total</c> instance only.</summary>
    Total,

    /// <summary>Every instance except <c>_Total</c> (disks, network interfaces).</summary>
    Each,
}

/// <summary>How the CIM transport turns the class property into a value.</summary>
public enum CimCooking
{
    /// <summary>A <c>Win32_PerfFormattedData_*</c> property, already cooked by WMI.</summary>
    Formatted,

    /// <summary>
    /// A <c>Win32_PerfRawData_*</c> PERF_AVERAGE_TIMER (e.g. Avg. Disk sec/Read): Δvalue / Frequency_PerfTime / Δbase.
    /// The formatted class truncates these to whole seconds, so the raw class is required.
    /// </summary>
    RawAverageTimer,
}

/// <summary>One Windows metric and where each transport reads it.</summary>
public sealed record WinMetric(
    string Key,
    string Unit,
    string PdhCategory,
    string PdhCounter,
    CounterInstances Instances,
    string CimClass,
    string CimProperty,
    double PdhScale = 1,
    CimCooking Cooking = CimCooking.Formatted,
    double CimScale = 1);

/// <summary>
/// The metric-key contract (P2 acceptance: "remote CIM and local PDH return the same metric keys"). Rules, charts and
/// facts only see these keys; which transport produced a value is an implementation detail.
/// </summary>
public static class WindowsMetricCatalog
{
    private const string ProcessorInfo = "Win32_PerfFormattedData_Counters_ProcessorInformation";
    private const string SystemClass = "Win32_PerfFormattedData_PerfOS_System";
    private const string MemoryClass = "Win32_PerfFormattedData_PerfOS_Memory";
    private const string DiskClass = "Win32_PerfFormattedData_PerfDisk_LogicalDisk";
    private const string DiskRawClass = "Win32_PerfRawData_PerfDisk_LogicalDisk";
    private const string NetClass = "Win32_PerfFormattedData_Tcpip_NetworkInterface";
    private const string TcpClass = "Win32_PerfFormattedData_Tcpip_TCPv4";

    public static IReadOnlyList<WinMetric> All { get; } =
    [
        new("win.cpu.total_pct", "%", "Processor Information", "% Processor Time", CounterInstances.Total, ProcessorInfo, "PercentProcessorTime"),
        new("win.cpu.perf_pct", "%", "Processor Information", "% Processor Performance", CounterInstances.Total, ProcessorInfo, "PercentProcessorPerformance"),
        new("win.cpu.frequency_mhz", "MHz", "Processor Information", "Processor Frequency", CounterInstances.Total, ProcessorInfo, "ProcessorFrequency"),
        new("win.cpu.queue_length", "", "System", "Processor Queue Length", CounterInstances.Single, SystemClass, "ProcessorQueueLength"),
        new("win.cpu.context_switches_sec", "/s", "System", "Context Switches/sec", CounterInstances.Single, SystemClass, "ContextSwitchesPersec"),
        new("win.mem.available_mb", "MB", "Memory", "Available MBytes", CounterInstances.Single, MemoryClass, "AvailableMBytes"),
        new("win.mem.committed_pct", "%", "Memory", "% Committed Bytes In Use", CounterInstances.Single, MemoryClass, "PercentCommittedBytesInUse"),
        new("win.mem.pages_sec", "/s", "Memory", "Pages/sec", CounterInstances.Single, MemoryClass, "PagesPersec"),
        new("win.disk.read_ms", "ms", "LogicalDisk", "Avg. Disk sec/Read", CounterInstances.Each, DiskRawClass, "AvgDisksecPerRead", 1000, CimCooking.RawAverageTimer, 1000),
        new("win.disk.write_ms", "ms", "LogicalDisk", "Avg. Disk sec/Write", CounterInstances.Each, DiskRawClass, "AvgDisksecPerWrite", 1000, CimCooking.RawAverageTimer, 1000),
        new("win.disk.queue_length", "", "LogicalDisk", "Current Disk Queue Length", CounterInstances.Each, DiskClass, "CurrentDiskQueueLength"),
        new("win.disk.reads_sec", "/s", "LogicalDisk", "Disk Reads/sec", CounterInstances.Each, DiskClass, "DiskReadsPersec"),
        new("win.disk.writes_sec", "/s", "LogicalDisk", "Disk Writes/sec", CounterInstances.Each, DiskClass, "DiskWritesPersec"),
        new("win.disk.free_pct", "%", "LogicalDisk", "% Free Space", CounterInstances.Each, DiskClass, "PercentFreeSpace"),
        new("win.net.bytes_total_sec", "B/s", "Network Interface", "Bytes Total/sec", CounterInstances.Each, NetClass, "BytesTotalPersec"),
        new("win.net.bandwidth_bps", "bit/s", "Network Interface", "Current Bandwidth", CounterInstances.Each, NetClass, "CurrentBandwidth"),
        new("win.net.output_queue", "", "Network Interface", "Output Queue Length", CounterInstances.Each, NetClass, "OutputQueueLength"),
        new("win.net.errors_received", "", "Network Interface", "Packets Received Errors", CounterInstances.Each, NetClass, "PacketsReceivedErrors"),
        new("win.net.errors_outbound", "", "Network Interface", "Packets Outbound Errors", CounterInstances.Each, NetClass, "PacketsOutboundErrors"),
        new("win.net.discards_received", "", "Network Interface", "Packets Received Discarded", CounterInstances.Each, NetClass, "PacketsReceivedDiscarded"),
        new("win.tcp.connections_established", "", "TCPv4", "Connections Established", CounterInstances.Single, TcpClass, "ConnectionsEstablished"),
    ];

    public static IReadOnlyCollection<string> Keys { get; } = All.Select(m => m.Key).ToList();

    /// <summary>Instances shown as noise: the aggregate, and volumes without a drive letter or mount path.</summary>
    public static bool IsUsefulInstance(WinMetric metric, string instance) =>
        metric.Instances != CounterInstances.Each
        || (!string.Equals(instance, "_Total", StringComparison.OrdinalIgnoreCase)
            && !instance.StartsWith("HarddiskVolume", StringComparison.OrdinalIgnoreCase));

    /// <summary>"D:" (counter instance) → "D:\" (the mount point SQL Server and Win32_Volume report).</summary>
    public static string VolumeKey(string instance) => instance.EndsWith('\\') ? instance : instance + "\\";
}
