-- key:         db.cdc.tables
-- purpose:     Number of CDC-tracked tables (CDC-enabled databases only)
-- min_version: 13
-- permission:  VIEW ANY DEFINITION
-- cost:        cheap
-- timeout_s:   10
-- database:    each
SELECT COUNT(*) AS TrackedTables
FROM sys.tables
WHERE is_tracked_by_cdc = 1;
