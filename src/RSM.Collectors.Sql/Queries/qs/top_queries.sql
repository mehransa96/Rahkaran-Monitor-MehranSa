-- key:         sql.qs.top_queries
-- purpose:     Top-N cached statements by @OrderBy (1 CPU, 2 duration, 3 logical reads, 4 writes, 5 executions), RSM's own batches excluded
-- min_version: 13
-- permission:  VIEW SERVER STATE
-- cost:        medium
-- timeout_s:   20
-- database:    master
SELECT TOP (@Top)
       qs.plan_handle AS PlanHandle, qs.statement_start_offset AS StatementStart, qs.statement_end_offset AS StatementEnd,
       qs.query_hash AS QueryHash, DB_NAME(CAST(pa.value AS int)) AS DatabaseName,
       qs.execution_count AS Executions, qs.total_worker_time / 1000 AS TotalCpuMs, qs.total_elapsed_time / 1000 AS TotalDurationMs,
       qs.total_logical_reads AS TotalLogicalReads, qs.total_logical_writes AS TotalWrites,
       qs.max_elapsed_time / 1000 AS MaxDurationMs, qs.creation_time AS CreationTime, qs.last_execution_time AS LastExecution,
       CAST(NULL AS bigint) AS TotalSpills, CAST(NULL AS bigint) AS MaxGrantKb,
       LEFT(SUBSTRING(t.text, qs.statement_start_offset / 2 + 1,
            (CASE WHEN qs.statement_end_offset = -1 THEN DATALENGTH(t.text) ELSE qs.statement_end_offset END - qs.statement_start_offset) / 2 + 1), 4000) AS StatementText
FROM sys.dm_exec_query_stats AS qs
OUTER APPLY sys.dm_exec_sql_text(qs.sql_handle) AS t
OUTER APPLY (SELECT TOP (1) a.value FROM sys.dm_exec_plan_attributes(qs.plan_handle) AS a WHERE a.attribute = N'dbid') AS pa
WHERE ISNULL(t.text, N'') NOT LIKE N'SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED; SET LOCK_TIMEOUT 2000; SET DEADLOCK_PRIORITY LOW;%'
ORDER BY CASE @OrderBy WHEN 1 THEN qs.total_worker_time WHEN 2 THEN qs.total_elapsed_time WHEN 3 THEN qs.total_logical_reads
                       WHEN 4 THEN qs.total_logical_writes ELSE qs.execution_count END DESC;
