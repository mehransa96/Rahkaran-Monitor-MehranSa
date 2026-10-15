-- key:         db.backups.last
-- purpose:     Most recent backup per database, type and copy-only flag (all time)
-- min_version: 13
-- permission:  SELECT on msdb.dbo.backupset
-- cost:        cheap
-- timeout_s:   30
-- database:    msdb
SELECT b.database_name AS DatabaseName, b.type AS BackupType, CAST(b.is_copy_only AS int) AS IsCopyOnly, MAX(b.backup_finish_date) AS LastFinish
FROM dbo.backupset AS b
GROUP BY b.database_name, b.type, b.is_copy_only;
