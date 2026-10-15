-- key:         sql.cpu.ring_buffer
-- purpose:     Scheduler-monitor records (~1/min, ~4 h): SQL CPU % vs idle % → other-process CPU % (parsed client-side)
-- min_version: 13
-- permission:  VIEW SERVER STATE
-- cost:        medium
-- timeout_s:   10
-- database:    master
SELECT TOP (256) CAST(rb.record AS nvarchar(4000)) AS RecordXml, rb.[timestamp] AS RecordTicks, si.ms_ticks AS NowTicks
FROM sys.dm_os_ring_buffers AS rb
CROSS JOIN sys.dm_os_sys_info AS si
WHERE rb.ring_buffer_type = N'RING_BUFFER_SCHEDULER_MONITOR'
  AND rb.record LIKE N'%<SystemHealth>%'
ORDER BY rb.[timestamp] DESC;
