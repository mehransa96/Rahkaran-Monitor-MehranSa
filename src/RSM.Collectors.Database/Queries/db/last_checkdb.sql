-- key:         db.integrity.last_checkdb
-- purpose:     Last known good CHECKDB per online database
-- min_version: 13.0.5026
-- permission:  public
-- cost:        cheap
-- timeout_s:   15
-- database:    master
SELECT d.name AS Name, CAST(DATABASEPROPERTYEX(d.name, 'LastGoodCheckDbTime') AS datetime) AS LastGoodCheckDb
FROM sys.databases AS d
WHERE d.state = 0;
