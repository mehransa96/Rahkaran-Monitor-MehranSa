-- key:         cap.server_perms
-- purpose:     Effective server-level permissions and role flags of the RSM login (Capability Matrix)
-- min_version: 13
-- permission:  public
-- cost:        cheap
-- timeout_s:   10
-- database:    master
SELECT
    SUSER_SNAME()                                                     AS LoginName,
    HAS_PERMS_BY_NAME(NULL, NULL, N'VIEW SERVER STATE')               AS ViewServerState,
    CAST(NULL AS int)                                                 AS ViewServerPerformanceState,
    CAST(NULL AS int)                                                 AS ViewServerSecurityState,
    HAS_PERMS_BY_NAME(NULL, NULL, N'VIEW ANY DEFINITION')             AS ViewAnyDefinition,
    HAS_PERMS_BY_NAME(NULL, NULL, N'CONNECT ANY DATABASE')            AS ConnectAnyDatabase,
    HAS_PERMS_BY_NAME(NULL, NULL, N'CONTROL SERVER')                  AS ControlServer,
    IS_SRVROLEMEMBER(N'sysadmin')                                     AS IsSysadmin,
    IS_SRVROLEMEMBER(N'securityadmin')                                AS IsSecurityAdmin;
