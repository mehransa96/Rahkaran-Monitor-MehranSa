-- LAB ONLY. Session B = blocked: waits for the row locked by session A. RSM never runs this.
USE RsmLab;
SELECT Payload FROM dbo.LabBlocking WHERE Id = 1;
