-- key:         sql.agent.cdc_jobs
-- purpose:     CDC capture/cleanup job registration (table exists only once CDC was used)
-- min_version: 13
-- permission:  SELECT on msdb.dbo.cdc_jobs
-- cost:        cheap
-- timeout_s:   10
-- database:    msdb
IF OBJECT_ID(N'dbo.cdc_jobs') IS NOT NULL
    SELECT cj.database_id AS DatabaseId, cj.job_type AS JobType, cj.job_id AS JobId FROM dbo.cdc_jobs AS cj;
ELSE
    SELECT CAST(NULL AS int) AS DatabaseId, CAST(NULL AS nvarchar(20)) AS JobType, CAST(NULL AS uniqueidentifier) AS JobId WHERE 1 = 0;
