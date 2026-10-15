-- key:         sql.inv.sys_info
-- purpose:     CPU, NUMA, memory, start time, VM type (2016 RTM/SP1 column set)
-- min_version: 13
-- permission:  VIEW SERVER STATE
-- cost:        cheap
-- timeout_s:   10
-- database:    master
SELECT
    cpu_count                AS CpuCount,
    hyperthread_ratio        AS HyperthreadRatio,
    CAST(NULL AS int)        AS SocketCount,
    CAST(NULL AS int)        AS CoresPerSocket,
    CAST(NULL AS int)        AS NumaNodeCount,
    physical_memory_kb       AS PhysicalMemoryKb,
    committed_kb             AS CommittedKb,
    committed_target_kb      AS CommittedTargetKb,
    sqlserver_start_time     AS SqlServerStartTime,
    virtual_machine_type_desc AS VirtualMachineType,
    scheduler_count          AS SchedulerCount,
    max_workers_count        AS MaxWorkersCount,
    affinity_type_desc       AS AffinityType
FROM sys.dm_os_sys_info;
