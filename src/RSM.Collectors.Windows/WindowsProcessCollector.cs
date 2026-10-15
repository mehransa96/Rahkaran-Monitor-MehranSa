using RSM.Core.Model;

namespace RSM.Collectors.Windows;

/// <summary>One process in the top list (CPU % of the whole machine).</summary>
public sealed record ProcessUsage(string Name, int ProcessId, double CpuPercent, double PrivateMb, double WorkingSetMb);

public sealed class ProcessState
{
    public bool Warm { get; internal set; }

    public double? LogicalCpus { get; internal set; }

    public List<(DateTimeOffset At, double Value)> W3wpPrivateMb { get; } = [];
}

/// <summary>
/// Top processes by CPU and memory through the CIM performance class (works locally and remotely; no
/// <c>System.Diagnostics.Process</c> needed). Read-only: RSM shows processes, it never ends or re-prioritises them.
/// </summary>
public sealed class WindowsProcessCollector : WindowsCollectorBase<ProcessState>
{
    public const int Top = 15;

    public override string Id => "win.processes";

    public override CollectorCost Cost => CollectorCost.Medium;

    public override TimeSpan DefaultInterval => TimeSpan.FromSeconds(60);

    public TimeSpan WarmUp { get; init; } = TimeSpan.FromSeconds(1);

    private static readonly CimQuery Query = new("Win32_PerfFormattedData_PerfProc_Process",
        ["Name", "IDProcess", "PercentProcessorTime", "PrivateBytes", "WorkingSetPrivate"]);

    protected override async Task CollectCoreAsync(WindowsNodeTransport transport, ProcessState state, LiveOutput output, List<string> problems, CancellationToken cancellationToken)
    {
        if (state.LogicalCpus is null)
        {
            var cs = (await TryQueryAsync(transport.Cim, new CimQuery("Win32_ComputerSystem", ["NumberOfLogicalProcessors"]), problems, cancellationToken).ConfigureAwait(false))?.FirstOrDefault();
            state.LogicalCpus = cs?.Number("NumberOfLogicalProcessors");
        }

        if (!state.Warm)
        {
            // The formatted class reports 0 % CPU on the first read; read once to prime it.
            await TryQueryAsync(transport.Cim, Query, [], cancellationToken).ConfigureAwait(false);
            state.Warm = true;
            await Task.Delay(WarmUp, cancellationToken).ConfigureAwait(false);
        }

        var rows = await TryQueryAsync(transport.Cim, Query, problems, cancellationToken).ConfigureAwait(false);
        if (rows is not null)
        {
            Apply(state, rows, Clock.GetUtcNow(), output);
        }
    }

    internal static void Apply(ProcessState state, IReadOnlyList<CimRow> rows, DateTimeOffset at, LiveOutput output)
    {
        var cpus = state.LogicalCpus is > 0 ? state.LogicalCpus.Value : 1;
        var processes = rows
            .Where(r => r.String("Name") is { } n && n != "_Total" && n != "Idle")
            .Select(r => new ProcessUsage(
                BaseName(r.String("Name")!),
                (int)(r.Number("IDProcess") ?? 0),
                (r.Number("PercentProcessorTime") ?? 0) / cpus,
                (r.Number("PrivateBytes") ?? 0) / 1048576,
                (r.Number("WorkingSetPrivate") ?? 0) / 1048576))
            .ToList();

        var top = processes.OrderByDescending(p => p.CpuPercent).ThenByDescending(p => p.PrivateMb).Take(Top).ToList();
        foreach (var group in top.GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase).Take(8))
        {
            output.Sample("win.process.cpu_pct", group.Key, group.Sum(p => p.CpuPercent), at);
        }

        var w3wp = processes.Where(p => p.Name.Equals("w3wp", StringComparison.OrdinalIgnoreCase)).ToList();
        var w3wpMb = w3wp.Sum(p => p.PrivateMb);
        Window.Push(state.W3wpPrivateMb, (at, w3wpMb), 20);
        output.Fact("win.process.w3wp_count", w3wp.Count);
        output.Fact("win.process.w3wp_private_mb", w3wpMb);
        output.Fact("win.process.w3wp_private_rising", state.W3wpPrivateMb.Count >= 3 && IsRising(state.W3wpPrivateMb.TakeLast(3).Select(v => v.Value).ToList()));
        output.Fact("win.process.sqlservr_working_set_mb", processes.Where(p => p.Name.Equals("sqlservr", StringComparison.OrdinalIgnoreCase)).Sum(p => p.WorkingSetMb));
        output.Fact("win.process.top_cpu", top.FirstOrDefault() is { } first ? $"{first.Name} ({first.CpuPercent:0} %)" : null);
        output.Sample("win.process.w3wp_private_mb", null, w3wpMb, at);
        output.Payload["processes"] = top;
    }

    /// <summary>"w3wp#2" → "w3wp" (the performance class numbers same-named processes).</summary>
    internal static string BaseName(string name)
    {
        var hash = name.IndexOf('#', StringComparison.Ordinal);
        return hash > 0 ? name[..hash] : name;
    }

    private static bool IsRising(IReadOnlyList<double> values) => values.Zip(values.Skip(1)).All(p => p.Second > p.First * 1.01);
}
