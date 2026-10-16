-- LAB ONLY. Session A = head blocker: updates a row inside a transaction and keeps it open for 5 minutes.
-- Run blocking-session-b.sql in a second window while this one waits. RSM never runs this.
USE RsmLab;
BEGIN TRANSACTION;
UPDATE dbo.LabBlocking SET Payload = N'held by session A' WHERE Id = 1;
WAITFOR DELAY '00:05:00';
ROLLBACK TRANSACTION;
