-- key:         sql.cfg.configurations
-- purpose:     All sp_configure options: value vs value_in_use
-- min_version: 13
-- permission:  public
-- cost:        cheap
-- timeout_s:   10
-- database:    master
SELECT name AS Name, CAST(value AS bigint) AS Value, CAST(value_in_use AS bigint) AS ValueInUse,
       CAST(is_dynamic AS int) AS IsDynamic, CAST(is_advanced AS int) AS IsAdvanced
FROM sys.configurations;
