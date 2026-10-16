-- key:         topo.sql_clients
-- purpose:     User connections grouped by client host, program, login, database and client address (topology map, App-host balance)
-- min_version: 13
-- permission:  VIEW SERVER STATE
-- cost:        cheap
-- timeout_s:   10
-- database:    master
SELECT TOP (500)
       ISNULL(s.host_name, N'') AS HostName,
       ISNULL(s.program_name, N'') AS ProgramName,
       s.login_name AS LoginName,
       DB_NAME(s.database_id) AS DatabaseName,
       c.client_net_address AS ClientAddress,
       COUNT(*) AS Sessions,
       SUM(CASE WHEN s.status = N'running' THEN 1 ELSE 0 END) AS Running
FROM sys.dm_exec_sessions AS s
LEFT JOIN sys.dm_exec_connections AS c ON c.session_id = s.session_id AND c.parent_connection_id IS NULL
WHERE s.is_user_process = 1
  AND ISNULL(s.program_name, N'') <> N'RSM-ReadOnlyMonitor'
GROUP BY s.host_name, s.program_name, s.login_name, s.database_id, c.client_net_address
ORDER BY Sessions DESC;
