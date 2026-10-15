-- key:         db.integrity.suspect_pages
-- purpose:     Pages recorded as suspect (823/824/checksum)
-- min_version: 13
-- permission:  SELECT on msdb.dbo.suspect_pages
-- cost:        cheap
-- timeout_s:   10
-- database:    msdb
SELECT sp.database_id AS DatabaseId, sp.file_id AS FileId, CAST(sp.page_id AS bigint) AS PageId, sp.event_type AS EventType,
       sp.error_count AS ErrorCount, sp.last_update_date AS LastUpdateDate
FROM dbo.suspect_pages AS sp;
