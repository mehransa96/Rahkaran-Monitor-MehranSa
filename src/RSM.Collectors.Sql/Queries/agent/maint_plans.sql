-- key:         sql.agent.maint_plans
-- purpose:     Maintenance plans and their subplan jobs
-- min_version: 13
-- permission:  SELECT on msdb.dbo.sysmaintplan_plans, sysmaintplan_subplans
-- cost:        cheap
-- timeout_s:   10
-- database:    msdb
SELECT p.id AS PlanId, p.name AS PlanName, sp.subplan_id AS SubplanId, sp.subplan_name AS SubplanName, sp.job_id AS JobId
FROM dbo.sysmaintplan_plans AS p
LEFT JOIN dbo.sysmaintplan_subplans AS sp ON sp.plan_id = p.id;
