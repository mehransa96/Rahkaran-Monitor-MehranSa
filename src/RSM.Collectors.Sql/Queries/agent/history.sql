-- key:         sql.agent.history
-- purpose:     Job outcomes and durations since @SinceDate (yyyymmdd)
-- min_version: 13
-- permission:  SELECT on msdb.dbo.sysjobhistory
-- cost:        medium
-- timeout_s:   30
-- database:    msdb
SELECT TOP (20000) h.job_id AS JobId, h.step_id AS StepId, h.run_status AS RunStatus, h.run_date AS RunDate, h.run_time AS RunTime,
       h.run_duration AS RunDuration, LEFT(h.message, 400) AS Message
FROM dbo.sysjobhistory AS h
WHERE h.run_date >= @SinceDate
ORDER BY h.instance_id DESC;
