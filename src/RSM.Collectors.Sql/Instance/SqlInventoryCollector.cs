using RSM.Collectors.Sql.Builds;
using RSM.Core.Model;
using RSM.Safety.Sql;

namespace RSM.Collectors.Sql.Instance;

/// <summary>§7.1 inventory + §7.12 memory: version/CU, CPU/NUMA, memory, services, schedulers, dumps.</summary>
public sealed class SqlInventoryCollector(SafeSqlExecutor executor, KnownBuildCatalog? builds = null) : SqlCollectorBase(executor)
{
    public override string Id => "sql.inventory";

    protected override async Task<IReadOnlyDictionary<string, object?>> CollectCoreAsync(INodeContext node, CollectContext context, SqlQueryRunner runner, List<Fact> facts, List<UnifiedEvent> events)
    {
        var props = (await runner.QueryAsync<ServerPropertiesRow>("sql.inv.server_properties").ConfigureAwait(false))?.FirstOrDefault();
        var sysInfo = (await runner.QueryAsync<SysInfoRow>("sql.inv.sys_info").ConfigureAwait(false))?.FirstOrDefault();
        var host = (await runner.QueryAsync<HostInfoRow>("sql.inv.host_info").ConfigureAwait(false))?.FirstOrDefault();
        var services = await runner.QueryAsync<ServiceRow>("sql.inv.services").ConfigureAwait(false);
        var schedulers = (await runner.QueryAsync<SchedulerRow>("sql.inv.schedulers").ConfigureAwait(false))?.FirstOrDefault();
        var dumps = await runner.QueryAsync<DumpRow>("sql.inv.memory_dumps").ConfigureAwait(false);
        var osMemory = (await runner.QueryAsync<SysMemoryRow>("sql.inv.sys_memory").ConfigureAwait(false))?.FirstOrDefault();
        var process = (await runner.QueryAsync<ProcessMemoryRow>("sql.mem.process").ConfigureAwait(false))?.FirstOrDefault();
        var clerks = await runner.QueryAsync<ClerkRow>("sql.mem.clerks").ConfigureAwait(false);

        var now = DateTime.Now;
        if (props is not null)
        {
            Add(facts, "sql.version", props.ProductVersion);
            Add(facts, "sql.edition", props.Edition);
            Add(facts, "sql.engine_edition", props.EngineEdition);
            Add(facts, "sql.product_level", props.ProductLevel);
            Add(facts, "sql.update_level", props.ProductUpdateLevel ?? string.Empty);
            Add(facts, "sql.collation", props.Collation);
            Add(facts, "sql.is_clustered", props.IsClustered == 1);
            Add(facts, "sql.hadr_enabled", props.IsHadrEnabled == 1);
            Add(facts, "sql.auth_windows_only", props.IsIntegratedSecurityOnly == 1);
            Add(facts, "sql.server_name", props.ServerName);
            Add(facts, "sql.machine_name", props.MachineName);
            Add(facts, "sql.native_compat_level", props.NativeCompatibilityLevel);
            Add(facts, "sql.errorlog_path", props.ErrorLogFileName);

            if (SqlServerVersion.TryParse(props.ProductVersion, out var version))
            {
                Add(facts, "sql.major", version.Major);
                Add(facts, "sql.product_name", version.ProductName);
                var a = KnownBuilds.Assess(version, builds ?? KnownBuilds.Embedded);
                Add(facts, "sql.build.branch", a.Branch ?? "unknown");
                Add(facts, "sql.build.latest", a.LatestBuild);
                Add(facts, "sql.build.latest_label", a.LatestLabel);
                Add(facts, "sql.build.is_latest", a.IsLatest);
                Add(facts, "sql.build.behind", a.IsBehind && !a.MissingSecurityUpdateOnly);
                Add(facts, "sql.build.security_update_missing", a.MissingSecurityUpdateOnly);
                Add(facts, "sql.build.below_baseline", a.BelowBaseline);
                Add(facts, "sql.build.newer_than_catalog", a.NewerThanCatalog);
                Add(facts, "sql.build.catalog_as_of", a.CatalogAsOf.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
                if (a.ExtendedSupportEnd is { } end)
                {
                    Add(facts, "sql.support.extended_end", end.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
                    Add(facts, "sql.support.days_left", (end.ToDateTime(TimeOnly.MinValue) - now.Date).TotalDays);
                }
            }
        }

        if (sysInfo is not null)
        {
            Add(facts, "sql.cpu.logical", sysInfo.CpuCount);
            Add(facts, "sql.cpu.hyperthread_ratio", sysInfo.HyperthreadRatio);
            Add(facts, "sql.cpu.sockets", sysInfo.SocketCount);
            Add(facts, "sql.cpu.cores_per_socket", sysInfo.CoresPerSocket);
            Add(facts, "sql.numa.nodes", sysInfo.NumaNodeCount ?? schedulers?.NodeCount);
            Add(facts, "sql.memory.physical_mb", sysInfo.PhysicalMemoryKb / 1024);
            Add(facts, "sql.memory.committed_mb", sysInfo.CommittedKb / 1024);
            Add(facts, "sql.memory.target_mb", sysInfo.CommittedTargetKb / 1024);
            Add(facts, "sql.start_time", sysInfo.SqlServerStartTime);
            if (sysInfo.SqlServerStartTime is { } start)
            {
                Add(facts, "sql.uptime_hours", (now - start).TotalHours);
            }

            Add(facts, "sql.vm_type", sysInfo.VirtualMachineType);
            Add(facts, "sql.max_workers", sysInfo.MaxWorkersCount);
        }

        Add(facts, "sql.host.platform", host?.HostPlatform);
        Add(facts, "sql.host.distribution", host?.HostDistribution);

        if (services is not null)
        {
            var engine = services.FirstOrDefault(s => s.ServiceName?.StartsWith("SQL Server (", StringComparison.OrdinalIgnoreCase) == true);
            var agent = services.FirstOrDefault(s => s.ServiceName?.StartsWith("SQL Server Agent", StringComparison.OrdinalIgnoreCase) == true);
            Add(facts, "sql.ifi_enabled", engine?.InstantFileInitializationEnabled is { } ifi ? ifi.Equals("Y", StringComparison.OrdinalIgnoreCase) : null);
            Add(facts, "sql.service.engine.account", engine?.ServiceAccount);
            Add(facts, "sql.service.agent.present", agent is not null);
            Add(facts, "sql.service.agent.startup", agent?.StartupType);
            Add(facts, "sql.service.agent.status", agent?.Status);
        }

        if (schedulers is not null)
        {
            Add(facts, "sql.schedulers.online", schedulers.VisibleOnline);
            Add(facts, "sql.schedulers.offline", schedulers.VisibleOffline);
        }

        if (dumps is not null)
        {
            Add(facts, "sql.dumps.count", dumps.Count);
            var since = DateTimeOffset.Now.AddDays(-30);
            Add(facts, "sql.dumps.count_30d", dumps.Count(d => d.CreationTime >= since));
            Add(facts, "sql.dumps.last", dumps.Count > 0 ? dumps.Max(d => d.CreationTime) : null);
            foreach (var dump in dumps.Where(d => d.CreationTime >= since))
            {
                events.Add(new UnifiedEvent(dump.CreationTime!.Value, node.Node.Id, Layer.Sql, "MSSQLSERVER", "memorydump", Severity.Critical, dump.FileName ?? "dump"));
            }
        }

        if (osMemory is not null)
        {
            Add(facts, "sql.os_memory.total_mb", osMemory.TotalPhysicalMemoryKb / 1024);
            Add(facts, "sql.os_memory.available_mb", osMemory.AvailablePhysicalMemoryKb / 1024);
            Add(facts, "sql.os_memory.state", osMemory.SystemMemoryState);
        }

        if (process is not null)
        {
            Add(facts, "sql.memory.in_use_mb", process.PhysicalMemoryInUseKb / 1024);
            Add(facts, "sql.memory.locked_pages_mb", process.LockedPageAllocationsKb / 1024);
            Add(facts, "sql.memory.physical_low", process.ProcessPhysicalMemoryLow == 1);
            Add(facts, "sql.memory.virtual_low", process.ProcessVirtualMemoryLow == 1);
        }

        return new Dictionary<string, object?>
        {
            ["serverProperties"] = props,
            ["sysInfo"] = sysInfo,
            ["hostInfo"] = host,
            ["services"] = services,
            ["schedulers"] = schedulers,
            ["memoryDumps"] = dumps,
            ["osMemory"] = osMemory,
            ["processMemory"] = process,
            ["memoryClerks"] = clerks,
        };
    }

    public sealed class ServerPropertiesRow
    {
        public string? MachineName { get; set; }

        public string? ServerName { get; set; }

        public string? InstanceName { get; set; }

        public string? ProductVersion { get; set; }

        public string? ProductLevel { get; set; }

        public string? ProductUpdateLevel { get; set; }

        public string? ProductUpdateReference { get; set; }

        public string? Edition { get; set; }

        public int? EngineEdition { get; set; }

        public string? Collation { get; set; }

        public int? IsClustered { get; set; }

        public int? IsHadrEnabled { get; set; }

        public int? IsIntegratedSecurityOnly { get; set; }

        public string? InstanceDefaultDataPath { get; set; }

        public string? InstanceDefaultLogPath { get; set; }

        public string? ErrorLogFileName { get; set; }

        public int? NativeCompatibilityLevel { get; set; }
    }

    public sealed class SysInfoRow
    {
        public int CpuCount { get; set; }

        public int HyperthreadRatio { get; set; }

        public int? SocketCount { get; set; }

        public int? CoresPerSocket { get; set; }

        public int? NumaNodeCount { get; set; }

        public long PhysicalMemoryKb { get; set; }

        public long CommittedKb { get; set; }

        public long CommittedTargetKb { get; set; }

        public DateTime? SqlServerStartTime { get; set; }

        public string? VirtualMachineType { get; set; }

        public int SchedulerCount { get; set; }

        public int MaxWorkersCount { get; set; }

        public string? AffinityType { get; set; }
    }

    public sealed class HostInfoRow
    {
        public string? HostPlatform { get; set; }

        public string? HostDistribution { get; set; }

        public string? HostRelease { get; set; }

        public string? HostServicePackLevel { get; set; }

        public int? HostSku { get; set; }
    }

    public sealed class ServiceRow
    {
        public string? ServiceName { get; set; }

        public string? StartupType { get; set; }

        public string? Status { get; set; }

        public int? ProcessId { get; set; }

        public DateTimeOffset? LastStartupTime { get; set; }

        public string? ServiceAccount { get; set; }

        public string? IsClustered { get; set; }

        public string? InstantFileInitializationEnabled { get; set; }
    }

    public sealed class SchedulerRow
    {
        public int? VisibleOnline { get; set; }

        public int? VisibleOffline { get; set; }

        public int? RunnableTasks { get; set; }

        public int? NodeCount { get; set; }
    }

    public sealed class DumpRow
    {
        public string? FileName { get; set; }

        public DateTimeOffset? CreationTime { get; set; }

        public long? SizeInBytes { get; set; }
    }

    public sealed class SysMemoryRow
    {
        public long TotalPhysicalMemoryKb { get; set; }

        public long AvailablePhysicalMemoryKb { get; set; }

        public long TotalPageFileKb { get; set; }

        public long AvailablePageFileKb { get; set; }

        public string? SystemMemoryState { get; set; }
    }

    public sealed class ProcessMemoryRow
    {
        public long PhysicalMemoryInUseKb { get; set; }

        public long LockedPageAllocationsKb { get; set; }

        public long LargePageAllocationsKb { get; set; }

        public int MemoryUtilizationPercentage { get; set; }

        public int ProcessPhysicalMemoryLow { get; set; }

        public int ProcessVirtualMemoryLow { get; set; }
    }

    public sealed class ClerkRow
    {
        public string? ClerkType { get; set; }

        public long SizeMb { get; set; }
    }
}
