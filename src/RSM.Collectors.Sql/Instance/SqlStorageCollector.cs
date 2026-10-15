using RSM.Core.Model;
using RSM.Safety.Sql;

namespace RSM.Collectors.Sql.Instance;

/// <summary>
/// §7.13 tempdb and §7.14 I/O (snapshot part): tempdb layout and space, volume free space per database file,
/// cumulative file latency since startup. Live latency deltas arrive in P2.
/// </summary>
public sealed class SqlStorageCollector(SafeSqlExecutor executor) : SqlCollectorBase(executor)
{
    public override string Id => "sql.storage";

    protected override async Task<IReadOnlyDictionary<string, object?>> CollectCoreAsync(INodeContext node, CollectContext context, SqlQueryRunner runner, List<Fact> facts, List<UnifiedEvent> events)
    {
        var tempFiles = await runner.QueryAsync<TempdbFileRow>("sql.tempdb.files").ConfigureAwait(false);
        var tempSpace = (await runner.QueryAsync<TempdbSpaceRow>("sql.tempdb.space").ConfigureAwait(false))?.FirstOrDefault();
        var tempSessions = await runner.QueryAsync<TempdbSessionRow>("sql.tempdb.session_usage").ConfigureAwait(false);
        var contention = (await runner.QueryAsync<ContentionRow>("sql.tempdb.contention").ConfigureAwait(false))?.FirstOrDefault();
        var fileStats = await runner.QueryAsync<FileStatRow>("sql.io.file_stats").ConfigureAwait(false);
        var pending = (await runner.QueryAsync<PendingRow>("sql.io.pending").ConfigureAwait(false))?.FirstOrDefault();
        var volumes = await runner.QueryAsync<VolumeFileRow>("sql.io.volumes").ConfigureAwait(false);

        if (tempFiles is not null)
        {
            var data = tempFiles.Where(f => f.TypeDesc == "ROWS").ToList();
            Add(facts, "tempdb.data_files", data.Count);
            Add(facts, "tempdb.size_mb", data.Sum(f => f.SizeMb));
            Add(facts, "tempdb.files_equal_size", data.Select(f => f.SizeMb).Distinct().Count() <= 1);
            Add(facts, "tempdb.files_equal_growth", data.Select(f => (f.Growth, f.IsPercentGrowth)).Distinct().Count() <= 1);
            Add(facts, "tempdb.percent_growth", tempFiles.Any(f => f.IsPercentGrowth == 1));
            if (tempSpace is not null && data.Sum(f => f.SizeMb) is > 0 and var size)
            {
                Add(facts, "tempdb.version_store_mb", tempSpace.VersionStoreMb);
                Add(facts, "tempdb.version_store_pct", tempSpace.VersionStoreMb * 100d / size);
                Add(facts, "tempdb.free_mb", tempSpace.FreeMb);
            }
        }

        Add(facts, "tempdb.pagelatch_waiters", contention?.Waiters);

        if (fileStats is not null)
        {
            foreach (var db in fileStats.GroupBy(f => f.DatabaseName ?? f.DatabaseId.ToString(System.Globalization.CultureInfo.InvariantCulture)))
            {
                var dataFiles = db.Where(f => f.TypeDesc == "ROWS").ToList();
                var logFiles = db.Where(f => f.TypeDesc == "LOG").ToList();
                var reads = dataFiles.Sum(f => f.NumOfReads);
                var writes = logFiles.Sum(f => f.NumOfWrites);
                Add(facts, $"db[{db.Key}].io.data_read_ms_avg", reads > 0 ? dataFiles.Sum(f => f.IoStallReadMs) / (double)reads : null);
                Add(facts, $"db[{db.Key}].io.log_write_ms_avg", writes > 0 ? logFiles.Sum(f => f.IoStallWriteMs) / (double)writes : null);
            }
        }

        Add(facts, "io.pending_count", pending?.PendingCount);
        Add(facts, "io.max_pending_ms", pending?.MaxPendingMs);

        if (volumes is not null)
        {
            // Folders of every data/log file (OS-011 compares them with the antivirus exclusions on the same node).
            Add(facts, "sql.file_dirs", volumes.Select(v => v.PhysicalName is { } p && p.LastIndexOf('\\') > 0 ? p[..p.LastIndexOf('\\')] : null)
                .OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).Cast<object?>().ToList());
            foreach (var volume in volumes.GroupBy(v => v.VolumeMountPoint ?? "?"))
            {
                var v = volume.First();
                var key = $"volume[{volume.Key}]";
                Add(facts, $"{key}.total_mb", v.TotalMb);
                Add(facts, $"{key}.free_mb", v.AvailableMb);
                Add(facts, $"{key}.free_pct", v.TotalMb > 0 ? v.AvailableMb * 100d / v.TotalMb : null);
                Add(facts, $"{key}.file_system", v.FileSystemType);
                Add(facts, $"{key}.has_data", volume.Any(f => f.TypeDesc == "ROWS" && f.DatabaseId != 2));
                Add(facts, $"{key}.has_log", volume.Any(f => f.TypeDesc == "LOG" && f.DatabaseId != 2));
                Add(facts, $"{key}.has_tempdb", volume.Any(f => f.DatabaseId == 2));
            }

            foreach (var db in volumes.GroupBy(v => v.DatabaseName ?? "?"))
            {
                var dataVolumes = db.Where(f => f.TypeDesc == "ROWS").Select(f => f.VolumeMountPoint).Distinct().ToList();
                var logVolumes = db.Where(f => f.TypeDesc == "LOG").Select(f => f.VolumeMountPoint).Distinct().ToList();
                Add(facts, $"db[{db.Key}].data_volumes", dataVolumes);
                Add(facts, $"db[{db.Key}].log_volumes", logVolumes);
                Add(facts, $"db[{db.Key}].data_log_same_volume", dataVolumes.Intersect(logVolumes).Any());
                Add(facts, $"db[{db.Key}].data_volume_free_mb", db.Where(f => f.TypeDesc == "ROWS").Select(f => f.AvailableMb).DefaultIfEmpty().Min());
            }
        }

        return new Dictionary<string, object?>
        {
            ["tempdbFiles"] = tempFiles,
            ["tempdbSpace"] = tempSpace,
            ["tempdbTopSessions"] = tempSessions,
            ["tempdbContention"] = contention,
            ["fileStats"] = fileStats,
            ["pendingIo"] = pending,
            ["volumes"] = volumes,
        };
    }

    public sealed class TempdbFileRow
    {
        public int FileId { get; set; }

        public string? TypeDesc { get; set; }

        public string? Name { get; set; }

        public string? PhysicalName { get; set; }

        public long SizeMb { get; set; }

        public int Growth { get; set; }

        public int IsPercentGrowth { get; set; }

        public int MaxSize { get; set; }
    }

    public sealed class TempdbSpaceRow
    {
        public long FreeMb { get; set; }

        public long VersionStoreMb { get; set; }

        public long UserObjectsMb { get; set; }

        public long InternalObjectsMb { get; set; }
    }

    public sealed class TempdbSessionRow
    {
        public int SessionId { get; set; }

        public string? LoginName { get; set; }

        public string? HostName { get; set; }

        public string? ProgramName { get; set; }

        public long NetMb { get; set; }
    }

    public sealed class ContentionRow
    {
        public int Waiters { get; set; }

        public long MaxWaitMs { get; set; }
    }

    public sealed class FileStatRow
    {
        public string? DatabaseName { get; set; }

        public int DatabaseId { get; set; }

        public int FileId { get; set; }

        public string? TypeDesc { get; set; }

        public string? PhysicalName { get; set; }

        public long NumOfReads { get; set; }

        public long NumOfWrites { get; set; }

        public long IoStallReadMs { get; set; }

        public long IoStallWriteMs { get; set; }

        public long BytesRead { get; set; }

        public long BytesWritten { get; set; }
    }

    public sealed class PendingRow
    {
        public int PendingCount { get; set; }

        public long MaxPendingMs { get; set; }
    }

    public sealed class VolumeFileRow
    {
        public int DatabaseId { get; set; }

        public string? DatabaseName { get; set; }

        public int FileId { get; set; }

        public string? TypeDesc { get; set; }

        public string? PhysicalName { get; set; }

        public long SizeMb { get; set; }

        public string? VolumeMountPoint { get; set; }

        public string? LogicalVolumeName { get; set; }

        public string? FileSystemType { get; set; }

        public long TotalMb { get; set; }

        public long AvailableMb { get; set; }
    }
}
