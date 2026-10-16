using RSM.Core.Model;
using RSM.Safety.Sql;

namespace RSM.Collectors.Sql.Live;

public sealed record SqlClientRow
{
    public string HostName { get; set; } = string.Empty;

    public string ProgramName { get; set; } = string.Empty;

    public string? LoginName { get; set; }

    public string? DatabaseName { get; set; }

    public string? ClientAddress { get; set; }

    public int Sessions { get; set; }

    public int Running { get; set; }
}

/// <summary>Sessions of one client machine (by host_name, or by address when the client sends no host name).</summary>
public sealed record SqlClientHost(string Host, int Sessions, int Running, IReadOnlyList<string> Addresses, IReadOnlyList<string> Programs, IReadOnlyList<string> Databases);

/// <summary>
/// §6 topology: who is connected to this SQL Server — user sessions grouped by client host (RSM's own sessions
/// excluded). Feeds the topology map (App → SQL edges) and RK-002 (session balance across App hosts).
/// </summary>
public sealed class SqlClientsCollector(SafeSqlExecutor executor) : SqlLiveCollectorBase<NoState>(executor)
{
    public override string Id => "topo.sql";

    public override Layer Layer => Layer.Rahkaran;

    public override NodeRoles AppliesTo => NodeRoles.Data;

    public override TimeSpan DefaultInterval => TimeSpan.FromMinutes(1);

    protected override async Task CollectCoreAsync(INodeContext node, CollectContext context, SqlQueryRunner runner, NoState state, LiveOutput output)
    {
        if (await runner.QueryAsync<SqlClientRow>("topo.sql_clients").ConfigureAwait(false) is { } rows)
        {
            Apply(rows, output);
        }
    }

    public static IReadOnlyList<SqlClientHost> Group(IReadOnlyList<SqlClientRow> rows) =>
        rows.GroupBy(r => r.HostName.Length > 0 ? r.HostName : r.ClientAddress ?? "?", StringComparer.OrdinalIgnoreCase)
            .Select(g => new SqlClientHost(
                g.Key,
                g.Sum(r => r.Sessions),
                g.Sum(r => r.Running),
                g.Select(r => r.ClientAddress).Where(a => !string.IsNullOrEmpty(a)).Distinct(StringComparer.OrdinalIgnoreCase).ToList()!,
                g.GroupBy(r => r.ProgramName, StringComparer.OrdinalIgnoreCase).OrderByDescending(p => p.Sum(r => r.Sessions)).Select(p => p.Key).Where(p => p.Length > 0).Take(3).ToList(),
                g.Select(r => r.DatabaseName).Where(d => !string.IsNullOrEmpty(d)).Distinct(StringComparer.OrdinalIgnoreCase).Take(5).ToList()!))
            .OrderByDescending(h => h.Sessions)
            .ToList();

    internal static void Apply(IReadOnlyList<SqlClientRow> rows, LiveOutput output)
    {
        var hosts = Group(rows);
        output.Payload["clients"] = hosts;
        output.Fact("topo.sql_sessions_total", hosts.Sum(h => h.Sessions));
        output.Fact("topo.sql_client_hosts", hosts.Count);
        foreach (var h in hosts.Take(50))
        {
            var key = $"sqlclient[{h.Host}]";
            output.Fact($"{key}.sessions", h.Sessions);
            output.Fact($"{key}.running", h.Running);
            output.Fact($"{key}.addresses", string.Join("|", h.Addresses));
            output.Fact($"{key}.programs", string.Join(", ", h.Programs));
            output.Fact($"{key}.databases", string.Join(", ", h.Databases));
        }
    }
}
