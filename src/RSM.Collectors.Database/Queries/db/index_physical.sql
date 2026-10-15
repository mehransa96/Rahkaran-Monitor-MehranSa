-- key:         db.idx.physical
-- purpose:     Fragmentation of one table's indexes (LIMITED mode) — heavy, on demand only
-- min_version: 13
-- permission:  VIEW DATABASE STATE
-- cost:        heavy
-- timeout_s:   120
-- database:    rk
SELECT OBJECT_SCHEMA_NAME(ips.object_id) AS SchemaName, OBJECT_NAME(ips.object_id) AS TableName, i.name AS IndexName,
       ips.index_type_desc AS IndexType, CAST(ips.avg_fragmentation_in_percent AS float) AS FragmentationPct, ips.page_count AS PageCount
FROM sys.dm_db_index_physical_stats(DB_ID(), @ObjectId, NULL, NULL, 'LIMITED') AS ips
JOIN sys.indexes AS i ON i.object_id = ips.object_id AND i.index_id = ips.index_id
WHERE ips.page_count > 1000 AND ips.alloc_unit_type_desc = N'IN_ROW_DATA';
