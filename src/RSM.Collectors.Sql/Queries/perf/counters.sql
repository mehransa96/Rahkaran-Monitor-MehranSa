-- key:         sql.perf.counters
-- purpose:     Fixed list of SQL Server performance counters (raw values + cntr_type; per-second and ratio math client-side)
-- min_version: 13
-- permission:  VIEW SERVER STATE
-- cost:        cheap
-- timeout_s:   10
-- database:    master
SELECT RTRIM(pc.object_name) AS ObjectName, RTRIM(pc.counter_name) AS CounterName, RTRIM(pc.instance_name) AS InstanceName,
       pc.cntr_value AS RawValue, CAST(pc.cntr_type AS int) AS CounterType,
       (SELECT si.sqlserver_start_time FROM sys.dm_os_sys_info AS si) AS StartTime
FROM sys.dm_os_performance_counters AS pc
WHERE (pc.object_name LIKE N'%:Buffer Manager%' AND pc.counter_name IN (N'Page life expectancy', N'Lazy writes/sec', N'Free list stalls/sec', N'Checkpoint pages/sec'))
   OR (pc.object_name LIKE N'%:Buffer Node%' AND pc.counter_name = N'Page life expectancy')
   OR (pc.object_name LIKE N'%:SQL Statistics%' AND pc.counter_name IN (N'Batch Requests/sec', N'SQL Compilations/sec', N'SQL Re-Compilations/sec'))
   OR (pc.object_name LIKE N'%:Memory Manager%' AND pc.counter_name IN (N'Memory Grants Pending', N'Memory Grants Outstanding', N'Target Server Memory (KB)', N'Total Server Memory (KB)'))
   OR (pc.object_name LIKE N'%:General Statistics%' AND pc.counter_name IN (N'User Connections', N'Processes blocked'))
   OR (pc.object_name LIKE N'%:Locks%' AND pc.instance_name = N'_Total' AND pc.counter_name IN (N'Lock Waits/sec', N'Lock Timeouts/sec', N'Number of Deadlocks/sec'))
   OR (pc.object_name LIKE N'%:Access Methods%' AND pc.counter_name IN (N'Full Scans/sec', N'Page Splits/sec'))
   OR (pc.object_name LIKE N'%:Databases%' AND pc.counter_name IN (N'Log Flushes/sec', N'Log Flush Wait Time', N'Transactions/sec', N'Log Growths', N'Log Shrinks'))
   OR (pc.object_name LIKE N'%:SQL Errors%' AND pc.instance_name = N'_Total' AND pc.counter_name = N'Errors/sec');
