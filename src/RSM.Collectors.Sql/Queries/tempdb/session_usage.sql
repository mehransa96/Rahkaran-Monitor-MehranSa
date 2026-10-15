-- key:         sql.tempdb.session_usage
-- purpose:     Top sessions by tempdb allocation
-- min_version: 13
-- permission:  VIEW SERVER STATE
-- cost:        cheap
-- timeout_s:   10
-- database:    tempdb
SELECT TOP (10) CAST(s.session_id AS int) AS SessionId, s.login_name AS LoginName, s.host_name AS HostName, s.program_name AS ProgramName,
       (u.user_objects_alloc_page_count - u.user_objects_dealloc_page_count
        + u.internal_objects_alloc_page_count - u.internal_objects_dealloc_page_count) * 8 / 1024 AS NetMb
FROM sys.dm_db_session_space_usage AS u
JOIN sys.dm_exec_sessions AS s ON s.session_id = u.session_id
WHERE ISNULL(s.program_name, N'') <> N'RSM-ReadOnlyMonitor'
ORDER BY NetMb DESC;
