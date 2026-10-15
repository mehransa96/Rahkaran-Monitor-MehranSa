-- key:         sql.io.pending
-- purpose:     Pending I/O requests right now
-- min_version: 13
-- permission:  VIEW SERVER STATE
-- cost:        cheap
-- timeout_s:   10
-- database:    master
SELECT COUNT(*) AS PendingCount, ISNULL(MAX(io_pending_ms_ticks), 0) AS MaxPendingMs
FROM sys.dm_io_pending_io_requests;
