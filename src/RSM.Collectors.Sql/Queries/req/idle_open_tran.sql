-- key:         sql.req.idle_open_tran
-- purpose:     Sleeping sessions that hold an open transaction (begin time, last statement)
-- min_version: 13
-- permission:  VIEW SERVER STATE
-- cost:        cheap
-- timeout_s:   10
-- database:    master
SELECT TOP (50) CAST(s.session_id AS int) AS SessionId, s.login_name AS LoginName, s.host_name AS HostName, s.program_name AS ProgramName,
       DB_NAME(s.database_id) AS DatabaseName, CAST(s.open_transaction_count AS int) AS OpenTranCount,
       s.last_request_end_time AS LastRequestEnd, tr.TranBegin AS TranBegin,
       DATEDIFF(SECOND, ISNULL(tr.TranBegin, s.last_request_end_time), GETDATE()) AS IdleTranAgeS, LEFT(t.text, 4000) AS LastStatement
FROM sys.dm_exec_sessions AS s
LEFT JOIN sys.dm_exec_connections AS c ON c.session_id = s.session_id
OUTER APPLY sys.dm_exec_sql_text(c.most_recent_sql_handle) AS t
OUTER APPLY (SELECT MIN(at.transaction_begin_time) AS TranBegin
             FROM sys.dm_tran_session_transactions AS st
             JOIN sys.dm_tran_active_transactions AS at ON at.transaction_id = st.transaction_id
             WHERE st.session_id = s.session_id) AS tr
WHERE s.is_user_process = 1
  AND s.status = N'sleeping'
  AND s.open_transaction_count > 0
  AND ISNULL(s.program_name, N'') <> N'RSM-ReadOnlyMonitor'
ORDER BY tr.TranBegin;
