-- key:         sql.qds.regressed
-- purpose:     Query Store: queries whose average duration in the last @RecentHours is > 2x the previous @BaselineDays, with more than one plan
-- min_version: 13
-- permission:  VIEW DATABASE STATE
-- cost:        medium
-- timeout_s:   30
-- database:    rk
DECLARE @recent datetimeoffset = DATEADD(hour, -@RecentHours, SYSDATETIMEOFFSET());
DECLARE @start datetimeoffset = DATEADD(day, -@BaselineDays, SYSDATETIMEOFFSET());
WITH rs AS (
    SELECT p.query_id, p.plan_id, i.start_time, r.avg_duration, r.count_executions
    FROM sys.query_store_runtime_stats AS r
    JOIN sys.query_store_runtime_stats_interval AS i ON i.runtime_stats_interval_id = r.runtime_stats_interval_id
    JOIN sys.query_store_plan AS p ON p.plan_id = r.plan_id
    WHERE i.start_time >= @start
),
agg AS (
    SELECT rs.query_id,
           SUM(CASE WHEN rs.start_time >= @recent THEN rs.avg_duration * rs.count_executions END)
             / NULLIF(SUM(CASE WHEN rs.start_time >= @recent THEN rs.count_executions END), 0) AS RecentAvgUs,
           SUM(CASE WHEN rs.start_time < @recent THEN rs.avg_duration * rs.count_executions END)
             / NULLIF(SUM(CASE WHEN rs.start_time < @recent THEN rs.count_executions END), 0) AS BaselineAvgUs,
           SUM(CASE WHEN rs.start_time >= @recent THEN rs.count_executions ELSE 0 END) AS RecentExecutions,
           COUNT(DISTINCT rs.plan_id) AS Plans
    FROM rs
    GROUP BY rs.query_id
)
SELECT TOP (20) a.query_id AS QueryId, a.RecentAvgUs, a.BaselineAvgUs, a.RecentExecutions, a.Plans,
       LEFT(qt.query_sql_text, 4000) AS QueryText
FROM agg AS a
JOIN sys.query_store_query AS q ON q.query_id = a.query_id
JOIN sys.query_store_query_text AS qt ON qt.query_text_id = q.query_text_id
WHERE a.BaselineAvgUs > 0 AND a.RecentAvgUs > 2 * a.BaselineAvgUs AND a.Plans > 1
ORDER BY a.RecentAvgUs / a.BaselineAvgUs DESC;
