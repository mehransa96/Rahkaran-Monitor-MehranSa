-- key:         sql.waits.waiting_tasks
-- purpose:     Tasks waiting right now (wait type, duration, resource) — tempdb allocation-page latches, blocking context
-- min_version: 13
-- permission:  VIEW SERVER STATE
-- cost:        cheap
-- timeout_s:   10
-- database:    master
SELECT TOP (200) CAST(wt.session_id AS int) AS SessionId, wt.wait_type AS WaitType, wt.wait_duration_ms AS WaitMs,
       CAST(ISNULL(wt.blocking_session_id, 0) AS int) AS BlockingSessionId, LEFT(wt.resource_description, 400) AS ResourceDescription
FROM sys.dm_os_waiting_tasks AS wt
JOIN sys.dm_exec_sessions AS s ON s.session_id = wt.session_id
WHERE s.is_user_process = 1
  AND ISNULL(s.program_name, N'') <> N'RSM-ReadOnlyMonitor'
ORDER BY wt.wait_duration_ms DESC;
