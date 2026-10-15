-- key:         sql.inv.services
-- purpose:     SQL Server and Agent services: startup type, status, account, IFI
-- min_version: 13.0.4001
-- permission:  VIEW SERVER STATE
-- cost:        cheap
-- timeout_s:   10
-- database:    master
SELECT servicename AS ServiceName, startup_type_desc AS StartupType, status_desc AS Status, process_id AS ProcessId,
       last_startup_time AS LastStartupTime, service_account AS ServiceAccount, is_clustered AS IsClustered,
       instant_file_initialization_enabled AS InstantFileInitializationEnabled
FROM sys.dm_server_services;
