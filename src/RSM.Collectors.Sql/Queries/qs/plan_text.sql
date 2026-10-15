-- key:         sql.qs.plan_text
-- purpose:     Cached showplan XML of one statement (top-N analysis or on click); text form avoids the XML nesting limit
-- min_version: 13
-- permission:  VIEW SERVER STATE
-- cost:        medium
-- timeout_s:   20
-- database:    master
SELECT qp.query_plan AS PlanXml
FROM sys.dm_exec_text_query_plan(@PlanHandle, @StatementStart, @StatementEnd) AS qp;
