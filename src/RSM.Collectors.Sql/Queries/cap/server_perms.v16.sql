-- key:         cap.server_perms
-- purpose:     Same as cap.server_perms plus the SQL Server 2022 split permissions
-- min_version: 16
-- permission:  public
-- cost:        cheap
-- timeout_s:   10
-- database:    master
SELECT
    SUSER_SNAME()                                                     AS LoginName,
    HAS_PERMS_BY_NAME(NULL, NULL, N'VIEW SERVER STATE')               AS ViewServerState,
    HAS_PERMS_BY_NAME(NULL, NULL, N'VIEW SERVER PERFORMANCE STATE')   AS ViewServerPerformanceState,
    HAS_PERMS_BY_NAME(NULL, NULL, N'VIEW SERVER SECURITY STATE')      AS ViewServerSecurityState,
    HAS_PERMS_BY_NAME(NULL, NULL, N'VIEW ANY DEFINITION')             AS ViewAnyDefinition,
    HAS_PERMS_BY_NAME(NULL, NULL, N'CONNECT ANY DATABASE')            AS ConnectAnyDatabase,
    HAS_PERMS_BY_NAME(NULL, NULL, N'CONTROL SERVER')                  AS ControlServer,
    IS_SRVROLEMEMBER(N'sysadmin')                                     AS IsSysadmin,
    IS_SRVROLEMEMBER(N'securityadmin')                                AS IsSecurityAdmin;
