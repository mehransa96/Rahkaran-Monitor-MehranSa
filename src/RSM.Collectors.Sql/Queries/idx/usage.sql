-- key:         sql.idx.usage
-- purpose:     Index reads vs writes since restart for user tables (unused = writes without reads; display only)
-- min_version: 13
-- permission:  VIEW DATABASE STATE
-- cost:        medium
-- timeout_s:   20
-- database:    rk
SELECT TOP (200) OBJECT_SCHEMA_NAME(i.object_id) AS SchemaName, o.name AS TableName, i.name AS IndexName, i.type_desc AS IndexType,
       i.is_primary_key AS IsPrimaryKey, i.is_unique AS IsUnique,
       ISNULL(u.user_seeks, 0) AS UserSeeks, ISNULL(u.user_scans, 0) AS UserScans, ISNULL(u.user_lookups, 0) AS UserLookups,
       ISNULL(u.user_updates, 0) AS UserUpdates, u.last_user_seek AS LastUserSeek, u.last_user_scan AS LastUserScan
FROM sys.indexes AS i
JOIN sys.objects AS o ON o.object_id = i.object_id AND o.is_ms_shipped = 0 AND o.type = 'U'
LEFT JOIN sys.dm_db_index_usage_stats AS u ON u.object_id = i.object_id AND u.index_id = i.index_id AND u.database_id = DB_ID()
WHERE i.index_id > 0
ORDER BY ISNULL(u.user_seeks, 0) + ISNULL(u.user_scans, 0) + ISNULL(u.user_lookups, 0), ISNULL(u.user_updates, 0) DESC;
