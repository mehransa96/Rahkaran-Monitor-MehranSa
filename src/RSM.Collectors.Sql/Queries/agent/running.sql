-- key:         sql.agent.running
-- purpose:     Jobs running right now
-- min_version: 13
-- permission:  SELECT on msdb.dbo.sysjobactivity, syssessions
-- cost:        cheap
-- timeout_s:   10
-- database:    msdb
SELECT ja.job_id AS JobId, ja.start_execution_date AS StartExecutionDate
FROM dbo.sysjobactivity AS ja
WHERE ja.session_id = (SELECT MAX(ss.session_id) FROM dbo.syssessions AS ss)
  AND ja.start_execution_date IS NOT NULL
  AND ja.stop_execution_date IS NULL;
