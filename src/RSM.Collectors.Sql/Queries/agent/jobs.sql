-- key:         sql.agent.jobs
-- purpose:     Agent jobs with owner, category and schedules
-- min_version: 13
-- permission:  SELECT on msdb sysjobs/sysjobschedules/sysschedules/syscategories
-- cost:        cheap
-- timeout_s:   15
-- database:    msdb
SELECT j.job_id AS JobId, j.name AS Name, CAST(j.enabled AS int) AS Enabled, SUSER_SNAME(j.owner_sid) AS Owner, c.name AS Category,
       s.name AS ScheduleName, CAST(s.enabled AS int) AS ScheduleEnabled, s.freq_type AS FreqType, s.freq_interval AS FreqInterval,
       s.freq_subday_type AS FreqSubdayType, s.freq_subday_interval AS FreqSubdayInterval, s.active_start_time AS ActiveStartTime
FROM dbo.sysjobs AS j
LEFT JOIN dbo.syscategories AS c ON c.category_id = j.category_id
LEFT JOIN dbo.sysjobschedules AS js ON js.job_id = j.job_id
LEFT JOIN dbo.sysschedules AS s ON s.schedule_id = js.schedule_id;
