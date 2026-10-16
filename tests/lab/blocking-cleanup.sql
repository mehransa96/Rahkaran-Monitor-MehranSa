-- LAB ONLY. Drops the RsmLab database after the blocking scenario. RSM never runs this.
USE master;
IF DB_ID(N'RsmLab') IS NOT NULL
BEGIN
    ALTER DATABASE RsmLab SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
    DROP DATABASE RsmLab;
END
