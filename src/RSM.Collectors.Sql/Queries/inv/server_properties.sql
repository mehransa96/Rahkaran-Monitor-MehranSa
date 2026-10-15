-- key:         sql.inv.server_properties
-- purpose:     Instance identity, edition, collation, clustering/HADR, auth mode, default paths
-- min_version: 13
-- permission:  public
-- cost:        cheap
-- timeout_s:   10
-- database:    master
SELECT
    CAST(SERVERPROPERTY('MachineName')              AS nvarchar(256)) AS MachineName,
    CAST(SERVERPROPERTY('ServerName')               AS nvarchar(256)) AS ServerName,
    CAST(SERVERPROPERTY('InstanceName')             AS nvarchar(256)) AS InstanceName,
    CAST(SERVERPROPERTY('ProductVersion')           AS nvarchar(128)) AS ProductVersion,
    CAST(SERVERPROPERTY('ProductLevel')             AS nvarchar(128)) AS ProductLevel,
    CAST(SERVERPROPERTY('ProductUpdateLevel')       AS nvarchar(128)) AS ProductUpdateLevel,
    CAST(SERVERPROPERTY('ProductUpdateReference')   AS nvarchar(128)) AS ProductUpdateReference,
    CAST(SERVERPROPERTY('Edition')                  AS nvarchar(256)) AS Edition,
    CAST(SERVERPROPERTY('EngineEdition')            AS int)           AS EngineEdition,
    CAST(SERVERPROPERTY('Collation')                AS nvarchar(256)) AS Collation,
    CAST(SERVERPROPERTY('IsClustered')              AS int)           AS IsClustered,
    CAST(SERVERPROPERTY('IsHadrEnabled')            AS int)           AS IsHadrEnabled,
    CAST(SERVERPROPERTY('IsIntegratedSecurityOnly') AS int)           AS IsIntegratedSecurityOnly,
    CAST(SERVERPROPERTY('InstanceDefaultDataPath')  AS nvarchar(512)) AS InstanceDefaultDataPath,
    CAST(SERVERPROPERTY('InstanceDefaultLogPath')   AS nvarchar(512)) AS InstanceDefaultLogPath,
    CAST(SERVERPROPERTY('ErrorLogFileName')         AS nvarchar(512)) AS ErrorLogFileName,
    CAST((SELECT MAX(compatibility_level) FROM sys.databases WHERE database_id = 1) AS int) AS NativeCompatibilityLevel;
