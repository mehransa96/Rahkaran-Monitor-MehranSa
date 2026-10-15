-- key:         sql.qds.forced_plans
-- purpose:     Query Store forced plans and their failures (read only)
-- min_version: 13
-- permission:  VIEW DATABASE STATE
-- cost:        cheap
-- timeout_s:   10
-- database:    rk
SELECT TOP (50) p.query_id AS QueryId, p.plan_id AS PlanId, p.force_failure_count AS ForceFailureCount,
       p.last_force_failure_reason_desc AS LastFailureReason, p.last_execution_time AS LastExecution
FROM sys.query_store_plan AS p
WHERE p.is_forced_plan = 1
ORDER BY p.force_failure_count DESC;
