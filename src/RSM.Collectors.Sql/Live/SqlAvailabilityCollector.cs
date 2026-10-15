using RSM.Core.Model;
using RSM.Safety.Sql;

namespace RSM.Collectors.Sql.Live;

/// <summary>
/// §7.18 availability groups (informational): replica health and send/redo queues. Empty on servers without AGs;
/// facts are scoped <c>ag[&lt;group&gt;/&lt;replica&gt;]</c>.
/// </summary>
public sealed class SqlAvailabilityCollector(SafeSqlExecutor executor) : SqlLiveCollectorBase<NoState>(executor)
{
    public override string Id => "sql.ha";

    public override NodeRoles AppliesTo => NodeRoles.Data;

    public override TimeSpan DefaultInterval => TimeSpan.FromMinutes(1);

    protected override async Task CollectCoreAsync(INodeContext node, CollectContext context, SqlQueryRunner runner, NoState state, LiveOutput output)
    {
        var rows = await runner.QueryAsync<AgRow>("sql.ha.ag").ConfigureAwait(false);
        Apply(rows, Clock.GetUtcNow(), output);
    }

    internal static void Apply(IReadOnlyList<AgRow>? rows, DateTimeOffset at, LiveOutput output)
    {
        if (rows is null)
        {
            return;
        }

        output.Fact("sql.ha.ag_replicas", rows.Select(r => (r.AgName, r.ReplicaName)).Distinct().Count());
        foreach (var replica in rows.GroupBy(r => $"{r.AgName}/{r.ReplicaName}", StringComparer.OrdinalIgnoreCase))
        {
            var first = replica.First();
            var key = $"ag[{replica.Key}]";
            var sendMb = replica.Sum(r => r.LogSendQueueKb ?? 0) / 1024d;
            var redoMb = replica.Sum(r => r.RedoQueueKb ?? 0) / 1024d;
            output.Fact($"{key}.role", first.RoleDesc);
            output.Fact($"{key}.health", first.Health);
            output.Fact($"{key}.connected", first.ConnectedState);
            output.Fact($"{key}.healthy", string.Equals(first.Health, "HEALTHY", StringComparison.OrdinalIgnoreCase));
            output.Fact($"{key}.send_queue_mb", sendMb);
            output.Fact($"{key}.redo_queue_mb", redoMb);
            output.Fact($"{key}.not_synchronizing_dbs", replica.Where(r => r.DatabaseName is not null && r.SyncState is not ("SYNCHRONIZED" or "SYNCHRONIZING"))
                .Select(r => (object?)r.DatabaseName).ToList());
            output.Sample("sql.ha.send_queue_mb", replica.Key, sendMb, at);
            output.Sample("sql.ha.redo_queue_mb", replica.Key, redoMb, at);
        }

        output.Payload["availabilityGroups"] = rows;
    }

    public sealed class AgRow
    {
        public string? AgName { get; set; }

        public string? ReplicaName { get; set; }

        public string? RoleDesc { get; set; }

        public string? Health { get; set; }

        public string? ConnectedState { get; set; }

        public string? DatabaseName { get; set; }

        public string? SyncState { get; set; }

        public long? LogSendQueueKb { get; set; }

        public long? RedoQueueKb { get; set; }
    }
}
