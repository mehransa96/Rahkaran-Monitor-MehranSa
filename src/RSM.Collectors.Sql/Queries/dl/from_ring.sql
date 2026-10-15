-- key:         sql.dl.from_ring
-- purpose:     Fallback: system_health ring_buffer target (events parsed client-side)
-- min_version: 13
-- permission:  VIEW SERVER STATE
-- cost:        medium
-- timeout_s:   30
-- database:    master
SELECT CAST(t.target_data AS nvarchar(max)) AS TargetXml
FROM sys.dm_xe_session_targets AS t
JOIN sys.dm_xe_sessions AS x ON x.address = t.event_session_address
WHERE x.name = N'system_health'
  AND t.target_name = N'ring_buffer';
