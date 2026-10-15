-- key:         db.detect.activity
-- purpose:     User sessions per database (input for Rahkaran DB auto-detection)
-- min_version: 13
-- permission:  VIEW SERVER STATE
-- cost:        cheap
-- timeout_s:   10
-- database:    master
SELECT DB_NAME(s.database_id) AS DatabaseName, COUNT(*) AS Sessions, COUNT(DISTINCT s.host_name) AS Hosts
FROM sys.dm_exec_sessions AS s
WHERE s.is_user_process = 1 AND ISNULL(s.program_name, N'') <> N'RSM-ReadOnlyMonitor'
GROUP BY s.database_id;
