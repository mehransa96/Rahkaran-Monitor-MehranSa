-- key:         db.cdc.log_scan
-- purpose:     Latest CDC log scan session: latency, errors (CDC-enabled databases only)
-- min_version: 13
-- permission:  VIEW DATABASE STATE
-- cost:        cheap
-- timeout_s:   10
-- database:    each
SELECT TOP (1) ls.start_time AS StartTime, ls.end_time AS EndTime, ls.duration AS DurationSeconds, ls.scan_phase AS ScanPhase,
       ls.error_count AS ErrorCount, ls.latency AS LatencySeconds, ls.failed_sessions_count AS FailedSessions
FROM sys.dm_cdc_log_scan_sessions AS ls
ORDER BY ls.session_id DESC;
