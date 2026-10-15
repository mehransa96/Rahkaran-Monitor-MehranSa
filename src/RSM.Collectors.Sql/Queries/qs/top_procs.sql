-- key:         sql.qs.top_procs
-- purpose:     Top-N cached stored procedures by total CPU
-- min_version: 13
-- permission:  VIEW SERVER STATE
-- cost:        medium
-- timeout_s:   20
-- database:    master
SELECT TOP (@Top) DB_NAME(ps.database_id) AS DatabaseName, OBJECT_SCHEMA_NAME(ps.object_id, ps.database_id) AS SchemaName,
       OBJECT_NAME(ps.object_id, ps.database_id) AS ProcedureName, ps.execution_count AS Executions,
       ps.total_worker_time / 1000 AS TotalCpuMs, ps.total_elapsed_time / 1000 AS TotalDurationMs,
       ps.total_logical_reads AS TotalLogicalReads, ps.total_logical_writes AS TotalWrites, ps.last_execution_time AS LastExecution
FROM sys.dm_exec_procedure_stats AS ps
ORDER BY ps.total_worker_time DESC;
