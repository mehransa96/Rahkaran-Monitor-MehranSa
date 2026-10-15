-- key:         db.stats.properties
-- purpose:     Stale statistics: >20 % rows modified (top 50 by modifications + total count)
-- min_version: 13
-- permission:  VIEW DATABASE STATE
-- cost:        medium
-- timeout_s:   60
-- database:    rk
SELECT TOP (50) OBJECT_SCHEMA_NAME(st.object_id) AS SchemaName, OBJECT_NAME(st.object_id) AS TableName, st.name AS StatName,
       sp.last_updated AS LastUpdated, CAST(sp.rows AS bigint) AS RowCnt, CAST(sp.modification_counter AS bigint) AS ModificationCounter,
       COUNT(*) OVER () AS TotalStale
FROM sys.stats AS st
CROSS APPLY sys.dm_db_stats_properties(st.object_id, st.stats_id) AS sp
WHERE OBJECTPROPERTY(st.object_id, 'IsUserTable') = 1
  AND sp.rows > 1000
  AND sp.modification_counter > sp.rows / 5
ORDER BY sp.modification_counter DESC;
