-- key:         sql.req.active
-- purpose:     Running requests: statement (offset extraction), waits, blocking, CPU, I/O, grant, DOP, % complete, open transactions
-- min_version: 13
-- permission:  VIEW SERVER STATE
-- cost:        cheap
-- timeout_s:   10
-- database:    master
SELECT TOP (200)
       CAST(r.session_id AS int) AS SessionId, s.login_name AS LoginName, s.host_name AS HostName, s.program_name AS ProgramName,
       DB_NAME(r.database_id) AS DatabaseName, r.status AS Status, r.command AS Command,
       r.wait_type AS WaitType, CAST(r.wait_time AS bigint) AS WaitMs, r.wait_resource AS WaitResource,
       CAST(r.blocking_session_id AS int) AS BlockingSessionId,
       CAST(r.cpu_time AS bigint) AS CpuMs, r.reads AS Reads, r.writes AS Writes, r.logical_reads AS LogicalReads,
       CAST(r.total_elapsed_time AS bigint) AS ElapsedMs, CAST(r.granted_query_memory AS bigint) * 8 AS GrantedKb,
       CAST(r.dop AS int) AS Dop, CAST(r.percent_complete AS float) AS PercentComplete, CAST(r.open_transaction_count AS int) AS OpenTranCount,
       r.start_time AS StartTime, r.plan_handle AS PlanHandle, r.statement_start_offset AS StatementStart, r.statement_end_offset AS StatementEnd,
       LEFT(SUBSTRING(t.text, r.statement_start_offset / 2 + 1,
            (CASE WHEN r.statement_end_offset = -1 THEN DATALENGTH(t.text) ELSE r.statement_end_offset END - r.statement_start_offset) / 2 + 1), 4000) AS StatementText
FROM sys.dm_exec_requests AS r
JOIN sys.dm_exec_sessions AS s ON s.session_id = r.session_id
OUTER APPLY sys.dm_exec_sql_text(r.sql_handle) AS t
WHERE s.is_user_process = 1
  AND r.session_id <> @@SPID
  AND ISNULL(s.program_name, N'') <> N'RSM-ReadOnlyMonitor'
ORDER BY r.total_elapsed_time DESC;
