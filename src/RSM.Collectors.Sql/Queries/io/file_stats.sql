-- key:         sql.io.file_stats
-- purpose:     Cumulative I/O per database file since startup (averages; live deltas in P2)
-- min_version: 13
-- permission:  VIEW SERVER STATE
-- cost:        cheap
-- timeout_s:   10
-- database:    master
SELECT DB_NAME(vfs.database_id) AS DatabaseName, CAST(vfs.database_id AS int) AS DatabaseId, CAST(vfs.file_id AS int) AS FileId, mf.type_desc AS TypeDesc,
       mf.physical_name AS PhysicalName, vfs.num_of_reads AS NumOfReads, vfs.num_of_writes AS NumOfWrites,
       vfs.io_stall_read_ms AS IoStallReadMs, vfs.io_stall_write_ms AS IoStallWriteMs,
       vfs.num_of_bytes_read AS BytesRead, vfs.num_of_bytes_written AS BytesWritten
FROM sys.dm_io_virtual_file_stats(NULL, NULL) AS vfs
JOIN sys.master_files AS mf ON mf.database_id = vfs.database_id AND mf.file_id = vfs.file_id;
