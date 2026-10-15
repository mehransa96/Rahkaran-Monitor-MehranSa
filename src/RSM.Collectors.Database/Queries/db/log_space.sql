-- key:         db.log_space
-- purpose:     Transaction log size and used percent
-- min_version: 13
-- permission:  VIEW DATABASE STATE
-- cost:        cheap
-- timeout_s:   10
-- database:    each
SELECT total_log_size_in_bytes / 1048576 AS TotalMb, used_log_space_in_bytes / 1048576 AS UsedMb,
       CAST(used_log_space_in_percent AS float) AS UsedPct
FROM sys.dm_db_log_space_usage;
