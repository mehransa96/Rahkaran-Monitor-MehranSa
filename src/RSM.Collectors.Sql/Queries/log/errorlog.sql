-- key:         sql.log.errorlog
-- purpose:     One SQL Server (@LogType = 1) or Agent (@LogType = 2) error log, entries since @Since
-- min_version: 13
-- permission:  EXECUTE on xp_readerrorlog or securityadmin (verify per version)
-- cost:        medium
-- timeout_s:   30
-- database:    master
DECLARE @t TABLE (LogDate datetime NULL, ProcessInfo nvarchar(100) NULL, [Text] nvarchar(max) NULL);
INSERT INTO @t EXEC master.sys.xp_readerrorlog @LogNumber, @LogType;
SELECT LogDate, ProcessInfo, [Text] AS [Text]
FROM @t
WHERE LogDate >= @Since
ORDER BY LogDate;
