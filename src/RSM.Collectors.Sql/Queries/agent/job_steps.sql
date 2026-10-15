-- key:         sql.agent.job_steps
-- purpose:     Job step subsystem and command (shrink/backup/UNC/CDC detection; masked in exports)
-- min_version: 13
-- permission:  SELECT on msdb.dbo.sysjobsteps
-- cost:        cheap
-- timeout_s:   15
-- database:    msdb
SELECT js.job_id AS JobId, js.step_id AS StepId, js.step_name AS StepName, js.subsystem AS Subsystem,
       js.database_name AS DatabaseName, js.command AS Command
FROM dbo.sysjobsteps AS js;
