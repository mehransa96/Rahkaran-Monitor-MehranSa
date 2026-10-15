-- key:         sql.sec.guest
-- purpose:     guest user has CONNECT in this database
-- min_version: 13
-- permission:  VIEW ANY DEFINITION
-- cost:        cheap
-- timeout_s:   10
-- database:    each
SELECT COUNT(*) AS GuestConnect
FROM sys.database_permissions AS p
WHERE p.grantee_principal_id = DATABASE_PRINCIPAL_ID(N'guest')
  AND p.permission_name = N'CONNECT'
  AND p.state IN ('G', 'W');
