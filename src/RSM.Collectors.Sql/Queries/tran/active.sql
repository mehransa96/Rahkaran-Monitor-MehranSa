-- key:         sql.tran.active
-- purpose:     Open transactions: begin time, type, state, session, log bytes used/reserved per database
-- min_version: 13
-- permission:  VIEW SERVER STATE
-- cost:        cheap
-- timeout_s:   10
-- database:    master
SELECT TOP (100) CAST(st.session_id AS int) AS SessionId, s.login_name AS LoginName, s.host_name AS HostName, s.program_name AS ProgramName,
       s.status AS SessionStatus, at.transaction_id AS TransactionId, at.name AS TransactionName, at.transaction_begin_time AS BeginTime,
       DATEDIFF(SECOND, at.transaction_begin_time, GETDATE()) AS AgeS,
       CAST(at.transaction_type AS int) AS TransactionType, CAST(at.transaction_state AS int) AS TransactionState,
       DB_NAME(dt.database_id) AS DatabaseName, dt.database_transaction_log_bytes_used AS LogBytesUsed,
       dt.database_transaction_log_bytes_reserved AS LogBytesReserved
FROM sys.dm_tran_session_transactions AS st
JOIN sys.dm_tran_active_transactions AS at ON at.transaction_id = st.transaction_id
JOIN sys.dm_exec_sessions AS s ON s.session_id = st.session_id
LEFT JOIN sys.dm_tran_database_transactions AS dt ON dt.transaction_id = at.transaction_id AND dt.database_id <> 32767
WHERE s.is_user_process = 1
  AND ISNULL(s.program_name, N'') <> N'RSM-ReadOnlyMonitor'
ORDER BY at.transaction_begin_time;
