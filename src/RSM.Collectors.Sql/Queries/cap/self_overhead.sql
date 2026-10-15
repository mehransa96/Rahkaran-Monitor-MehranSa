-- key:          cap.self_overhead
-- purpose:      RSM's own load: CPU, reads and memory of the sessions named RSM-ReadOnlyMonitor (the only view that looks AT our sessions)
-- min_version:  13
-- permission:   VIEW SERVER STATE
-- cost:         cheap
-- timeout_s:    10
-- database:     master
-- own_sessions: true
SELECT COUNT(*) AS Sessions, ISNULL(SUM(CAST(s.cpu_time AS bigint)), 0) AS CpuMs, ISNULL(SUM(s.reads), 0) AS Reads,
       ISNULL(SUM(CAST(s.memory_usage AS bigint)), 0) * 8 AS MemoryKb,
       (SELECT si.cpu_count FROM sys.dm_os_sys_info AS si) AS CpuCount, (SELECT si.ms_ticks FROM sys.dm_os_sys_info AS si) AS MsTicks
FROM sys.dm_exec_sessions AS s
WHERE s.program_name = N'RSM-ReadOnlyMonitor';
