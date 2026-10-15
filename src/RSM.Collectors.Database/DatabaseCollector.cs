using RSM.Collectors.Sql;
using RSM.Core.Model;
using RSM.Safety.Sql;

namespace RSM.Collectors.Database;

/// <summary>
/// §8 database module: options, files, log, VLFs, backups/RPO/LSN chain, integrity, CDC, orphaned users/guest,
/// largest tables and stale statistics of the Rahkaran DB, and (heavy, on demand) index fragmentation.
/// Facts are instance-scoped as <c>db[&lt;name&gt;].&lt;property&gt;</c>.
/// </summary>
public sealed class DatabaseCollector(SafeSqlExecutor executor) : SqlCollectorBase(executor)
{
    public const int BackupHistoryDays = 90;
    public const int HeavyTopTables = 20;

    public override string Id => "sql.databases";

    public override Layer Layer => Layer.Database;

    protected override async Task<IReadOnlyDictionary<string, object?>> CollectCoreAsync(INodeContext node, CollectContext context, SqlQueryRunner runner, List<Fact> facts, List<UnifiedEvent> events)
    {
        var now = DateTime.Now;
        var settings = node.Site.Settings;
        var databases = await runner.QueryAsync<DatabaseRow>("db.list").ConfigureAwait(false);
        if (databases is null)
        {
            return new Dictionary<string, object?>();
        }

        var checkdb = (await runner.QueryAsync<CheckDbRow>("db.integrity.last_checkdb").ConfigureAwait(false))
            ?.ToDictionary(r => r.Name, r => r.LastGoodCheckDb, StringComparer.OrdinalIgnoreCase);
        var activity = (await runner.QueryAsync<ActivityRow>("db.detect.activity").ConfigureAwait(false))
            ?.Where(a => a.DatabaseName is not null).ToDictionary(a => a.DatabaseName!, a => a.Sessions, StringComparer.OrdinalIgnoreCase) ?? [];
        var history = await runner.QueryAsync<BackupHistoryRow>("db.backups.history", new { Since = now.AddDays(-BackupHistoryDays) }).ConfigureAwait(false);
        var lastBackups = await runner.QueryAsync<LastBackupRow>("db.backups.last").ConfigureAwait(false);
        var suspect = await runner.QueryAsync<SuspectPageRow>("db.integrity.suspect_pages").ConfigureAwait(false);

        var detection = RahkaranDatabaseDetector.Detect(
            databases.Select(d => new DatabaseCandidate(d.Name, d.DatabaseId > 4, d.StateDesc == "ONLINE", d.DataSizeMb ?? 0, activity.GetValueOrDefault(d.Name))).ToList(),
            settings.RahkaranDatabases);
        Add(facts, "rk.databases", detection.Databases.Select(d => (object?)d).ToList());
        Add(facts, "rk.detected_by", detection.Method);
        Add(facts, "sql.suspect_pages", suspect?.Count);
        Add(facts, "sql.backup_history_available", history is not null);

        var backupSets = history is null ? [] : ToBackupSets(history);
        var perDatabase = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var db in databases)
        {
            var key = $"db[{db.Name}]";
            var isRahkaran = detection.Databases.Contains(db.Name, StringComparer.OrdinalIgnoreCase);
            var online = db.StateDesc == "ONLINE";
            Add(facts, $"{key}.database_id", db.DatabaseId);
            Add(facts, $"{key}.state", db.StateDesc);
            Add(facts, $"{key}.is_system", db.DatabaseId <= 4);
            Add(facts, $"{key}.is_user", db.DatabaseId > 4);
            Add(facts, $"{key}.is_reportserver", db.Name.StartsWith("ReportServer", StringComparison.OrdinalIgnoreCase) && !db.Name.EndsWith("TempDB", StringComparison.OrdinalIgnoreCase));
            Add(facts, $"{key}.is_reportserver_temp", db.Name.StartsWith("ReportServer", StringComparison.OrdinalIgnoreCase) && db.Name.EndsWith("TempDB", StringComparison.OrdinalIgnoreCase));
            Add(facts, $"{key}.is_rahkaran", isRahkaran);
            Add(facts, $"{key}.recovery_model", db.RecoveryModel);
            Add(facts, $"{key}.page_verify", db.PageVerify);
            Add(facts, $"{key}.auto_close", db.AutoClose == 1);
            Add(facts, $"{key}.auto_shrink", db.AutoShrink == 1);
            Add(facts, $"{key}.auto_create_stats", db.AutoCreateStats == 1);
            Add(facts, $"{key}.auto_update_stats", db.AutoUpdateStats == 1);
            Add(facts, $"{key}.compat_level", db.CompatibilityLevel);
            Add(facts, $"{key}.rcsi", db.Rcsi == 1);
            Add(facts, $"{key}.snapshot_isolation", db.SnapshotIsolation);
            Add(facts, $"{key}.log_reuse_wait", db.LogReuseWait);
            Add(facts, $"{key}.is_cdc_enabled", db.IsCdcEnabled == 1);
            Add(facts, $"{key}.is_query_store_on", db.IsQueryStoreOn == 1);
            Add(facts, $"{key}.is_read_only", db.IsReadOnly == 1);
            Add(facts, $"{key}.data_size_mb", db.DataSizeMb);
            Add(facts, $"{key}.log_size_mb", db.LogSizeMb);
            Add(facts, $"{key}.is_large", (db.DataSizeMb ?? 0) >= settings.LargeDatabaseThresholdGb * 1024L);
            Add(facts, $"{key}.sessions", activity.GetValueOrDefault(db.Name));
            if (checkdb is not null && online)
            {
                var last = checkdb.GetValueOrDefault(db.Name);
                // 1900-01-01 means "never" for LastGoodCheckDbTime.
                var never = last is null || last.Value.Year <= 1900;
                Add(facts, $"{key}.checkdb_known", true);
                Add(facts, $"{key}.days_since_checkdb", never ? 99999d : (now - last!.Value).TotalDays);
            }

            if (history is not null && lastBackups is not null)
            {
                var sets = backupSets.Where(s => string.Equals(s.Database, db.Name, StringComparison.OrdinalIgnoreCase)).ToList();
                DateTime? Last(char type) => lastBackups.Where(b => string.Equals(b.DatabaseName, db.Name, StringComparison.OrdinalIgnoreCase) && b.BackupType == type.ToString())
                    .Select(b => b.LastFinish).Max();
                var b = BackupAnalysis.Analyze(sets, now, Last('D'), Last('I'), Last('L'));
                Add(facts, $"{key}.has_full_backup", b.HasFull);
                Add(facts, $"{key}.hours_since_full", b.HoursSinceFull ?? 99999d);
                Add(facts, $"{key}.hours_since_diff", b.HoursSinceDiff ?? 99999d);
                Add(facts, $"{key}.hours_since_log", b.HoursSinceLog ?? 99999d);
                Add(facts, $"{key}.log_chain_broken", b.LogChainBroken);
                Add(facts, $"{key}.full_backups_24h", b.NonCopyOnlyFullsLast24h);
                Add(facts, $"{key}.full_backup_destinations_24h", b.FullBackupDevicesLast24h);
                Add(facts, $"{key}.backups_without_checksum_30d", b.WithoutChecksum30d);
                Add(facts, $"{key}.backup_throughput_dropped", b.ThroughputDropped);
                Add(facts, $"{key}.backup_to_unc", b.UsesUncDestination);
                Add(facts, $"{key}.full_backup_hour", b.TypicalFullHour);
            }

            if (!online || db.HasAccess != 1 || db.DatabaseId == 2)
            {
                continue;
            }

            var detail = await CollectInsideDatabaseAsync(runner, db, key, isRahkaran, context.IncludeHeavy, facts).ConfigureAwait(false);
            perDatabase[db.Name] = detail;
        }

        return new Dictionary<string, object?>
        {
            ["databases"] = databases,
            ["rahkaran"] = detection,
            ["lastBackups"] = lastBackups,
            ["suspectPages"] = suspect,
            ["perDatabase"] = perDatabase,
        };
    }

    private static async Task<IReadOnlyDictionary<string, object?>> CollectInsideDatabaseAsync(SqlQueryRunner runner, DatabaseRow db, string key, bool isRahkaran, bool includeHeavy, List<Fact> facts)
    {
        var files = await runner.QueryAsync<FileRow>("db.files", database: db.Name).ConfigureAwait(false);
        var logSpace = (await runner.QueryAsync<LogSpaceRow>("db.log_space", database: db.Name).ConfigureAwait(false))?.FirstOrDefault();
        var logStats = (await runner.QueryAsync<LogStatsRow>("db.log_stats", database: db.Name).ConfigureAwait(false))?.FirstOrDefault();
        var orphaned = await runner.QueryAsync<OrphanedUserRow>("sql.sec.orphaned_users", database: db.Name).ConfigureAwait(false);
        var guest = (await runner.QueryAsync<GuestRow>("sql.sec.guest", database: db.Name).ConfigureAwait(false))?.FirstOrDefault();

        if (files is not null)
        {
            var data = files.Where(f => f.TypeDesc == "ROWS").ToList();
            Add(facts, $"{key}.percent_growth_files", files.Count(f => f.IsPercentGrowth == 1));
            Add(facts, $"{key}.growth_not_1024_files", data.Count(f => f.IsPercentGrowth == 0 && f.Growth > 0 && f.GrowthMb % 1024 != 0));
            Add(facts, $"{key}.restricted_max_size_files", files.Count(f => f.MaxSize != -1 && !(f.TypeDesc == "LOG" && f.MaxSize == 268435456)));
            Add(facts, $"{key}.no_growth_low_free_files", data.Count(f => f.Growth == 0 && f.SizeMb > 0 && (f.SizeMb - (f.UsedMb ?? 0)) * 100d / f.SizeMb < 10));
            Add(facts, $"{key}.data_free_pct", data.Sum(f => f.SizeMb) is > 0 and var size ? (size - data.Sum(f => f.UsedMb ?? 0)) * 100d / size : null);
            Add(facts, $"{key}.log_file_size_mb", files.Where(f => f.TypeDesc == "LOG").Sum(f => f.SizeMb));
        }

        Add(facts, $"{key}.log_used_pct", logSpace?.UsedPct);
        if (logStats is not null)
        {
            Add(facts, $"{key}.vlf_count", logStats.TotalVlfCount);
            Add(facts, $"{key}.log_since_last_log_backup_mb", logStats.LogSinceLastLogBackupMb);
        }

        if (orphaned is not null)
        {
            Add(facts, $"{key}.orphaned_users", orphaned.Count);
            Add(facts, $"{key}.orphaned_user_names", orphaned.Select(o => (object?)o.Name).ToList());
        }

        Add(facts, $"{key}.guest_connect", guest is null ? null : guest.GuestConnect > 0);

        var result = new Dictionary<string, object?>
        {
            ["files"] = files,
            ["logSpace"] = logSpace,
            ["logStats"] = logStats,
            ["orphanedUsers"] = orphaned,
        };

        if (db.IsCdcEnabled == 1)
        {
            var cdcTables = (await runner.QueryAsync<CdcTablesRow>("db.cdc.tables", database: db.Name).ConfigureAwait(false))?.FirstOrDefault();
            var scan = (await runner.QueryAsync<CdcScanRow>("db.cdc.log_scan", database: db.Name).ConfigureAwait(false))?.FirstOrDefault();
            Add(facts, $"{key}.cdc_tracked_tables", cdcTables?.TrackedTables);
            Add(facts, $"{key}.cdc_latency_s", scan?.LatencySeconds);
            Add(facts, $"{key}.cdc_errors", scan?.ErrorCount);
            result["cdc"] = new { cdcTables, scan };
        }

        if (isRahkaran)
        {
            var tables = await runner.QueryAsync<TopTableRow>("db.size.top_tables", database: db.Name).ConfigureAwait(false);
            var stats = await runner.QueryAsync<StaleStatRow>("db.stats.properties", database: db.Name).ConfigureAwait(false);
            Add(facts, $"{key}.stale_stats", stats is null ? null : stats.Count == 0 ? 0 : stats[0].TotalStale);
            result["topTables"] = tables;
            result["staleStats"] = stats;

            if (includeHeavy && tables is not null)
            {
                var fragmentation = new List<IndexPhysicalRow>();
                foreach (var table in tables.Take(HeavyTopTables))
                {
                    var rows = await runner.QueryAsync<IndexPhysicalRow>("db.idx.physical", new { table.ObjectId }, db.Name).ConfigureAwait(false);
                    fragmentation.AddRange(rows ?? []);
                }

                Add(facts, $"{key}.fragmented_indexes", fragmentation.Count(f => f.FragmentationPct > 30));
                result["indexFragmentation"] = fragmentation;
            }
        }

        return result;
    }

    internal static List<BackupSet> ToBackupSets(IEnumerable<BackupHistoryRow> rows) =>
        rows.GroupBy(r => r.BackupSetId)
            .Select(g =>
            {
                var r = g.First();
                return new BackupSet(r.BackupSetId, r.DatabaseName ?? "?", string.IsNullOrEmpty(r.BackupType) ? '?' : r.BackupType[0],
                    r.StartDate ?? DateTime.MinValue, r.FinishDate ?? DateTime.MinValue, r.CompressedBackupSize ?? r.BackupSize ?? 0,
                    r.IsCopyOnly == 1, r.HasChecksums == 1, r.FirstLsn ?? 0, r.LastLsn ?? 0,
                    g.Select(x => x.DeviceName ?? string.Empty).Where(d => d.Length > 0).Distinct().ToList());
            })
            .ToList();

    public sealed class DatabaseRow
    {
        public int DatabaseId { get; set; }

        public string Name { get; set; } = string.Empty;

        public string? StateDesc { get; set; }

        public string? RecoveryModel { get; set; }

        public string? PageVerify { get; set; }

        public int AutoClose { get; set; }

        public int AutoShrink { get; set; }

        public int AutoCreateStats { get; set; }

        public int AutoUpdateStats { get; set; }

        public int CompatibilityLevel { get; set; }

        public int Rcsi { get; set; }

        public string? SnapshotIsolation { get; set; }

        public string? LogReuseWait { get; set; }

        public int IsCdcEnabled { get; set; }

        public int? IsQueryStoreOn { get; set; }

        public int IsReadOnly { get; set; }

        public string? UserAccess { get; set; }

        public DateTime? CreateDate { get; set; }

        public string? Owner { get; set; }

        public int? HasAccess { get; set; }

        public long? DataSizeMb { get; set; }

        public long? LogSizeMb { get; set; }
    }

    public sealed class CheckDbRow
    {
        public string Name { get; set; } = string.Empty;

        public DateTime? LastGoodCheckDb { get; set; }
    }

    public sealed class ActivityRow
    {
        public string? DatabaseName { get; set; }

        public int Sessions { get; set; }

        public int Hosts { get; set; }
    }

    public sealed class BackupHistoryRow
    {
        public int BackupSetId { get; set; }

        public string? DatabaseName { get; set; }

        public string? BackupType { get; set; }

        public DateTime? StartDate { get; set; }

        public DateTime? FinishDate { get; set; }

        public long? BackupSize { get; set; }

        public long? CompressedBackupSize { get; set; }

        public int IsCopyOnly { get; set; }

        public int HasChecksums { get; set; }

        public decimal? FirstLsn { get; set; }

        public decimal? LastLsn { get; set; }

        public string? DeviceName { get; set; }

        public int? DeviceType { get; set; }
    }

    public sealed class LastBackupRow
    {
        public string? DatabaseName { get; set; }

        public string? BackupType { get; set; }

        public int IsCopyOnly { get; set; }

        public DateTime? LastFinish { get; set; }
    }

    public sealed class SuspectPageRow
    {
        public int DatabaseId { get; set; }

        public int FileId { get; set; }

        public long PageId { get; set; }

        public int EventType { get; set; }

        public int ErrorCount { get; set; }

        public DateTime? LastUpdateDate { get; set; }
    }

    public sealed class FileRow
    {
        public int FileId { get; set; }

        public string? TypeDesc { get; set; }

        public string? Name { get; set; }

        public string? PhysicalName { get; set; }

        public long SizeMb { get; set; }

        public long? UsedMb { get; set; }

        public int Growth { get; set; }

        public int IsPercentGrowth { get; set; }

        public int MaxSize { get; set; }

        /// <summary>Growth in MB when not percent (growth is in 8 KB pages).</summary>
        public long GrowthMb => IsPercentGrowth == 1 ? 0 : Growth * 8L / 1024;
    }

    public sealed class LogSpaceRow
    {
        public long TotalMb { get; set; }

        public long UsedMb { get; set; }

        public double UsedPct { get; set; }
    }

    public sealed class LogStatsRow
    {
        public double? LogSinceLastLogBackupMb { get; set; }

        public DateTime? LogBackupTime { get; set; }

        public long? TotalVlfCount { get; set; }

        public long? ActiveVlfCount { get; set; }

        public string? LogTruncationHoldupReason { get; set; }
    }

    public sealed class OrphanedUserRow
    {
        public string Name { get; set; } = string.Empty;

        public string? TypeDesc { get; set; }
    }

    public sealed class GuestRow
    {
        public int GuestConnect { get; set; }
    }

    public sealed class CdcTablesRow
    {
        public int TrackedTables { get; set; }
    }

    public sealed class CdcScanRow
    {
        public DateTime? StartTime { get; set; }

        public DateTime? EndTime { get; set; }

        public int? DurationSeconds { get; set; }

        public string? ScanPhase { get; set; }

        public int? ErrorCount { get; set; }

        public int? LatencySeconds { get; set; }

        public int? FailedSessions { get; set; }
    }

    public sealed class TopTableRow
    {
        public int ObjectId { get; set; }

        public string? SchemaName { get; set; }

        public string? TableName { get; set; }

        public long ReservedMb { get; set; }

        public long UsedMb { get; set; }

        public long RowCnt { get; set; }
    }

    public sealed class StaleStatRow
    {
        public string? SchemaName { get; set; }

        public string? TableName { get; set; }

        public string? StatName { get; set; }

        public DateTime? LastUpdated { get; set; }

        public long RowCnt { get; set; }

        public long ModificationCounter { get; set; }

        public int TotalStale { get; set; }
    }

    public sealed class IndexPhysicalRow
    {
        public string? SchemaName { get; set; }

        public string? TableName { get; set; }

        public string? IndexName { get; set; }

        public string? IndexType { get; set; }

        public double FragmentationPct { get; set; }

        public long PageCount { get; set; }
    }
}
