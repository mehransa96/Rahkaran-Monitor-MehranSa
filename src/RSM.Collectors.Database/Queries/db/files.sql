-- key:         db.files
-- purpose:     Database files: size, used, growth type/value, max size
-- min_version: 13
-- permission:  CONNECT to the database
-- cost:        cheap
-- timeout_s:   15
-- database:    each
SELECT CAST(df.file_id AS int) AS FileId, df.type_desc AS TypeDesc, df.name AS Name, df.physical_name AS PhysicalName,
       CAST(df.size AS bigint) * 8 / 1024 AS SizeMb,
       CAST(FILEPROPERTY(df.name, 'SpaceUsed') AS bigint) * 8 / 1024 AS UsedMb,
       df.growth AS Growth, CAST(df.is_percent_growth AS int) AS IsPercentGrowth, df.max_size AS MaxSize
FROM sys.database_files AS df;
