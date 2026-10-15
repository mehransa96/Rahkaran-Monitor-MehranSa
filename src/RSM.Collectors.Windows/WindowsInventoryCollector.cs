using System.Globalization;
using RSM.Core.Model;

namespace RSM.Collectors.Windows;

/// <summary>Raw inputs of the inventory analysis (each part null when it could not be read).</summary>
public sealed record WindowsInventory(
    CimRow? Os,
    CimRow? ComputerSystem,
    IReadOnlyList<CimRow>? Processors,
    IReadOnlyList<CimRow>? PageFiles,
    IReadOnlyList<CimRow>? Volumes,
    IReadOnlyList<CimRow>? PowerPlans,
    CimRow? TimeZone,
    RegistrySnapshot? Registry,
    DateTimeOffset AtUtc);

/// <summary>Whitelisted registry values read for the inventory (null fields = not readable).</summary>
public sealed record RegistrySnapshot(
    int? PrioritySeparation,
    bool? RebootPending,
    IReadOnlyList<string> RebootReasons,
    string? ActivePowerScheme,
    bool? DefenderRealtimeDisabled,
    IReadOnlyList<string>? DefenderExcludedPaths,
    IReadOnlyList<string>? DefenderExcludedProcesses);

/// <summary>
/// §9.1–9.3, 9.5: OS, hardware, power plan, processor scheduling, pagefile, pending reboot, time zone, volumes
/// (allocation unit, OS/pagefile volume) and Defender exclusions. Facts feed RULES.md §1–2.
/// </summary>
public sealed class WindowsInventoryCollector : WindowsCollectorBase<NoWindowsState>
{
    public const string HighPerformancePlan = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";
    public const string UltimatePerformancePlan = "e9a42b02-d5df-448d-aa00-03f14749eb61";

    public override string Id => "win.inventory";

    public override TimeSpan DefaultInterval => TimeSpan.FromHours(1);

    protected override async Task CollectCoreAsync(WindowsNodeTransport transport, NoWindowsState state, LiveOutput output, List<string> problems, CancellationToken cancellationToken)
    {
        var cim = transport.Cim;
        var os = await TryQueryAsync(cim, new CimQuery("Win32_OperatingSystem",
            ["Caption", "Version", "BuildNumber", "OSArchitecture", "ProductType", "LastBootUpTime", "TotalVisibleMemorySize", "FreePhysicalMemory", "SystemDrive"]), problems, cancellationToken).ConfigureAwait(false);
        var cs = await TryQueryAsync(cim, new CimQuery("Win32_ComputerSystem",
            ["Manufacturer", "Model", "NumberOfProcessors", "NumberOfLogicalProcessors", "TotalPhysicalMemory", "HypervisorPresent", "AutomaticManagedPagefile", "Domain"]), problems, cancellationToken).ConfigureAwait(false);
        var cpus = await TryQueryAsync(cim, new CimQuery("Win32_Processor",
            ["Name", "NumberOfCores", "NumberOfLogicalProcessors", "MaxClockSpeed", "CurrentClockSpeed", "VirtualizationFirmwareEnabled"]), problems, cancellationToken).ConfigureAwait(false);
        var pageFiles = await TryQueryAsync(cim, new CimQuery("Win32_PageFileSetting", ["Name", "InitialSize", "MaximumSize"]), problems, cancellationToken).ConfigureAwait(false);
        var volumes = await TryQueryAsync(cim, new CimQuery("Win32_Volume",
            ["Name", "Label", "BlockSize", "Capacity", "FreeSpace", "FileSystem", "DriveType", "BootVolume", "SystemVolume", "PageFilePresent"], "DriveType = 3"), problems, cancellationToken).ConfigureAwait(false);
        var plans = await TryQueryAsync(cim, new CimQuery("Win32_PowerPlan", ["InstanceID", "ElementName", "IsActive"], null, @"root\cimv2\power"), [], cancellationToken).ConfigureAwait(false);
        var tz = await TryQueryAsync(cim, new CimQuery("Win32_TimeZone", ["Caption", "StandardName", "DaylightName", "DaylightMonth", "Bias"]), problems, cancellationToken).ConfigureAwait(false);
        var registry = ReadRegistry(transport.Registry, problems);

        Analyze(new WindowsInventory(os?.FirstOrDefault(), cs?.FirstOrDefault(), cpus, pageFiles, volumes, plans, tz?.FirstOrDefault(), registry, Clock.GetUtcNow()), output);
    }

    internal static RegistrySnapshot? ReadRegistry(IRegistryValues? registry, List<string> problems)
    {
        if (registry is null)
        {
            problems.Add("registry: not reachable (Remote Registry / rights)");
            return null;
        }

        T? Try<T>(Func<T?> read)
        {
            try
            {
                return read();
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
            {
                return default;
            }
        }

        var reasons = new List<string>();
        bool? pending = null;
        var cbs = Try(() => (bool?)registry.KeyExists(RegistryWhitelist.CbsRebootPending));
        var wu = Try(() => (bool?)registry.KeyExists(RegistryWhitelist.WindowsUpdateRebootRequired));
        var renames = Try(() => registry.Read(RegistryWhitelist.PendingFileRenames));
        if (cbs is not null || wu is not null)
        {
            if (cbs == true)
            {
                reasons.Add("Component Based Servicing");
            }

            if (wu == true)
            {
                reasons.Add("Windows Update");
            }

            if (renames is string[] { Length: > 0 } list && list.Any(s => !string.IsNullOrWhiteSpace(s)))
            {
                reasons.Add("PendingFileRenameOperations");
            }

            pending = reasons.Count > 0;
        }

        var realtime = Try(() => registry.Read(RegistryWhitelist.DefenderRealtimeDisabled));
        return new RegistrySnapshot(
            Try(() => registry.Read(RegistryWhitelist.PrioritySeparation) is int p ? p : (int?)null),
            pending,
            reasons,
            Try(() => registry.Read(RegistryWhitelist.ActivePowerScheme) as string),
            realtime is int r ? r != 0 : null,
            Try(() => registry.Read(RegistryWhitelist.DefenderExcludedPaths) as string[]),
            Try(() => registry.Read(RegistryWhitelist.DefenderExcludedProcesses) as string[]));
    }

    internal static void Analyze(WindowsInventory inv, LiveOutput output)
    {
        if (inv.Os is { } os)
        {
            output.Fact("os.caption", os.String("Caption"));
            output.Fact("os.version", os.String("Version"));
            output.Fact("os.is_64bit", os.String("OSArchitecture")?.Contains("64", StringComparison.Ordinal));
            output.Fact("os.is_server", os.Number("ProductType") is { } pt ? pt != 1 : null);
            output.Fact("os.ram_mb", os.Number("TotalVisibleMemorySize") / 1024);
            if (os.Time("LastBootUpTime") is { } boot)
            {
                output.Fact("os.uptime_days", (inv.AtUtc - boot).TotalDays);
            }
        }

        var logicalCpus = inv.ComputerSystem?.Number("NumberOfLogicalProcessors");
        if (inv.ComputerSystem is { } cs)
        {
            var manufacturer = cs.String("Manufacturer") ?? string.Empty;
            var model = cs.String("Model") ?? string.Empty;
            output.Fact("os.manufacturer", manufacturer);
            output.Fact("os.model", model);
            output.Fact("os.is_hpe", manufacturer.Contains("HPE", StringComparison.OrdinalIgnoreCase) || manufacturer.Contains("Hewlett", StringComparison.OrdinalIgnoreCase));
            output.Fact("os.is_virtual", IsVirtual(manufacturer, model));
            output.Fact("os.hypervisor_present", cs.Bool("HypervisorPresent"));
            output.Fact("os.logical_cpus", logicalCpus);
            output.Fact("os.pagefile_auto", cs.Bool("AutomaticManagedPagefile"));
        }

        if (inv.Processors is { Count: > 0 } cpus)
        {
            output.Fact("cpu.name", cpus[0].String("Name")?.Trim());
            output.Fact("cpu.sockets", cpus.Count);
            output.Fact("cpu.cores", cpus.Sum(c => c.Number("NumberOfCores") ?? 0));
            output.Fact("cpu.max_clock_mhz", cpus.Max(c => c.Number("MaxClockSpeed") ?? 0));
            output.Fact("cpu.current_clock_mhz", cpus.Min(c => c.Number("CurrentClockSpeed") ?? 0));
            output.Fact("cpu.virtualization_firmware", cpus.Any(c => c.Bool("VirtualizationFirmwareEnabled") == true));
            if (logicalCpus is { } logical)
            {
                output.Fact("cpu.hyperthreading", logical > cpus.Sum(c => c.Number("NumberOfCores") ?? 0));
            }
        }

        var ramMb = inv.Os?.Number("TotalVisibleMemorySize") / 1024;
        if (inv.PageFiles is { } pageFiles)
        {
            // MaximumSize 0 = "system managed size" for that file.
            output.Fact("os.pagefile_count", pageFiles.Count);
            output.Fact("os.pagefile_system_managed", inv.ComputerSystem?.Bool("AutomaticManagedPagefile") == true || pageFiles.Any(p => (p.Number("MaximumSize") ?? 0) == 0));
            output.Fact("os.pagefile_min_mb", pageFiles.Sum(p => p.Number("InitialSize") ?? 0));
            output.Fact("os.pagefile_max_mb", pageFiles.Sum(p => p.Number("MaximumSize") ?? 0));
            if (ramMb is > 0)
            {
                output.Fact("os.pagefile_min_to_ram", pageFiles.Sum(p => p.Number("InitialSize") ?? 0) / ramMb);
                output.Fact("os.pagefile_max_to_ram", pageFiles.Sum(p => p.Number("MaximumSize") ?? 0) / ramMb);
            }
        }

        AnalyzePowerPlan(inv, output);

        if (inv.Volumes is { } volumes)
        {
            foreach (var v in volumes.Where(v => v.String("Name") is { Length: > 0 } name && !name.StartsWith(@"\\?\", StringComparison.Ordinal)))
            {
                var key = $"volume[{v.String("Name")}]";
                output.Fact($"{key}.block_size_kb", v.Number("BlockSize") / 1024);
                output.Fact($"{key}.is_os", v.Bool("BootVolume") == true || v.Bool("SystemVolume") == true);
                output.Fact($"{key}.has_pagefile", v.Bool("PageFilePresent"));
                output.Fact($"{key}.label", v.String("Label"));
                if (v.Number("Capacity") is > 0 and var capacity)
                {
                    output.Fact($"{key}.os_free_pct", 100 * (v.Number("FreeSpace") ?? 0) / capacity);
                }
            }

            output.Payload["volumes"] = volumes;
        }

        if (inv.TimeZone is { } tz)
        {
            var name = tz.String("StandardName") ?? tz.String("Caption") ?? string.Empty;
            output.Fact("os.timezone", name);
            output.Fact("os.timezone_is_iran", name.Contains("Iran", StringComparison.OrdinalIgnoreCase) || (tz.Number("Bias") == 210));
            output.Fact("os.timezone_has_dst", (tz.Number("DaylightMonth") ?? 0) != 0);
        }

        if (inv.Registry is { } reg)
        {
            if (reg.PrioritySeparation is { } ps)
            {
                output.Fact("os.priority_separation", ps);
                output.Fact("os.priority_background", IsBackgroundServices(ps, inv.Os?.Number("ProductType") is { } t && t != 1));
            }

            output.Fact("os.pending_reboot", reg.RebootPending);
            output.Fact("os.pending_reboot_reasons", reg.RebootReasons.Select(r => (object?)r).ToList());
            output.Fact("os.defender_exclusions_readable", reg.DefenderExcludedProcesses is not null || reg.DefenderExcludedPaths is not null);
            if (reg.DefenderRealtimeDisabled is { } disabled)
            {
                output.Fact("os.defender_realtime", !disabled);
            }

            if (reg.DefenderExcludedProcesses is { } processes)
            {
                output.Fact("os.defender_excludes_sqlservr", processes.Any(p => p.EndsWith("sqlservr.exe", StringComparison.OrdinalIgnoreCase)));
            }

            if (reg.DefenderExcludedPaths is { } paths)
            {
                output.Fact("os.defender_excluded_paths", paths.Select(p => (object?)p).ToList());
            }
        }

        output.Payload["os"] = inv.Os;
        output.Payload["computerSystem"] = inv.ComputerSystem;
        output.Payload["processors"] = inv.Processors;
    }

    private static void AnalyzePowerPlan(WindowsInventory inv, LiveOutput output)
    {
        string? guid = null;
        string? name = null;
        if (inv.PowerPlans?.FirstOrDefault(p => p.Bool("IsActive") == true) is { } active)
        {
            name = active.String("ElementName");
            var id = active.String("InstanceID") ?? string.Empty; // "Microsoft:PowerPlan\{guid}"
            var open = id.IndexOf('{', StringComparison.Ordinal);
            guid = open >= 0 ? id[(open + 1)..].TrimEnd('}') : null;
        }
        else if (inv.Registry?.ActivePowerScheme is { } scheme)
        {
            guid = scheme;
        }

        if (guid is null)
        {
            return;
        }

        output.Fact("os.power_plan", name ?? guid);
        output.Fact("os.power_plan_high", guid.Equals(HighPerformancePlan, StringComparison.OrdinalIgnoreCase) || guid.Equals(UltimatePerformancePlan, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Win32PrioritySeparation: bits 4–5 interval (1 long, 2 short, 0 default), bits 2–3 length (1 variable, 2 fixed,
    /// 0 default), bits 0–1 foreground boost. "Background services" = long, fixed, no boost (0x18); defaults on a
    /// Server SKU (e.g. 0x2) mean long and fixed, so the boost bits have no effect there.
    /// </summary>
    public static bool IsBackgroundServices(int value, bool isServer)
    {
        var interval = (value >> 4) & 3;
        var length = (value >> 2) & 3;
        var boost = value & 3;
        var longQuanta = interval == 1 || (interval == 0 && isServer);
        var fixedLength = length == 2 || (length == 0 && isServer);
        return longQuanta && fixedLength && (boost == 0 || (interval == 0 && length == 0 && isServer));
    }

    public static bool IsVirtual(string manufacturer, string model) =>
        new[] { "Virtual", "VMware", "KVM", "QEMU", "Xen", "HVM domU", "Bochs", "OpenStack" }.Any(m => model.Contains(m, StringComparison.OrdinalIgnoreCase))
        || new[] { "VMware", "QEMU", "Xen", "Red Hat", "Nutanix", "innotek" }.Any(m => manufacturer.Contains(m, StringComparison.OrdinalIgnoreCase))
        || (manufacturer.Contains("Microsoft", StringComparison.OrdinalIgnoreCase) && model.Contains("Virtual", StringComparison.OrdinalIgnoreCase));

    internal static string Mb(double value) => value.ToString("0", CultureInfo.InvariantCulture);
}

/// <summary>Collectors without state between ticks.</summary>
public sealed class NoWindowsState;
