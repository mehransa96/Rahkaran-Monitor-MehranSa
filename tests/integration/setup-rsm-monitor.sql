-- CI only. Plays the customer's DBA: creates the least-privilege login exactly as PERMISSIONS.md describes.
-- RSM itself never runs this (and SqlGuard would reject it).
-- sqlcmd substitutes $(RSM_IT_PASSWORD) from the environment.
USE master;
CREATE LOGIN rsm_monitor WITH PASSWORD = N'$(RSM_IT_PASSWORD)', CHECK_POLICY = ON;
GRANT VIEW SERVER STATE    TO rsm_monitor;
GRANT VIEW ANY DEFINITION  TO rsm_monitor;
GRANT CONNECT ANY DATABASE TO rsm_monitor;
GO
USE msdb;
CREATE USER rsm_monitor FOR LOGIN rsm_monitor;
ALTER ROLE SQLAgentReaderRole ADD MEMBER rsm_monitor;
GRANT SELECT ON dbo.backupset          TO rsm_monitor;
GRANT SELECT ON dbo.backupmediafamily  TO rsm_monitor;
GRANT SELECT ON dbo.backupfile         TO rsm_monitor;
GRANT SELECT ON dbo.suspect_pages      TO rsm_monitor;
GRANT SELECT ON dbo.sysjobs            TO rsm_monitor;
GRANT SELECT ON dbo.sysjobsteps        TO rsm_monitor;
GRANT SELECT ON dbo.sysjobhistory      TO rsm_monitor;
GRANT SELECT ON dbo.sysjobactivity     TO rsm_monitor;
GRANT SELECT ON dbo.syssessions        TO rsm_monitor;
GRANT SELECT ON dbo.sysschedules       TO rsm_monitor;
GRANT SELECT ON dbo.sysjobschedules    TO rsm_monitor;
GRANT SELECT ON dbo.syscategories      TO rsm_monitor;
GRANT SELECT ON dbo.sysmaintplan_plans    TO rsm_monitor;
GRANT SELECT ON dbo.sysmaintplan_subplans TO rsm_monitor;
GO
