-- key:         sql.sec.logins
-- purpose:     Logins: type, disabled, password policy, sa (sid 0x01)
-- min_version: 13
-- permission:  VIEW ANY DEFINITION
-- cost:        cheap
-- timeout_s:   10
-- database:    master
SELECT sp.name AS Name, sp.type_desc AS TypeDesc, CAST(sp.is_disabled AS int) AS IsDisabled, sp.default_database_name AS DefaultDatabase,
       CASE WHEN sp.sid = 0x01 THEN 1 ELSE 0 END AS IsSa,
       CAST(sl.is_policy_checked AS int) AS IsPolicyChecked, CAST(sl.is_expiration_checked AS int) AS IsExpirationChecked
FROM sys.server_principals AS sp
LEFT JOIN sys.sql_logins AS sl ON sl.principal_id = sp.principal_id
WHERE sp.type IN ('S', 'U', 'G') AND sp.name NOT LIKE N'##%';
