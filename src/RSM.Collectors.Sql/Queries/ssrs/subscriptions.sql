-- key:         ssrs.subscriptions
-- purpose:     Subscriptions with their report, delivery type, last status text and minutes since the last run
-- min_version: 13
-- permission:  SELECT on dbo.Subscriptions and dbo.Catalog
-- cost:        cheap
-- timeout_s:   15
-- database:    reportserver
SELECT TOP (500)
       c.[Path]                                        AS ReportPath,
       s.[Description]                                 AS Description,
       s.DeliveryExtension,
       s.LastStatus,
       DATEDIFF(MINUTE, s.LastRunTime, GETDATE())      AS LastRunAgoMin,
       s.InactiveFlags
FROM dbo.Subscriptions AS s
LEFT JOIN dbo.[Catalog] AS c ON c.ItemID = s.Report_OID
ORDER BY s.LastRunTime DESC;
