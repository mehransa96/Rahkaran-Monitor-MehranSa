-- key:         sql.idx.missing
-- purpose:     Missing-index suggestions with improvement measure (display only — Rahkaran schema is vendor-owned)
-- min_version: 13
-- permission:  VIEW SERVER STATE
-- cost:        cheap
-- timeout_s:   10
-- database:    master
SELECT TOP (50) DB_NAME(d.database_id) AS DatabaseName, d.statement AS TableName, d.equality_columns AS EqualityColumns,
       d.inequality_columns AS InequalityColumns, d.included_columns AS IncludedColumns,
       gs.user_seeks AS UserSeeks, gs.user_scans AS UserScans, gs.avg_total_user_cost AS AvgTotalUserCost, gs.avg_user_impact AS AvgUserImpact,
       gs.last_user_seek AS LastUserSeek,
       gs.avg_total_user_cost * gs.avg_user_impact * (gs.user_seeks + gs.user_scans) AS ImprovementMeasure
FROM sys.dm_db_missing_index_group_stats AS gs
JOIN sys.dm_db_missing_index_groups AS g ON g.index_group_handle = gs.group_handle
JOIN sys.dm_db_missing_index_details AS d ON d.index_handle = g.index_handle
ORDER BY ImprovementMeasure DESC;
