-- key:         ssrs.exec.recent
-- purpose:     Report executions of the last @Hours (newest first, at most @MaxRows): timings, status, source; aggregated in RSM (p95, data vs render)
-- min_version: 13
-- permission:  SELECT on dbo.ExecutionLog3
-- cost:        medium
-- timeout_s:   30
-- database:    reportserver
SELECT TOP (@MaxRows)
       e.ItemPath,
       e.UserName,
       e.RequestType,
       e.Format,
       e.Source,
       e.Status,
       DATEDIFF(SECOND, e.TimeStart, GETDATE())       AS AgeS,
       DATEPART(HOUR, e.TimeStart)                    AS HourOfDay,
       DATEDIFF(MILLISECOND, e.TimeStart, e.TimeEnd)  AS DurationMs,
       e.TimeDataRetrieval                            AS DataMs,
       e.TimeProcessing                               AS ProcessingMs,
       e.TimeRendering                                AS RenderingMs,
       e.[RowCount]                                   AS RowsReturned,
       e.ByteCount                                    AS Bytes
FROM dbo.ExecutionLog3 AS e
WHERE e.TimeStart >= DATEADD(HOUR, -@Hours, GETDATE())
ORDER BY e.TimeStart DESC;
