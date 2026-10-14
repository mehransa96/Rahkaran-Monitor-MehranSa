-- key:         cap.version
-- purpose:     Product version, level, edition for version gating and the report header
-- min_version: 13
-- permission:  public
-- cost:        cheap
-- timeout_s:   10
-- database:    master
SELECT
    CAST(SERVERPROPERTY('ProductVersion')     AS nvarchar(128)) AS ProductVersion,
    CAST(SERVERPROPERTY('ProductLevel')       AS nvarchar(128)) AS ProductLevel,
    CAST(SERVERPROPERTY('ProductUpdateLevel') AS nvarchar(128)) AS ProductUpdateLevel,
    CAST(SERVERPROPERTY('Edition')            AS nvarchar(256)) AS Edition,
    CAST(SERVERPROPERTY('EngineEdition')      AS int)           AS EngineEdition,
    CAST(SERVERPROPERTY('ServerName')         AS nvarchar(256)) AS ServerName,
    CAST(SERVERPROPERTY('IsClustered')        AS int)           AS IsClustered,
    CAST(SERVERPROPERTY('IsHadrEnabled')      AS int)           AS IsHadrEnabled,
    @@VERSION                                                   AS VersionText;
