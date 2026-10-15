-- key:         sql.sec.linked_servers
-- purpose:     Linked servers present (never called)
-- min_version: 13
-- permission:  VIEW ANY DEFINITION
-- cost:        cheap
-- timeout_s:   10
-- database:    master
SELECT s.name AS Name, s.product AS Product, s.provider AS Provider, s.data_source AS DataSource
FROM sys.servers AS s
WHERE s.is_linked = 1;
