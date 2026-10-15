-- key:         sql.block.locks_summary
-- purpose:     Lock counts by session, resource type and mode (on demand, top 100)
-- min_version: 13
-- permission:  VIEW SERVER STATE
-- cost:        medium
-- timeout_s:   15
-- database:    master
SELECT TOP (100) CAST(l.request_session_id AS int) AS SessionId, l.resource_type AS ResourceType, DB_NAME(l.resource_database_id) AS DatabaseName,
       l.request_mode AS RequestMode, l.request_status AS RequestStatus, COUNT_BIG(*) AS LockCount
FROM sys.dm_tran_locks AS l
JOIN sys.dm_exec_sessions AS s ON s.session_id = l.request_session_id
WHERE s.is_user_process = 1
  AND ISNULL(s.program_name, N'') <> N'RSM-ReadOnlyMonitor'
GROUP BY l.request_session_id, l.resource_type, l.resource_database_id, l.request_mode, l.request_status
ORDER BY LockCount DESC;
