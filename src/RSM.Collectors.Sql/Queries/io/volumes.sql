-- key:         sql.io.volumes
-- purpose:     Volume free space and file system for every online database file
-- min_version: 13
-- permission:  VIEW SERVER STATE
-- cost:        cheap
-- timeout_s:   15
-- database:    master
SELECT mf.database_id AS DatabaseId, d.name AS DatabaseName, mf.file_id AS FileId, mf.type_desc AS TypeDesc, mf.physical_name AS PhysicalName,
       CAST(mf.size AS bigint) * 8 / 1024 AS SizeMb, vs.volume_mount_point AS VolumeMountPoint, vs.logical_volume_name AS LogicalVolumeName,
       vs.file_system_type AS FileSystemType, vs.total_bytes / 1048576 AS TotalMb, vs.available_bytes / 1048576 AS AvailableMb
FROM sys.master_files AS mf
JOIN sys.databases AS d ON d.database_id = mf.database_id AND d.state = 0
CROSS APPLY sys.dm_os_volume_stats(mf.database_id, mf.file_id) AS vs;
