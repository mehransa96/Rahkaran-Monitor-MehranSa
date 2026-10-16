-- Example query pack file: read-only SELECT over catalog views (no Rahkaran table names assumed).
SELECT TOP (20)
       s.name + N'.' + t.name AS TableName,
       SUM(CASE WHEN ps.index_id IN (0, 1) THEN ps.row_count ELSE 0 END) AS [RowCount],
       CAST(SUM(ps.reserved_page_count) * 8 / 1024.0 AS decimal(18, 1)) AS ReservedMb
FROM sys.dm_db_partition_stats AS ps
JOIN sys.tables AS t ON t.object_id = ps.object_id
JOIN sys.schemas AS s ON s.schema_id = t.schema_id
GROUP BY s.name, t.name
ORDER BY ReservedMb DESC;
