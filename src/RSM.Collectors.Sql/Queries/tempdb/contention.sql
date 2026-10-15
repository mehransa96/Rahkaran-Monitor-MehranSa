-- key:         sql.tempdb.contention
-- purpose:     PAGELATCH waits on tempdb pages right now
-- min_version: 13
-- permission:  VIEW SERVER STATE
-- cost:        cheap
-- timeout_s:   10
-- database:    master
SELECT COUNT(*) AS Waiters, ISNULL(MAX(wt.wait_duration_ms), 0) AS MaxWaitMs
FROM sys.dm_os_waiting_tasks AS wt
WHERE wt.wait_type LIKE N'PAGELATCH[_]%' AND wt.resource_description LIKE N'2:%';
