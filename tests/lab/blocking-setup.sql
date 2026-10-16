-- LAB ONLY (run as a lab administrator, never as rsm_monitor, never on production). RSM never runs this.
-- Creates the RsmLab database with one table used by blocking-session-a.sql / blocking-session-b.sql.
IF DB_ID(N'RsmLab') IS NULL CREATE DATABASE RsmLab;
GO
USE RsmLab;
GO
IF OBJECT_ID(N'dbo.LabBlocking') IS NULL
BEGIN
    CREATE TABLE dbo.LabBlocking (Id int NOT NULL PRIMARY KEY, Payload nvarchar(100) NOT NULL);
    INSERT dbo.LabBlocking (Id, Payload) VALUES (1, N'row 1'), (2, N'row 2');
END
GO
