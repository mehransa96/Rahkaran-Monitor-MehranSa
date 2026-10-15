-- key:         sql.dl.from_file
-- purpose:     xml_deadlock_report events from the built-in system_health files (path taken from the server, never from the client)
-- min_version: 13
-- permission:  VIEW SERVER STATE
-- cost:        medium
-- timeout_s:   30
-- database:    master
DECLARE @dir nvarchar(512);
SET @dir = (SELECT TOP (1) c.path FROM sys.dm_os_server_diagnostics_log_configurations AS c);
-- The path may or may not end with a separator (Windows '\', Linux '/').
SET @dir = @dir + CASE WHEN RIGHT(@dir, 1) IN (N'\', N'/') THEN N'' WHEN CHARINDEX(N'/', @dir) > 0 THEN N'/' ELSE N'\' END;
SELECT TOP (200) CAST(f.event_data AS nvarchar(max)) AS EventXml
FROM sys.fn_xe_file_target_read_file(@dir + N'system_health*.xel', NULL, NULL, NULL) AS f
WHERE f.object_name = N'xml_deadlock_report';
