-- CI only, run as sa before and after the RSM diagnosis: everything RSM could conceivably change.
-- The two outputs must be identical (read-only proof).
SET NOCOUNT ON;
SELECT 'cfg', name, CAST(value AS nvarchar(50)), CAST(value_in_use AS nvarchar(50)) FROM sys.configurations ORDER BY name;
SELECT 'db', name, state_desc, recovery_model_desc, page_verify_option_desc, CAST(is_auto_shrink_on AS char(1)), CAST(compatibility_level AS varchar(5)) FROM sys.databases ORDER BY name;
-- File sizes are left out: tempdb grows under any query load and AUTO_SHRINK (seeded) shrinks in the background.
SELECT 'file', DB_NAME(database_id), name, CAST(growth AS varchar(20)), CAST(is_percent_growth AS char(1)), CAST(max_size AS varchar(20)) FROM sys.master_files ORDER BY database_id, file_id;
SELECT 'login', name, CAST(is_disabled AS char(1)), CONVERT(varchar(30), modify_date, 126) FROM sys.server_principals ORDER BY name;
SELECT 'job', name, CAST(enabled AS char(1)), CONVERT(varchar(30), date_modified, 126) FROM msdb.dbo.sysjobs ORDER BY name;
SELECT 'jobstep', j.name, s.step_id, s.command FROM msdb.dbo.sysjobsteps s JOIN msdb.dbo.sysjobs j ON j.job_id = s.job_id ORDER BY j.name, s.step_id;
SELECT 'backups', COUNT(*) FROM msdb.dbo.backupset;
SELECT 'objects', COUNT(*), CONVERT(varchar(30), MAX(modify_date), 126) FROM Rahkaran.sys.objects;
SELECT 'rows', COUNT(*) FROM Rahkaran.dbo.Sample;
SELECT 'traces', COUNT(*) FROM sys.traces;
SELECT 'xe', COUNT(*) FROM sys.server_event_sessions;
