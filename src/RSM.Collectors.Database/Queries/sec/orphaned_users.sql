-- key:         sql.sec.orphaned_users
-- purpose:     Database users of instance logins whose login no longer exists
-- min_version: 13
-- permission:  VIEW ANY DEFINITION
-- cost:        cheap
-- timeout_s:   10
-- database:    each
SELECT dp.name AS Name, dp.type_desc AS TypeDesc
FROM sys.database_principals AS dp
LEFT JOIN sys.server_principals AS sp ON sp.sid = dp.sid
WHERE dp.type IN ('S', 'U', 'G')
  AND dp.authentication_type_desc = N'INSTANCE'
  AND dp.principal_id > 4
  AND sp.sid IS NULL;
