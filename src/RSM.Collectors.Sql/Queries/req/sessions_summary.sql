-- key:         sql.req.sessions_summary
-- purpose:     User sessions grouped by host, program, login and database (topology, ARR balance)
-- min_version: 13
-- permission:  VIEW SERVER STATE
-- cost:        cheap
-- timeout_s:   10
-- database:    master
SELECT TOP (200) s.host_name AS HostName, s.program_name AS ProgramName, s.login_name AS LoginName, DB_NAME(s.database_id) AS DatabaseName,
       COUNT(*) AS Sessions, SUM(CASE WHEN s.status = N'running' THEN 1 ELSE 0 END) AS Running,
       SUM(CASE WHEN s.open_transaction_count > 0 THEN 1 ELSE 0 END) AS WithOpenTran
FROM sys.dm_exec_sessions AS s
WHERE s.is_user_process = 1
  AND ISNULL(s.program_name, N'') <> N'RSM-ReadOnlyMonitor'
GROUP BY s.host_name, s.program_name, s.login_name, s.database_id
ORDER BY Sessions DESC;
