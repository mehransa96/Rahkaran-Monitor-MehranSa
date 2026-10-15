-- CI only, run as sa BEFORE RSM connects. Plays a customer server that deviates from the ستاد فنی baseline,
-- so the integration test can check that a full diagnosis reports exactly these problems.
-- RSM itself never runs this (SqlGuard would reject every statement here).
USE master;
EXEC sp_configure 'show advanced options', 1;
RECONFIGURE;
EXEC sp_configure 'max degree of parallelism', 0;          -- SQL-CFG-005 (expected 4)
EXEC sp_configure 'optimize for ad hoc workloads', 0;      -- SQL-CFG-007
EXEC sp_configure 'backup compression default', 0;        -- SQL-CFG-004
RECONFIGURE;
GO
CREATE DATABASE Rahkaran;
GO
ALTER DATABASE Rahkaran SET RECOVERY SIMPLE;                -- DB-RM-001
ALTER DATABASE Rahkaran SET AUTO_SHRINK ON;                 -- DB-OPT-003
ALTER DATABASE Rahkaran SET PAGE_VERIFY NONE;               -- DB-OPT-001
ALTER DATABASE Rahkaran MODIFY FILE (NAME = Rahkaran, FILEGROWTH = 10%);  -- DB-FILE-001
GO
USE Rahkaran;
CREATE TABLE dbo.Sample (Id int IDENTITY PRIMARY KEY, Payload nvarchar(200) NOT NULL);
INSERT INTO dbo.Sample (Payload) SELECT TOP (2000) N'row' FROM sys.all_objects;
-- Rows the live blocking/deadlock demos lock (LiveDemoTests plays two app sessions; every demo transaction rolls back).
CREATE TABLE dbo.RsmDemoAccounts (Id int PRIMARY KEY, Balance int NOT NULL);
INSERT INTO dbo.RsmDemoAccounts (Id, Balance) VALUES (1, 100), (2, 200);
GO
USE msdb;
EXEC dbo.sp_add_job @job_name = N'RSM-IT nightly shrink';
EXEC dbo.sp_add_jobstep @job_name = N'RSM-IT nightly shrink', @step_name = N'shrink', @subsystem = N'TSQL',
     @database_name = N'Rahkaran', @command = N'DBCC SHRINKFILE (Rahkaran, 1);';      -- SQL-AGT-003
EXEC dbo.sp_add_jobserver @job_name = N'RSM-IT nightly shrink';
GO
