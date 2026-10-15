-- key:         sql.qs.plan_cache_health
-- purpose:     Plan cache size by object type and single-use plan share (ties to optimize for ad hoc workloads)
-- min_version: 13
-- permission:  VIEW SERVER STATE
-- cost:        medium
-- timeout_s:   20
-- database:    master
SELECT cp.objtype AS ObjType, COUNT_BIG(*) AS Plans, SUM(CAST(cp.size_in_bytes AS bigint)) AS Bytes,
       SUM(CASE WHEN cp.usecounts = 1 THEN CAST(1 AS bigint) ELSE 0 END) AS SingleUsePlans,
       SUM(CASE WHEN cp.usecounts = 1 THEN CAST(cp.size_in_bytes AS bigint) ELSE 0 END) AS SingleUseBytes
FROM sys.dm_exec_cached_plans AS cp
GROUP BY cp.objtype;
