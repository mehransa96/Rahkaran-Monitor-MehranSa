-- key:         sql.block.chains
-- purpose:     Blocking chains: head-blocker tree, wait resources, statement (running) or last statement (idle head), open-transaction age
-- min_version: 13
-- permission:  VIEW SERVER STATE
-- cost:        cheap
-- timeout_s:   10
-- database:    master
WITH req AS (
    SELECT CAST(r.session_id AS int) AS session_id, CAST(r.blocking_session_id AS int) AS blocking_session_id
    FROM sys.dm_exec_requests AS r
    WHERE r.blocking_session_id <> 0 AND r.blocking_session_id <> r.session_id
),
heads AS (
    SELECT DISTINCT q.blocking_session_id AS session_id
    FROM req AS q
    WHERE q.blocking_session_id NOT IN (SELECT q2.session_id FROM req AS q2)
),
tree AS (
    SELECT h.session_id, CAST(0 AS int) AS blocking_session_id, CAST(0 AS int) AS chain_level, h.session_id AS head_session_id
    FROM heads AS h
    UNION ALL
    SELECT q.session_id, q.blocking_session_id, t.chain_level + 1, t.head_session_id
    FROM req AS q
    JOIN tree AS t ON q.blocking_session_id = t.session_id
    WHERE t.chain_level < 32
)
SELECT TOP (500)
       t.head_session_id AS HeadSessionId, t.session_id AS SessionId, t.blocking_session_id AS BlockingSessionId, t.chain_level AS ChainLevel,
       s.login_name AS LoginName, s.host_name AS HostName, s.program_name AS ProgramName, DB_NAME(s.database_id) AS DatabaseName,
       s.status AS SessionStatus, r.status AS RequestStatus, r.command AS Command,
       r.wait_type AS WaitType, CAST(ISNULL(r.wait_time, 0) AS bigint) AS WaitMs, r.wait_resource AS WaitResource,
       CAST(s.open_transaction_count AS int) AS OpenTranCount, tr.TranBegin AS TranBegin, s.last_request_end_time AS LastRequestEnd,
       DATEDIFF(SECOND, tr.TranBegin, GETDATE()) AS TranAgeS,
       LEFT(CASE WHEN r.session_id IS NOT NULL AND rt.text IS NOT NULL
                 THEN SUBSTRING(rt.text, r.statement_start_offset / 2 + 1,
                      (CASE WHEN r.statement_end_offset = -1 THEN DATALENGTH(rt.text) ELSE r.statement_end_offset END - r.statement_start_offset) / 2 + 1)
                 ELSE ct.text END, 4000) AS StatementText
FROM tree AS t
JOIN sys.dm_exec_sessions AS s ON s.session_id = t.session_id
LEFT JOIN sys.dm_exec_requests AS r ON r.session_id = t.session_id
LEFT JOIN sys.dm_exec_connections AS c ON c.session_id = t.session_id
OUTER APPLY sys.dm_exec_sql_text(r.sql_handle) AS rt
OUTER APPLY sys.dm_exec_sql_text(c.most_recent_sql_handle) AS ct
OUTER APPLY (SELECT MIN(at.transaction_begin_time) AS TranBegin
             FROM sys.dm_tran_session_transactions AS st
             JOIN sys.dm_tran_active_transactions AS at ON at.transaction_id = st.transaction_id
             WHERE st.session_id = t.session_id) AS tr
WHERE ISNULL(s.program_name, N'') <> N'RSM-ReadOnlyMonitor'
ORDER BY t.head_session_id, t.chain_level, t.session_id;
