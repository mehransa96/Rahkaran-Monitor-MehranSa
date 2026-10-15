-- key:         sql.ha.ag
-- purpose:     Availability groups: replica health, database sync state, send/redo queues (informational; empty when no AG)
-- min_version: 13
-- permission:  VIEW SERVER STATE
-- cost:        cheap
-- timeout_s:   10
-- database:    master
SELECT ag.name AS AgName, ar.replica_server_name AS ReplicaName, ars.role_desc AS RoleDesc, ars.synchronization_health_desc AS Health,
       ars.connected_state_desc AS ConnectedState, DB_NAME(drs.database_id) AS DatabaseName, drs.synchronization_state_desc AS SyncState,
       drs.log_send_queue_size AS LogSendQueueKb, drs.redo_queue_size AS RedoQueueKb
FROM sys.dm_hadr_availability_replica_states AS ars
JOIN sys.availability_replicas AS ar ON ar.replica_id = ars.replica_id
JOIN sys.availability_groups AS ag ON ag.group_id = ars.group_id
LEFT JOIN sys.dm_hadr_database_replica_states AS drs ON drs.replica_id = ars.replica_id;
