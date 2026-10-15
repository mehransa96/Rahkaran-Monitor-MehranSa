using System.Text;
using RSM.Core.Model;
using RSM.Safety.Sql;

namespace RSM.Collectors.Sql.Instance;

/// <summary>§7.2 configuration compliance: every sp_configure option as <c>sql.config.&lt;name&gt;</c> + derived memory %.</summary>
public sealed class SqlConfigurationCollector(SafeSqlExecutor executor) : SqlCollectorBase(executor)
{
    public const long MaxServerMemoryDefault = 2147483647;

    public override string Id => "sql.configuration";

    public override CollectorCost Cost => CollectorCost.Cheap;

    /// <summary>"max server memory (MB)" → "max_server_memory_mb".</summary>
    public static string Normalize(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (var c in name.ToLowerInvariant())
        {
            sb.Append(char.IsAsciiLetterOrDigit(c) ? c : '_');
        }

        var text = sb.ToString();
        while (text.Contains("__", StringComparison.Ordinal))
        {
            text = text.Replace("__", "_", StringComparison.Ordinal);
        }

        return text.Trim('_');
    }

    protected override async Task<IReadOnlyDictionary<string, object?>> CollectCoreAsync(INodeContext node, CollectContext context, SqlQueryRunner runner, List<Fact> facts, List<UnifiedEvent> events)
    {
        var rows = await runner.QueryAsync<ConfigurationRow>("sql.cfg.configurations").ConfigureAwait(false);
        var sysInfo = (await runner.QueryAsync<SqlInventoryCollector.SysInfoRow>("sql.inv.sys_info").ConfigureAwait(false))?.FirstOrDefault();
        if (rows is null)
        {
            return new Dictionary<string, object?>();
        }

        var pending = new List<string>();
        foreach (var row in rows)
        {
            var key = Normalize(row.Name);
            Add(facts, $"sql.config.{key}", row.ValueInUse);
            Add(facts, $"sql.config_value.{key}", row.Value);
            if (row.Value != row.ValueInUse)
            {
                pending.Add(row.Name);
            }
        }

        Add(facts, "sql.config_pending_count", pending.Count);
        Add(facts, "sql.config_pending", pending);

        var max = rows.FirstOrDefault(r => Normalize(r.Name) == "max_server_memory_mb")?.ValueInUse;
        var min = rows.FirstOrDefault(r => Normalize(r.Name) == "min_server_memory_mb")?.ValueInUse;
        if (max is { } maxMb)
        {
            Add(facts, "sql.config.max_server_memory_default", maxMb >= MaxServerMemoryDefault);
        }

        if (sysInfo is { PhysicalMemoryKb: > 0 } info)
        {
            var physicalMb = info.PhysicalMemoryKb / 1024d;
            if (max is { } maxMb2 && maxMb2 < MaxServerMemoryDefault)
            {
                Add(facts, "sql.config.max_server_memory_pct", maxMb2 / physicalMb * 100);
                Add(facts, "sql.config.os_reserve_mb", physicalMb - maxMb2);
            }

            if (min is { } minMb)
            {
                Add(facts, "sql.config.min_server_memory_pct", minMb / physicalMb * 100);
            }
        }

        return new Dictionary<string, object?> { ["configurations"] = rows };
    }

    public sealed class ConfigurationRow
    {
        public string Name { get; set; } = string.Empty;

        public long Value { get; set; }

        public long ValueInUse { get; set; }

        public int IsDynamic { get; set; }

        public int IsAdvanced { get; set; }
    }
}
