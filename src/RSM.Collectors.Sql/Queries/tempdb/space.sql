-- key:         sql.tempdb.space
-- purpose:     tempdb free space, version store, user and internal objects
-- min_version: 13
-- permission:  VIEW SERVER STATE
-- cost:        cheap
-- timeout_s:   10
-- database:    tempdb
SELECT SUM(unallocated_extent_page_count) * 8 / 1024       AS FreeMb,
       SUM(version_store_reserved_page_count) * 8 / 1024   AS VersionStoreMb,
       SUM(user_object_reserved_page_count) * 8 / 1024     AS UserObjectsMb,
       SUM(internal_object_reserved_page_count) * 8 / 1024 AS InternalObjectsMb
FROM sys.dm_db_file_space_usage;
