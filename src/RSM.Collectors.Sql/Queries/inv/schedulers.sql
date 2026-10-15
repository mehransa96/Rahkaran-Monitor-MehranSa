-- key:         sql.inv.schedulers
-- purpose:     Online/offline visible schedulers (edition or affinity limits)
-- min_version: 13
-- permission:  VIEW SERVER STATE
-- cost:        cheap
-- timeout_s:   10
-- database:    master
SELECT
    SUM(CASE WHEN s.status = N'VISIBLE ONLINE'  THEN 1 ELSE 0 END) AS VisibleOnline,
    SUM(CASE WHEN s.status = N'VISIBLE OFFLINE' THEN 1 ELSE 0 END) AS VisibleOffline,
    SUM(s.runnable_tasks_count)                                    AS RunnableTasks,
    (SELECT COUNT(*) FROM sys.dm_os_nodes AS n WHERE n.node_state_desc NOT LIKE N'%DAC%') AS NodeCount
FROM sys.dm_os_schedulers AS s
WHERE s.status LIKE N'VISIBLE%';
