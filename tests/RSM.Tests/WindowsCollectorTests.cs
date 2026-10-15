using RSM.Collectors.Windows;
using RSM.Core.Model;
using RSM.Core.Profiles;

namespace RSM.Tests;

public class WindowsCollectorTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 15, 6, 0, 0, TimeSpan.Zero);

    private static object? FactValue(LiveOutput output, string key) => output.Facts.LastOrDefault(f => f.Key == key)?.Value;

    /// <summary>Answers every query with plausible values; raw counters grow on each call.</summary>
    private sealed class FakeCim : ICimQuery
    {
        public List<string> Queries { get; } = [];

        public Func<CimQuery, IReadOnlyList<CimRow>>? Handler { get; init; }

        public string Protocol => "fake";

        private int _calls;

        public Task<IReadOnlyList<CimRow>> QueryAsync(CimQuery query, CancellationToken cancellationToken)
        {
            Queries.Add(query.ToWql());
            if (Handler is not null)
            {
                return Task.FromResult(Handler(query));
            }

            _calls++;
            var instances = query.Where is null ? new[] { "C:", "D:", "_Total", "HarddiskVolume3" } : ["_Total"];
            var rows = instances.Select(name => new CimRow(query.Properties.ToDictionary(p => p, p => (object?)(p switch
            {
                "Name" => name,
                "Frequency_PerfTime" => 10_000_000UL,
                _ when p.EndsWith("_Base", StringComparison.Ordinal) => (ulong)(_calls * 100),
                _ when query.ClassName.Contains("Raw", StringComparison.Ordinal) => (ulong)(_calls * 1_000_000),
                _ => 7UL,
            })))).ToList();
            return Task.FromResult<IReadOnlyList<CimRow>>(rows);
        }
    }

    // ---------- the metric-key contract ----------

    [Fact]
    public void Every_metric_has_a_pdh_and_a_cim_source_and_a_unique_key()
    {
        WindowsMetricCatalog.All.Should().OnlyContain(m =>
            m.Key.StartsWith("win.", StringComparison.Ordinal)
            && m.PdhCategory.Length > 0 && m.PdhCounter.Length > 0 && m.CimClass.StartsWith("Win32_Perf", StringComparison.Ordinal) && m.CimProperty.Length > 0);
        WindowsMetricCatalog.Keys.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task Cim_transport_returns_exactly_the_catalogue_keys_that_pdh_reads()
    {
        var source = new CimCounterSource(new FakeCim()) { WarmUp = TimeSpan.Zero };

        var readings = await source.ReadAsync(CancellationToken.None);

        // PdhCounterSource opens one counter per catalogue entry, so the catalogue key set IS the PDH key set.
        readings.Select(r => r.Key).Distinct().Should().BeEquivalentTo(WindowsMetricCatalog.Keys);
        readings.Where(r => r.Key == "win.disk.read_ms").Select(r => r.Instance).Should().BeEquivalentTo(["C:", "D:"], "_Total and unnamed volumes are dropped");
        readings.Where(r => r.Key == "win.cpu.total_pct").Should().ContainSingle().Which.Instance.Should().BeNull("_Total metrics have no instance, like PDH");
    }

    [Fact]
    public async Task Cim_average_timer_is_delta_value_over_frequency_over_delta_base()
    {
        var source = new CimCounterSource(new FakeCim()) { WarmUp = TimeSpan.Zero };

        var readings = await source.ReadAsync(CancellationToken.None);

        // Δvalue 1 000 000 ticks / 10 MHz = 0.1 s over Δbase 100 operations = 1 ms per read.
        readings.Single(r => r.Key == "win.disk.read_ms" && r.Instance == "D:").Value.Should().BeApproximately(1, 1e-9);
    }

    // ---------- query and registry whitelists ----------

    [Theory]
    [InlineData("Win32_Process; DROP", "Name", null)]
    [InlineData("Win32_Process", "Name, CommandLine", null)]
    [InlineData("Win32_Service", "Name", "Name = 'x' OR (1=1)")]
    [InlineData("Win32_Service", "Name", "Name = 'x'; SELECT")]
    public void Cim_queries_accept_only_identifiers_and_constant_filters(string className, string property, string? where) =>
        FluentActions.Invoking(() => new CimQuery(className, [property], where).ToWql()).Should().Throw<ArgumentException>();

    [Fact]
    public void Cim_query_text_is_a_plain_select()
    {
        new CimQuery("Win32_PerfFormattedData_PerfOS_Memory", ["AvailableMBytes"], "Name = '_Total'").ToWql()
            .Should().Be("SELECT AvailableMBytes FROM Win32_PerfFormattedData_PerfOS_Memory WHERE Name = '_Total'");
        FluentActions.Invoking(() => new CimQuery("Win32_PowerPlan", ["IsActive"], null, @"root\..\x").ToWql()).Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Registry_reads_are_limited_to_the_whitelist()
    {
        RegistryWhitelist.All.Should().OnlyContain(id => id.Key.StartsWith("SYSTEM\\", StringComparison.Ordinal) || id.Key.StartsWith("SOFTWARE\\", StringComparison.Ordinal));
        RegistryWhitelist.IsAllowed(new RegistryValueId(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon", "DefaultPassword")).Should().BeFalse();
        RegistryWhitelist.IsAllowed(RegistryWhitelist.PrioritySeparation).Should().BeTrue();
    }

    // ---------- inventory ----------

    private sealed class FakeRegistry(Dictionary<RegistryValueId, object?> values, bool denyDefender = false) : IRegistryValues
    {
        public object? Read(RegistryValueId id) => denyDefender && id.Key.Contains("Defender", StringComparison.Ordinal)
            ? throw new UnauthorizedAccessException()
            : values.GetValueOrDefault(id);

        public bool KeyExists(RegistryValueId id) => values.ContainsKey(id);
    }

    [Fact]
    public void Registry_snapshot_reports_pending_reboot_reasons_and_unreadable_defender()
    {
        var registry = new FakeRegistry(new()
        {
            [RegistryWhitelist.PrioritySeparation] = 0x26,
            [RegistryWhitelist.CbsRebootPending] = null,
            [RegistryWhitelist.PendingFileRenames] = new[] { @"\??\C:\x.tmp", "" },
        }, denyDefender: true);

        var snapshot = WindowsInventoryCollector.ReadRegistry(registry, [])!;

        snapshot.RebootPending.Should().BeTrue();
        snapshot.RebootReasons.Should().Equal("Component Based Servicing", "PendingFileRenameOperations");
        snapshot.PrioritySeparation.Should().Be(0x26);
        snapshot.DefenderExcludedProcesses.Should().BeNull("access denied is 'unknown', not 'no exclusions'");
        var problems = new List<string>();
        WindowsInventoryCollector.ReadRegistry(null, problems).Should().BeNull();
        problems.Should().ContainSingle();
    }

    [Theory]
    [InlineData(0x18, true, true)]   // Background services
    [InlineData(0x26, true, false)]  // Programs
    [InlineData(0x02, true, true)]   // Server default
    [InlineData(0x02, false, false)] // Client default (programs)
    public void Decodes_processor_scheduling(int value, bool isServer, bool background) =>
        WindowsInventoryCollector.IsBackgroundServices(value, isServer).Should().Be(background);

    [Theory]
    [InlineData("Microsoft Corporation", "Virtual Machine", true)]
    [InlineData("VMware, Inc.", "VMware7,1", true)]
    [InlineData("HPE", "ProLiant DL380 Gen10", false)]
    [InlineData("Dell Inc.", "PowerEdge R740", false)]
    public void Detects_virtual_machines(string manufacturer, string model, bool expected) =>
        WindowsInventoryCollector.IsVirtual(manufacturer, model).Should().Be(expected);

    [Fact]
    public void Inventory_facts_cover_power_plan_pagefile_volumes_time_zone_and_hardware()
    {
        var inventory = new WindowsInventory(
            CimRow.Of(("Caption", "Microsoft Windows Server 2022 Standard"), ("OSArchitecture", "64-bit"), ("ProductType", 3u), ("TotalVisibleMemorySize", 64UL * 1024 * 1024),
                ("LastBootUpTime", T0.AddDays(-3).UtcDateTime)),
            CimRow.Of(("Manufacturer", "HPE"), ("Model", "ProLiant DL380 Gen10"), ("NumberOfLogicalProcessors", 16u), ("HypervisorPresent", false), ("AutomaticManagedPagefile", false)),
            [CimRow.Of(("Name", "Intel Xeon"), ("NumberOfCores", 16u), ("MaxClockSpeed", 2900u), ("CurrentClockSpeed", 1200u), ("VirtualizationFirmwareEnabled", true))],
            [CimRow.Of(("Name", @"C:\pagefile.sys"), ("InitialSize", 16384u), ("MaximumSize", 32768u))],
            [
                CimRow.Of(("Name", @"C:\"), ("BlockSize", 4096UL), ("Capacity", 100UL), ("FreeSpace", 40UL), ("BootVolume", true), ("SystemVolume", false), ("PageFilePresent", true)),
                CimRow.Of(("Name", @"D:\"), ("BlockSize", 65536UL), ("Capacity", 100UL), ("FreeSpace", 90UL), ("BootVolume", false), ("SystemVolume", false), ("PageFilePresent", false)),
                CimRow.Of(("Name", @"\\?\Volume{1234}\"), ("BlockSize", 4096UL)),
            ],
            [
                CimRow.Of(("InstanceID", @"Microsoft:PowerPlan\{381b4222-f694-41f0-9685-ff5bb260df2e}"), ("ElementName", "Balanced"), ("IsActive", true)),
                CimRow.Of(("InstanceID", @"Microsoft:PowerPlan\{8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c}"), ("ElementName", "High performance"), ("IsActive", false)),
            ],
            CimRow.Of(("StandardName", "Iran Standard Time"), ("DaylightMonth", 3u), ("Bias", 210)),
            new RegistrySnapshot(0x18, false, [], null, false, null, ["sqlservr.exe"]),
            T0);
        var output = new LiveOutput();

        WindowsInventoryCollector.Analyze(inventory, output);

        FactValue(output, "os.is_64bit").Should().Be(true);
        FactValue(output, "os.is_server").Should().Be(true);
        FactValue(output, "os.ram_mb").Should().Be(65_536d);
        ((double)FactValue(output, "os.uptime_days")!).Should().BeApproximately(3, 0.01);
        FactValue(output, "os.is_hpe").Should().Be(true);
        FactValue(output, "os.is_virtual").Should().Be(false);
        FactValue(output, "cpu.hyperthreading").Should().Be(false, "16 logical = 16 cores");
        FactValue(output, "cpu.virtualization_firmware").Should().Be(true);
        FactValue(output, "os.power_plan").Should().Be("Balanced");
        FactValue(output, "os.power_plan_high").Should().Be(false);
        FactValue(output, "os.pagefile_system_managed").Should().Be(false);
        FactValue(output, "os.pagefile_min_to_ram").Should().Be(0.25);
        FactValue(output, "os.pagefile_max_to_ram").Should().Be(0.5);
        FactValue(output, @"volume[C:\].block_size_kb").Should().Be(4d);
        FactValue(output, @"volume[C:\].is_os").Should().Be(true);
        FactValue(output, @"volume[C:\].has_pagefile").Should().Be(true);
        FactValue(output, @"volume[D:\].block_size_kb").Should().Be(64d);
        output.Facts.Should().NotContain(f => f.Key.Contains("Volume{", StringComparison.Ordinal));
        FactValue(output, "os.timezone_is_iran").Should().Be(true);
        FactValue(output, "os.timezone_has_dst").Should().Be(true);
        FactValue(output, "os.priority_background").Should().Be(true);
        FactValue(output, "os.pending_reboot").Should().Be(false);
        FactValue(output, "os.defender_realtime").Should().Be(true);
        FactValue(output, "os.defender_excludes_sqlservr").Should().Be(true);
    }

    [Fact]
    public void Power_plan_falls_back_to_the_registry_scheme()
    {
        var output = new LiveOutput();
        WindowsInventoryCollector.Analyze(new WindowsInventory(null, null, null, null, null, null, null,
            new RegistrySnapshot(null, null, [], WindowsInventoryCollector.HighPerformancePlan, null, null, null), T0), output);

        FactValue(output, "os.power_plan_high").Should().Be(true);
    }

    // ---------- live counters ----------

    private static IReadOnlyList<MetricReading> Readings(double cpu, double perf, double availableMb, double readMs, double queue = 1) =>
    [
        new("win.cpu.total_pct", null, cpu),
        new("win.cpu.perf_pct", null, perf),
        new("win.cpu.queue_length", null, queue),
        new("win.cpu.context_switches_sec", null, 32_000),
        new("win.mem.available_mb", null, availableMb),
        new("win.disk.read_ms", "D:", readMs),
        new("win.disk.write_ms", "D:", 2),
        new("win.net.bytes_total_sec", "Ethernet", 62_500_000),
        new("win.net.bandwidth_bps", "Ethernet", 1_000_000_000),
        new("win.net.errors_received", "Ethernet", 10),
    ];

    [Fact]
    public void Perf_maps_counters_to_volume_and_nic_facts()
    {
        var state = new WindowsPerfState { LogicalCpus = 16, RamMb = 65_536 };
        var output = new LiveOutput();

        WindowsPerfCollector.Apply(state, Readings(50, 100, 4_096, 25), T0, output);

        FactValue(output, "win.cpu.total_pct").Should().Be(50d);
        FactValue(output, "win.mem.available_pct").Should().Be(6.25);
        FactValue(output, "win.cpu.context_switches_per_core").Should().Be(2_000d);
        FactValue(output, @"volume[D:\].read_ms").Should().Be(25d);
        FactValue(output, @"volume[D:\].read_ms_min3").Should().BeNull("one sample is not sustained");
        FactValue(output, "net[Ethernet].util_pct").Should().Be(50d);
        FactValue(output, "net[Ethernet].errors_increasing").Should().BeNull("no previous sample yet");
        output.Samples.Should().Contain(s => s.Key == "win.disk.read_ms" && s.Instance == @"D:\");
    }

    [Fact]
    public void Perf_sustained_facts_need_the_window_covered()
    {
        var state = new WindowsPerfState { LogicalCpus = 4, RamMb = 16_384 };
        LiveOutput Tick(int seconds, double availableMb, double readMs, double perf, double errors = 10)
        {
            var o = new LiveOutput();
            var readings = Readings(60, perf, availableMb, readMs).Select(r => r.Key == "win.net.errors_received" ? r with { Value = errors } : r).ToList();
            WindowsPerfCollector.Apply(state, readings, T0.AddSeconds(seconds), o);
            return o;
        }

        Tick(0, 800, 30, 80);
        var short1 = Tick(15, 900, 40, 80);
        FactValue(short1, "win.mem.available_pct_max60").Should().BeNull("15 s does not cover 75 % of 60 s");
        Tick(30, 700, 35, 80);
        var covered = Tick(45, 600, 50, 80, errors: 12);

        ((double)FactValue(covered, "win.mem.available_pct_max60")!).Should().BeApproximately(100 * 900d / 16_384, 1e-9);
        FactValue(covered, @"volume[D:\].read_ms_min3").Should().Be(35d);
        FactValue(covered, "win.cpu.throttled_samples").Should().Be(4);
        FactValue(covered, "net[Ethernet].errors_increasing").Should().Be(true);
        FactValue(Tick(60, 600, 50, 100, errors: 12), "win.cpu.throttled_samples").Should().Be(0);
    }

    [Fact]
    public void Processes_are_named_scaled_per_core_and_w3wp_growth_is_tracked()
    {
        var state = new ProcessState { LogicalCpus = 4 };
        void Tick(int i, double w3wpBytes)
        {
            WindowsProcessCollector.Apply(state,
            [
                CimRow.Of(("Name", "_Total"), ("PercentProcessorTime", 400UL)),
                CimRow.Of(("Name", "Idle"), ("PercentProcessorTime", 300UL)),
                CimRow.Of(("Name", "sqlservr"), ("IDProcess", 1200u), ("PercentProcessorTime", 200UL), ("PrivateBytes", 1UL << 30), ("WorkingSetPrivate", 2UL << 30)),
                CimRow.Of(("Name", "w3wp"), ("IDProcess", 2000u), ("PercentProcessorTime", 40UL), ("PrivateBytes", (ulong)w3wpBytes), ("WorkingSetPrivate", 1UL << 20)),
                CimRow.Of(("Name", "w3wp#1"), ("IDProcess", 2001u), ("PercentProcessorTime", 0UL), ("PrivateBytes", (ulong)w3wpBytes), ("WorkingSetPrivate", 1UL << 20)),
            ], T0.AddMinutes(i), _last = new LiveOutput());
        }

        Tick(0, 100 << 20);
        Tick(1, 120 << 20);
        Tick(2, 150 << 20);

        FactValue(_last!, "win.process.w3wp_count").Should().Be(2);
        FactValue(_last!, "win.process.w3wp_private_mb").Should().Be(300d);
        FactValue(_last!, "win.process.w3wp_private_rising").Should().Be(true);
        FactValue(_last!, "win.process.sqlservr_working_set_mb").Should().Be(2_048d);
        FactValue(_last!, "win.process.top_cpu").Should().Be("sqlservr (50 %)");
        WindowsProcessCollector.BaseName("w3wp#12").Should().Be("w3wp");
    }

    private LiveOutput? _last;

    // ---------- collector base: degrade, never crash ----------

    private sealed class FixedCounters(IReadOnlyList<MetricReading> readings) : IWindowsCounters
    {
        public string Transport => "test";

        public Task<IReadOnlyList<MetricReading>> ReadAsync(CancellationToken cancellationToken) => Task.FromResult(readings);
    }

    private static NodeContext Node(params object[] transports) =>
        new(new SiteProfile { Id = "s", Name = "s" }, new NodeProfile { Id = "n1", Name = "N1", Host = "srv", Roles = NodeRoles.Data }, transports);

    [Fact]
    public async Task Missing_windows_access_is_reported_not_thrown()
    {
        var result = await new WindowsPerfCollector().CollectAsync(Node(), new CollectContext(), CancellationToken.None);

        result.Status.Kind.Should().Be(CollectorStatusKind.Unavailable);
        result.Status.MissingCapability.Should().Be("win.cim");
    }

    [Fact]
    public async Task Inventory_degrades_to_partial_when_some_classes_fail()
    {
        var cim = new FakeCim
        {
            Handler = q => q.ClassName == "Win32_OperatingSystem"
                ? [CimRow.Of(("Caption", "Windows Server 2019"), ("OSArchitecture", "64-bit"))]
                : throw new InvalidOperationException("provider failure"),
        };
        using var transport = new WindowsNodeTransport(cim, new FixedCounters([]), null, isLocal: false);

        var result = await new WindowsInventoryCollector().CollectAsync(Node(transport), new CollectContext(), CancellationToken.None);

        result.Status.Kind.Should().Be(CollectorStatusKind.Partial);
        result.Facts.Should().Contain(new Fact("os.caption", "Windows Server 2019"));
        cim.Queries.Should().OnlyContain(q => q.StartsWith("SELECT ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Perf_collector_reports_the_transport_and_samples()
    {
        var cim = new FakeCim { Handler = q => [CimRow.Of(("NumberOfLogicalProcessors", 8u), ("TotalVisibleMemorySize", 8UL * 1024 * 1024))] };
        using var transport = new WindowsNodeTransport(cim, new FixedCounters(Readings(10, 100, 2_048, 1)), null, isLocal: true);

        var result = await new WindowsPerfCollector().CollectAsync(Node(transport), new CollectContext(), CancellationToken.None);

        result.Status.Should().Be(CollectorStatus.Ok);
        result.Facts.Should().Contain(new Fact("win.perf.transport", "test"));
        result.Facts.Should().Contain(new Fact("win.mem.available_pct", 25d));
        result.Samples.Should().NotBeEmpty();
    }
}
