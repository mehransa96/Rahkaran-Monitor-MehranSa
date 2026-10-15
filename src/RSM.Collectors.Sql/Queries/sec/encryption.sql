-- key:         sql.sec.encryption
-- purpose:     Connection encryption / auth scheme / transport distribution
-- min_version: 13
-- permission:  VIEW SERVER STATE
-- cost:        cheap
-- timeout_s:   10
-- database:    master
SELECT c.encrypt_option AS EncryptOption, c.auth_scheme AS AuthScheme, c.net_transport AS NetTransport, COUNT(*) AS Connections,
       SUM(CASE WHEN c.client_net_address IN (N'<local machine>', N'127.0.0.1', N'::1') THEN 0 ELSE 1 END) AS RemoteConnections
FROM sys.dm_exec_connections AS c
JOIN sys.dm_exec_sessions AS s ON s.session_id = c.session_id
WHERE ISNULL(s.program_name, N'') <> N'RSM-ReadOnlyMonitor'
GROUP BY c.encrypt_option, c.auth_scheme, c.net_transport;
