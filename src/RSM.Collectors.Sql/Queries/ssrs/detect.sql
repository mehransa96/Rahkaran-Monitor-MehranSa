-- key:         ssrs.detect
-- purpose:     ReportServer databases on this instance (ReportServerTempDB excluded) that the RSM login can open
-- min_version: 13
-- permission:  public
-- cost:        cheap
-- timeout_s:   10
-- database:    master
SELECT d.name AS Name
FROM sys.databases AS d
WHERE d.name LIKE N'ReportServer%'
  AND d.name NOT LIKE N'%TempDB'
  AND d.state_desc = N'ONLINE'
  AND HAS_DBACCESS(d.name) = 1
ORDER BY d.name;
