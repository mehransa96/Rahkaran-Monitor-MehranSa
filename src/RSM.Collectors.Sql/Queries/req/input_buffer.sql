-- key:         sql.req.input_buffer
-- purpose:     Last batch submitted by one session (on click / head blocker)
-- min_version: 13
-- permission:  VIEW SERVER STATE
-- cost:        cheap
-- timeout_s:   10
-- database:    master
SELECT LEFT(ib.event_info, 8000) AS EventInfo, ib.event_type AS EventType
FROM sys.dm_exec_sessions AS s
CROSS APPLY sys.dm_exec_input_buffer(s.session_id, NULL) AS ib
WHERE s.session_id = @SessionId
  AND ISNULL(s.program_name, N'') <> N'RSM-ReadOnlyMonitor';
