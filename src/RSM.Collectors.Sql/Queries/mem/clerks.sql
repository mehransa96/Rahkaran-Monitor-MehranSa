-- key:         sql.mem.clerks
-- purpose:     Top 20 memory clerks by size
-- min_version: 13
-- permission:  VIEW SERVER STATE
-- cost:        cheap
-- timeout_s:   10
-- database:    master
SELECT TOP (20) type AS ClerkType, SUM(pages_kb) / 1024 AS SizeMb
FROM sys.dm_os_memory_clerks
GROUP BY type
ORDER BY SUM(pages_kb) DESC;
