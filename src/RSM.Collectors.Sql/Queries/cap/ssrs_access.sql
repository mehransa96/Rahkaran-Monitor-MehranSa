-- key:         cap.ssrs_access
-- purpose:     Whether a ReportServer database exposes ExecutionLog3 and the RSM login may read it
-- min_version: 13
-- permission:  CONNECT to the ReportServer DB + SELECT on dbo.ExecutionLog3
-- cost:        cheap
-- timeout_s:   10
-- database:    reportserver
SELECT
    CASE WHEN OBJECT_ID(N'dbo.ExecutionLog3') IS NULL THEN 0 ELSE 1 END AS HasExecutionLog3,
    HAS_PERMS_BY_NAME(N'dbo.ExecutionLog3', N'OBJECT', N'SELECT')       AS CanReadExecutionLog3;
