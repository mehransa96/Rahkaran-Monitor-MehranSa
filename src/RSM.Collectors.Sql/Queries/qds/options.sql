-- key:         sql.qds.options
-- purpose:     Query Store state of one database
-- min_version: 13
-- permission:  VIEW DATABASE STATE
-- cost:        cheap
-- timeout_s:   10
-- database:    each
SELECT o.actual_state_desc AS ActualState, o.desired_state_desc AS DesiredState, CAST(o.readonly_reason AS int) AS ReadonlyReason,
       o.current_storage_size_mb AS CurrentStorageMb, o.max_storage_size_mb AS MaxStorageMb
FROM sys.database_query_store_options AS o;
