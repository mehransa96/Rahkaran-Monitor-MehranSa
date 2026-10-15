-- key:         sql.sec.sysadmins
-- purpose:     Members of the sysadmin role
-- min_version: 13
-- permission:  VIEW ANY DEFINITION
-- cost:        cheap
-- timeout_s:   10
-- database:    master
SELECT m.name AS Name, m.type_desc AS TypeDesc, CAST(m.is_disabled AS int) AS IsDisabled
FROM sys.server_role_members AS rm
JOIN sys.server_principals AS r ON r.principal_id = rm.role_principal_id
JOIN sys.server_principals AS m ON m.principal_id = rm.member_principal_id
WHERE r.name = N'sysadmin';
