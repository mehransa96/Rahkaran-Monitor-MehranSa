-- key:         cap.db_access
-- purpose:     Databases on the instance and whether the RSM login can open each one
-- min_version: 13
-- permission:  public
-- cost:        cheap
-- timeout_s:   10
-- database:    master
SELECT
    d.name                  AS Name,
    d.database_id           AS DatabaseId,
    d.state_desc            AS StateDesc,
    HAS_DBACCESS(d.name)    AS HasAccess
FROM sys.databases AS d
ORDER BY d.database_id;
