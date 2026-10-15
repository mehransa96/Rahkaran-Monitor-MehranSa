-- key:         db.backups.history
-- purpose:     Backup history (all types) since @Since with sizes, flags, LSNs and destinations
-- min_version: 13
-- permission:  SELECT on msdb.dbo.backupset, backupmediafamily
-- cost:        medium
-- timeout_s:   60
-- database:    msdb
SELECT TOP (20000) b.backup_set_id AS BackupSetId, b.database_name AS DatabaseName, b.type AS BackupType,
       b.backup_start_date AS StartDate, b.backup_finish_date AS FinishDate,
       CAST(b.backup_size AS bigint) AS BackupSize, CAST(b.compressed_backup_size AS bigint) AS CompressedBackupSize,
       CAST(b.is_copy_only AS int) AS IsCopyOnly, CAST(b.has_backup_checksums AS int) AS HasChecksums,
       CAST(b.first_lsn AS decimal(25, 0)) AS FirstLsn, CAST(b.last_lsn AS decimal(25, 0)) AS LastLsn,
       mf.physical_device_name AS DeviceName, CAST(mf.device_type AS int) AS DeviceType
FROM dbo.backupset AS b
JOIN dbo.backupmediafamily AS mf ON mf.media_set_id = b.media_set_id
WHERE b.backup_finish_date >= @Since
ORDER BY b.backup_finish_date;
