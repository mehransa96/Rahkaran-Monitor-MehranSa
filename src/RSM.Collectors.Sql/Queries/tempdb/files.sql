-- key:         sql.tempdb.files
-- purpose:     tempdb files: count, size, growth settings
-- min_version: 13
-- permission:  public
-- cost:        cheap
-- timeout_s:   10
-- database:    tempdb
SELECT file_id AS FileId, type_desc AS TypeDesc, name AS Name, physical_name AS PhysicalName,
       CAST(size AS bigint) * 8 / 1024 AS SizeMb, growth AS Growth, CAST(is_percent_growth AS int) AS IsPercentGrowth, max_size AS MaxSize
FROM sys.database_files;
