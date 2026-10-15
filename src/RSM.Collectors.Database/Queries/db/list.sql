-- key:         db.list
-- purpose:     Every database: state, recovery model, options, log reuse wait, CDC/QS flags, sizes, access
-- min_version: 13
-- permission:  public
-- cost:        cheap
-- timeout_s:   15
-- database:    master
SELECT d.database_id AS DatabaseId, d.name AS Name, d.state_desc AS StateDesc, d.recovery_model_desc AS RecoveryModel,
       d.page_verify_option_desc AS PageVerify, CAST(d.is_auto_close_on AS int) AS AutoClose, CAST(d.is_auto_shrink_on AS int) AS AutoShrink,
       CAST(d.is_auto_create_stats_on AS int) AS AutoCreateStats, CAST(d.is_auto_update_stats_on AS int) AS AutoUpdateStats,
       CAST(d.compatibility_level AS int) AS CompatibilityLevel, CAST(d.is_read_committed_snapshot_on AS int) AS Rcsi,
       d.snapshot_isolation_state_desc AS SnapshotIsolation, d.log_reuse_wait_desc AS LogReuseWait, CAST(d.is_cdc_enabled AS int) AS IsCdcEnabled,
       CAST(d.is_query_store_on AS int) AS IsQueryStoreOn, CAST(d.is_read_only AS int) AS IsReadOnly, d.user_access_desc AS UserAccess,
       d.create_date AS CreateDate, SUSER_SNAME(d.owner_sid) AS Owner, CAST(HAS_DBACCESS(d.name) AS int) AS HasAccess,
       (SELECT SUM(CAST(mf.size AS bigint)) * 8 / 1024 FROM sys.master_files AS mf WHERE mf.database_id = d.database_id AND mf.type = 0) AS DataSizeMb,
       (SELECT SUM(CAST(mf.size AS bigint)) * 8 / 1024 FROM sys.master_files AS mf WHERE mf.database_id = d.database_id AND mf.type = 1) AS LogSizeMb
FROM sys.databases AS d
ORDER BY d.database_id;
