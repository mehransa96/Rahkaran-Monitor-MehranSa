-- key:         cap.agent_role
-- purpose:     SQL Agent reader role membership and job-table access (runs in msdb)
-- min_version: 13
-- permission:  CONNECT to msdb (user mapped in msdb)
-- cost:        cheap
-- timeout_s:   10
-- database:    msdb
SELECT
    IS_ROLEMEMBER(N'SQLAgentReaderRole')                          AS IsAgentReader,
    HAS_PERMS_BY_NAME(N'dbo.sysjobs',       N'OBJECT', N'SELECT') AS CanReadJobs,
    HAS_PERMS_BY_NAME(N'dbo.sysjobhistory', N'OBJECT', N'SELECT') AS CanReadJobHistory;
