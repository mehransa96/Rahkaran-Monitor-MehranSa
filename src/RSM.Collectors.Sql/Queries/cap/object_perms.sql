-- key:         cap.object_perms
-- purpose:     SELECT on msdb history tables and EXECUTE on xp_readerrorlog (fixed object list)
-- min_version: 13
-- permission:  public
-- cost:        cheap
-- timeout_s:   10
-- database:    master
SELECT
    HAS_PERMS_BY_NAME(N'msdb.dbo.backupset',         N'OBJECT', N'SELECT')  AS MsdbBackupset,
    HAS_PERMS_BY_NAME(N'msdb.dbo.backupmediafamily', N'OBJECT', N'SELECT')  AS MsdbBackupMediaFamily,
    HAS_PERMS_BY_NAME(N'msdb.dbo.suspect_pages',     N'OBJECT', N'SELECT')  AS MsdbSuspectPages,
    HAS_PERMS_BY_NAME(N'master.sys.xp_readerrorlog', N'OBJECT', N'EXECUTE') AS XpReadErrorLog,
    CAST(SERVERPROPERTY('ErrorLogFileName') AS nvarchar(512))               AS ErrorLogFileName;
