-- key:         sql.waits.wait_stats
-- purpose:     Cumulative wait statistics without the common idle/benign waits (interval deltas + signal % client-side)
-- min_version: 13
-- permission:  VIEW SERVER STATE
-- cost:        cheap
-- timeout_s:   10
-- database:    master
-- The benign list is general guidance (widely used community exclusion lists); the client applies it again.
SELECT ws.wait_type AS WaitType, ws.waiting_tasks_count AS WaitingTasks, ws.wait_time_ms AS WaitMs, ws.signal_wait_time_ms AS SignalMs
FROM sys.dm_os_wait_stats AS ws
WHERE ws.wait_time_ms > 0
  AND ws.wait_type NOT LIKE N'SLEEP[_]%'
  AND ws.wait_type NOT LIKE N'BROKER[_]%'
  AND ws.wait_type NOT LIKE N'XE[_]%'
  AND ws.wait_type NOT LIKE N'QDS[_]%'
  AND ws.wait_type NOT LIKE N'HADR[_]%'
  AND ws.wait_type NOT LIKE N'PARALLEL[_]REDO[_]%'
  AND ws.wait_type NOT LIKE N'PWAIT[_]%'
  AND ws.wait_type NOT IN (
      N'CHECKPOINT_QUEUE', N'CHKPT', N'CLR_AUTO_EVENT', N'CLR_MANUAL_EVENT', N'CLR_SEMAPHORE', N'DBMIRROR_DBM_EVENT',
      N'DBMIRROR_EVENTS_QUEUE', N'DBMIRROR_WORKER_QUEUE', N'DBMIRRORING_CMD', N'DIRTY_PAGE_POLL', N'DISPATCHER_QUEUE_SEMAPHORE',
      N'EXECSYNC', N'FSAGENT', N'FT_IFTS_SCHEDULER_IDLE_WAIT', N'FT_IFTSHC_MUTEX', N'KSOURCE_WAKEUP', N'LAZYWRITER_SLEEP',
      N'LOGMGR_QUEUE', N'MEMORY_ALLOCATION_EXT', N'ONDEMAND_TASK_QUEUE', N'PREEMPTIVE_XE_GETTARGETSTATE',
      N'PREEMPTIVE_OS_FLUSHFILEBUFFERS', N'REDO_THREAD_PENDING_WORK', N'REQUEST_FOR_DEADLOCK_SEARCH', N'RESOURCE_QUEUE',
      N'SERVER_IDLE_CHECK', N'SNI_HTTP_ACCEPT', N'SOS_WORK_DISPATCHER', N'SP_SERVER_DIAGNOSTICS_SLEEP', N'SQLTRACE_BUFFER_FLUSH',
      N'SQLTRACE_INCREMENTAL_FLUSH_SLEEP', N'SQLTRACE_WAIT_ENTRIES', N'VDI_CLIENT_OTHER', N'WAIT_FOR_RESULTS', N'WAITFOR',
      N'WAITFOR_TASKSHUTDOWN', N'WAIT_XTP_RECOVERY', N'WAIT_XTP_HOST_WAIT', N'WAIT_XTP_OFFLINE_CKPT_NEW_LOG', N'WAIT_XTP_CKPT_CLOSE');
