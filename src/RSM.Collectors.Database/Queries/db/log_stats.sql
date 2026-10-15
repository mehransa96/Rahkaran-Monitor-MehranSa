-- key:         db.log_stats
-- purpose:     Log since last log backup, VLF counts, truncation holdup
-- min_version: 13.0.5026
-- permission:  VIEW DATABASE STATE
-- cost:        cheap
-- timeout_s:   10
-- database:    each
SELECT CAST(ls.log_since_last_log_backup_mb AS float) AS LogSinceLastLogBackupMb, ls.log_backup_time AS LogBackupTime,
       CAST(ls.total_vlf_count AS bigint) AS TotalVlfCount, CAST(ls.active_vlf_count AS bigint) AS ActiveVlfCount,
       ls.log_truncation_holdup_reason AS LogTruncationHoldupReason
FROM sys.dm_db_log_stats(DB_ID()) AS ls;
