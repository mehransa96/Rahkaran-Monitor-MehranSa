-- key:         db.size.top_tables
-- purpose:     Top 50 tables by reserved space
-- min_version: 13
-- permission:  VIEW DATABASE STATE
-- cost:        medium
-- timeout_s:   60
-- database:    rk
SELECT TOP (50) o.object_id AS ObjectId, s.name AS SchemaName, o.name AS TableName,
       SUM(ps.reserved_page_count) * 8 / 1024 AS ReservedMb, SUM(ps.used_page_count) * 8 / 1024 AS UsedMb,
       SUM(CASE WHEN ps.index_id IN (0, 1) THEN ps.row_count ELSE 0 END) AS RowCnt
FROM sys.dm_db_partition_stats AS ps
JOIN sys.objects AS o ON o.object_id = ps.object_id
JOIN sys.schemas AS s ON s.schema_id = o.schema_id
WHERE o.is_ms_shipped = 0
GROUP BY o.object_id, s.name, o.name
ORDER BY SUM(ps.reserved_page_count) DESC;
