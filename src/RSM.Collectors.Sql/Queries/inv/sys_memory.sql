-- key:         sql.inv.sys_memory
-- purpose:     OS memory state as seen by SQL Server
-- min_version: 13
-- permission:  VIEW SERVER STATE
-- cost:        cheap
-- timeout_s:   10
-- database:    master
SELECT total_physical_memory_kb AS TotalPhysicalMemoryKb, available_physical_memory_kb AS AvailablePhysicalMemoryKb,
       total_page_file_kb AS TotalPageFileKb, available_page_file_kb AS AvailablePageFileKb,
       system_memory_state_desc AS SystemMemoryState
FROM sys.dm_os_sys_memory;
