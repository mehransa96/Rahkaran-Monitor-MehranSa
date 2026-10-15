-- key:         sql.inv.memory_dumps
-- purpose:     Memory/stack dumps produced by SQL Server
-- min_version: 13
-- permission:  VIEW SERVER STATE
-- cost:        cheap
-- timeout_s:   10
-- database:    master
SELECT filename AS FileName, creation_time AS CreationTime, size_in_bytes AS SizeInBytes
FROM sys.dm_server_memory_dumps;
