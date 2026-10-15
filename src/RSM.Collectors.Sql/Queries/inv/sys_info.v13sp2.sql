-- key:         sql.inv.sys_info
-- purpose:     CPU, sockets/cores, NUMA, memory, start time, VM type
-- min_version: 13.0.5026
-- permission:  VIEW SERVER STATE
-- cost:        cheap
-- timeout_s:   10
-- database:    master
SELECT
    cpu_count                AS CpuCount,
    hyperthread_ratio        AS HyperthreadRatio,
    socket_count             AS SocketCount,
    cores_per_socket         AS CoresPerSocket,
    numa_node_count          AS NumaNodeCount,
    physical_memory_kb       AS PhysicalMemoryKb,
    committed_kb             AS CommittedKb,
    committed_target_kb      AS CommittedTargetKb,
    sqlserver_start_time     AS SqlServerStartTime,
    virtual_machine_type_desc AS VirtualMachineType,
    scheduler_count          AS SchedulerCount,
    max_workers_count        AS MaxWorkersCount,
    affinity_type_desc       AS AffinityType
FROM sys.dm_os_sys_info;
