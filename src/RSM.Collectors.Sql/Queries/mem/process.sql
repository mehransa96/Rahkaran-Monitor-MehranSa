-- key:         sql.mem.process
-- purpose:     SQL Server process memory, locked pages, memory-low flags
-- min_version: 13
-- permission:  VIEW SERVER STATE
-- cost:        cheap
-- timeout_s:   10
-- database:    master
SELECT physical_memory_in_use_kb AS PhysicalMemoryInUseKb, locked_page_allocations_kb AS LockedPageAllocationsKb,
       large_page_allocations_kb AS LargePageAllocationsKb, memory_utilization_percentage AS MemoryUtilizationPercentage,
       CAST(process_physical_memory_low AS int) AS ProcessPhysicalMemoryLow, CAST(process_virtual_memory_low AS int) AS ProcessVirtualMemoryLow
FROM sys.dm_os_process_memory;
