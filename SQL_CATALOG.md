# SQL_CATALOG — every T-SQL statement RSM plans to run

Status: **approved**; §0 implemented in P0. Each row becomes one embedded `.sql` file under
`src/RSM.Collectors.*/Queries/` whose header repeats key, purpose, min version, permission, cost, timeout
and database (see `ARCHITECTURE.md` §6.3). The `SqlCatalogIsReadOnly` test parses every file with
SqlGuard; this document is cross-checked against the embedded files by a test (no undocumented query can
ship, no documented query can be missing).

Every batch is sent as:

```sql
SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED; SET LOCK_TIMEOUT 2000; SET DEADLOCK_PRIORITY LOW; SET NOCOUNT ON;
<catalog file body>
```

and every session/request view filters `s.program_name <> N'RSM-ReadOnlyMonitor'` (except
`cap.self_overhead`, which deliberately looks *only* at RSM's own sessions).

**Legend**

- **Ver** — minimum `ProductMajorVersion[.build]`: 13 = 2016, 13 SP2 = 13.0.5026, 14 = 2017, 15 = 2019,
  16 = 2022. Queries below the minimum return `Unavailable("requires …")`. Target range is 2016–2022.
- **Perm** — VSS = `VIEW SERVER STATE` (on 2022 the finer `VIEW SERVER PERFORMANCE STATE` /
  `VIEW SERVER SECURITY STATE` are implied by VSS); VAD = `VIEW ANY DEFINITION`; VDS = `VIEW DATABASE
  STATE` in the target DB (implied by VSS + `CONNECT ANY DATABASE`); msdb = SELECT on the listed msdb
  objects (via `SQLAgentReaderRole` + `db_datareader` per `PERMISSIONS.md`, or narrower grants — see Q7).
- **Cost** — `C` cheap (≤ 50 ms typical), `M` medium (≤ 1 s), `H` heavy (**on-demand only**, warning +
  estimate in UI).
- **DB** — `master` (default), `each` (one connection per accessible user DB, `Initial Catalog` set by the
  executor), `rk` (detected/selected Rahkaran DB(s) only), `tempdb`, `msdb`, `rs` (ReportServer DB).
- **Ph** — delivery phase (see `PLAN.md`).

---

## 0. Capability probes & self-monitoring (P0)

| Key | Purpose | Objects | Ver | Perm | Cost | DB | Ph |
|---|---|---|---|---|---|---|---|
| `cap.version` | Version/edition/level for gating, server name, clustering/HADR flags | `SERVERPROPERTY(ProductVersion, ProductLevel, ProductUpdateLevel, Edition, EngineEdition, ServerName, IsClustered, IsHadrEnabled)`, `@@VERSION` | 13 | public | C | master | 0 |
| `cap.server_perms` | Effective server permissions + sysadmin / securityadmin flags (banner) | `HAS_PERMS_BY_NAME(NULL,NULL,…)` for VSS / VAD / CONNECT ANY DATABASE / CONTROL SERVER, `IS_SRVROLEMEMBER`, `SUSER_SNAME()`; **v16 variant** adds VIEW SERVER PERFORMANCE / SECURITY STATE | 13 / 16 | public | C | master | 0 |
| `cap.db_access` | Which DBs are reachable | `sys.databases`, `HAS_DBACCESS(name)` | 13 | public | C | master | 0 |
| `cap.object_perms` | SELECT on msdb history tables, EXECUTE on xp_readerrorlog, ERRORLOG path (fixed object list) | `HAS_PERMS_BY_NAME(N'msdb.dbo.…','OBJECT','SELECT')`, `SERVERPROPERTY('ErrorLogFileName')` | 13 | public | C | master | 0 |
| `cap.agent_role` | SQLAgentReaderRole membership, job-table access | `IS_ROLEMEMBER(N'SQLAgentReaderRole')`, `HAS_PERMS_BY_NAME(N'dbo.sysjobs',…)` | 13 | user in msdb | C | msdb | 0 |
| `cap.ssrs_access` | ReportServer DB exposes `ExecutionLog3` and the login can read it | `OBJECT_ID(N'dbo.ExecutionLog3')`, `HAS_PERMS_BY_NAME` | 13 | user in ReportServer | C | rs | 0 |
| `cap.self_overhead` | RSM's own CPU/reads/memory on the server — the only query that looks *at* RSM's sessions (header `own_sessions: true`, pinned by a test) | `sys.dm_exec_sessions` **where** `program_name = N'RSM-ReadOnlyMonitor'`, `sys.dm_os_sys_info` | 13 | VSS | C | master | 2 |
| `cap.rtt` | Round-trip latency probe | `SELECT 1` | 13 | public | C | master | 2 |

## 1. Inventory (§7.1) — P1

| Key | Purpose | Objects | Ver | Perm | Cost | DB |
|---|---|---|---|---|---|---|
| `sql.inv.server_properties` | Collation, clustering, HADR, IFI-related props, ErrorLogFileName, InstanceDefaultDataPath/LogPath | `SERVERPROPERTY(...)` | 13 | public | C | master |
| `sql.inv.sys_info` | CPU/sockets/cores/HT ratio/NUMA, memory, start time, VM type, affinity | `sys.dm_os_sys_info` | 13 | VSS | C | master |
| `sql.inv.host_info` | Host OS distribution/release/SKU | `sys.dm_os_host_info` | 14 | public | C | master |
| `sql.inv.services` | Service accounts, startup type, status, IFI enabled | `sys.dm_server_services` | 13 SP1 | VSS | C | master |
| `sql.inv.nodes` | NUMA nodes, online schedulers per node | `sys.dm_os_nodes` | 13 | VSS | C | master |
| `sql.inv.schedulers` | Online/offline (licensing-limited) schedulers, runnable queue | `sys.dm_os_schedulers` | 13 | VSS | C | master |
| `sql.inv.memory_dumps` | Stack/memory dumps produced by SQL Server | `sys.dm_server_memory_dumps` | 13 | VSS | C | master |
| `sql.inv.sys_memory` | OS memory state as seen by SQL | `sys.dm_os_sys_memory` | 13 | VSS | C | master |

Build → CU mapping is done client-side against `known-builds.json`.
Trace flags: `DBCC TRACESTATUS` is forbidden; flags are reported from ERRORLOG startup lines when readable.

## 2. Configuration compliance (§7.2) — P1

| Key | Purpose | Objects | Ver | Perm | Cost | DB |
|---|---|---|---|---|---|---|
| `sql.cfg.configurations` | value vs value_in_use, pending restart, all options | `sys.configurations` | 13 | public | C | master |
| `sql.cfg.auth_mode` | Windows-only vs mixed | `SERVERPROPERTY('IsIntegratedSecurityOnly')` | 13 | public | C | master |

## 3. Performance counters (§7.3) — P2

| Key | Purpose | Objects | Ver | Perm | Cost | DB |
|---|---|---|---|---|---|---|
| `sql.perf.counters` | Fixed counter list (below), raw values + cntr_type; deltas client-side | `sys.dm_os_performance_counters` | 13 | VSS | C | master |

Counters: Batch Requests/sec, SQL Compilations/sec, SQL Re-Compilations/sec, Page life expectancy
(Buffer Manager + every Buffer Node), Lazy writes/sec, Free list stalls/sec, Checkpoint pages/sec,
Memory Grants Pending, Memory Grants Outstanding, Target Server Memory (KB), Total Server Memory (KB),
User Connections, Processes blocked, Lock Waits/sec, Lock Timeouts/sec, Number of Deadlocks/sec
(`_Total`), Full Scans/sec, Page Splits/sec, Log Flushes/sec, Log Flush Wait Time, Transactions/sec
(per DB), Log Growths, Log Shrinks, Errors/sec.

Delta math by `cntr_type`: `272696576` PERF_COUNTER_BULK_COUNT → (v2−v1)/Δt; `65792` raw → as is;
`537003264` PERF_LARGE_RAW_FRACTION ÷ matching `1073939712` base × 100; `1073874176`
PERF_AVERAGE_BULK → Δvalue/Δbase. Counter resets (restart) detected via `sqlserver_start_time`.

## 4. CPU attribution (§7.4) — P2

| Key | Purpose | Objects | Ver | Perm | Cost | DB |
|---|---|---|---|---|---|---|
| `sql.cpu.ring_buffer` | SQL CPU % vs other processes, idle %, ~256 min history | `sys.dm_os_ring_buffers` (`RING_BUFFER_SCHEDULER_MONITOR`), `sys.dm_os_sys_info.ms_ticks` | 13 | VSS | M | master |

## 5. Wait statistics (§7.5) — P2

| Key | Purpose | Objects | Ver | Perm | Cost | DB |
|---|---|---|---|---|---|---|
| `sql.waits.wait_stats` | Cumulative waits (benign excluded); deltas + signal % client-side | `sys.dm_os_wait_stats` | 13 | VSS | C | master |
| `sql.waits.waiting_tasks` | Live waiting tasks with resource description | `sys.dm_os_waiting_tasks` + `sys.dm_exec_sessions` | 13 | VSS | C | master |
| `sql.waits.session_waits` | Per-session waits for active sessions | `sys.dm_exec_session_wait_stats` | 13 | VSS | C | master |

Benign wait exclusion list (embedded as a `VALUES` table inside the file, also used client-side):
`BROKER_*` (EVENTHANDLER, RECEIVE_WAITFOR, TASK_STOP, TO_FLUSH, TRANSMITTER), `CHECKPOINT_QUEUE`,
`CHKPT`, `CLR_AUTO_EVENT`, `CLR_MANUAL_EVENT`, `CLR_SEMAPHORE`, `DBMIRROR_DBM_EVENT`,
`DBMIRROR_EVENTS_QUEUE`, `DBMIRROR_WORKER_QUEUE`, `DBMIRRORING_CMD`, `DIRTY_PAGE_POLL`,
`DISPATCHER_QUEUE_SEMAPHORE`, `EXECSYNC`, `FSAGENT`, `FT_IFTS_SCHEDULER_IDLE_WAIT`, `FT_IFTSHC_MUTEX`,
`HADR_CLUSAPI_CALL`, `HADR_FILESTREAM_IOMGR_IOCOMPLETION`, `HADR_LOGCAPTURE_WAIT`,
`HADR_NOTIFICATION_DEQUEUE`, `HADR_TIMER_TASK`, `HADR_WORK_QUEUE`, `KSOURCE_WAKEUP`, `LAZYWRITER_SLEEP`,
`LOGMGR_QUEUE`, `MEMORY_ALLOCATION_EXT`, `ONDEMAND_TASK_QUEUE`, `PARALLEL_REDO_*` (idle variants),
`PREEMPTIVE_XE_GETTARGETSTATE`, `PREEMPTIVE_OS_FLUSHFILEBUFFERS` (idle), `PWAIT_ALL_COMPONENTS_INITIALIZED`,
`PWAIT_DIRECTLOGCONSUMER_GETNEXT`, `PWAIT_EXTENSIBILITY_CLEANUP_TASK`, `QDS_ASYNC_QUEUE`,
`QDS_CLEANUP_STALE_QUERIES_TASK_MAIN_LOOP_SLEEP`, `QDS_PERSIST_TASK_MAIN_LOOP_SLEEP`,
`QDS_SHUTDOWN_QUEUE`, `REDO_THREAD_PENDING_WORK`, `REQUEST_FOR_DEADLOCK_SEARCH`, `RESOURCE_QUEUE`,
`SERVER_IDLE_CHECK`, `SLEEP_*` (BPOOL_FLUSH, DBSTARTUP, DCOMSTARTUP, MASTERDBREADY, MASTERMDREADY,
MASTERUPGRADED, MSDBSTARTUP, SYSTEMTASK, TASK, TEMPDBSTARTUP), `SNI_HTTP_ACCEPT`, `SOS_WORK_DISPATCHER`,
`SP_SERVER_DIAGNOSTICS_SLEEP`, `SQLTRACE_BUFFER_FLUSH`, `SQLTRACE_INCREMENTAL_FLUSH_SLEEP`,
`SQLTRACE_WAIT_ENTRIES`, `VDI_CLIENT_OTHER`, `WAIT_FOR_RESULTS`, `WAITFOR`, `WAITFOR_TASKSHUTDOWN`,
`WAIT_XTP_RECOVERY`, `WAIT_XTP_HOST_WAIT`, `WAIT_XTP_OFFLINE_CKPT_NEW_LOG`, `WAIT_XTP_CKPT_CLOSE`,
`XE_DISPATCHER_JOIN`, `XE_DISPATCHER_WAIT`, `XE_TIMER_EVENT`, `XE_LIVE_TARGET_TVF`.
(General guidance — based on widely used community exclusion lists; editable in settings.)

Categories (client-side, `kb/waits.json`): CPU, Parallelism, Worker/THREADPOOL, IO-data, IO-log,
Locking, Latch, TempDB (PAGELATCH on db 2), Memory, Network/client (ASYNC_NETWORK_IO), Backup, Other.

## 6. Active requests (§7.6) — P2

| Key | Purpose | Objects | Ver | Perm | Cost | DB |
|---|---|---|---|---|---|---|
| `sql.req.active` | Running requests with statement text (offset extraction), waits, blocking, CPU, reads, writes, grant, DOP, % complete, open tran count | `sys.dm_exec_requests`, `sys.dm_exec_sessions`, `sys.dm_exec_connections`, `sys.dm_exec_sql_text`, `sys.dm_exec_query_memory_grants` | 13 | VSS | C | master |
| `sql.req.input_buffer` | Last batch of a session (on click / head blocker) | `sys.dm_exec_input_buffer(@session_id, NULL)` | 13 | VSS | C | master |
| `sql.req.plan` | *(served by `sql.qs.plan_text` with the request's plan handle and offsets)* | — | — | — | — | — |
| `sql.req.sessions_summary` | Sessions by host/program/login/DB (topology, ARR balance) | `sys.dm_exec_sessions`, `sys.dm_exec_connections` | 13 | VSS | C | master |
| `sql.req.idle_open_tran` | Sleeping sessions holding open transactions | `sys.dm_exec_sessions` (`open_transaction_count > 0`), `sys.dm_tran_session_transactions` | 13 | VSS | C | master |

## 7. Blocking (§7.7) — P2

| Key | Purpose | Objects | Ver | Perm | Cost | DB |
|---|---|---|---|---|---|---|
| `sql.block.chains` | Head-blocker tree via recursive CTE, durations, wait resources, head blocker's last statement + open tran age | `sys.dm_exec_requests`, `sys.dm_exec_sessions`, `sys.dm_exec_sql_text`, `sys.dm_tran_session_transactions`, `sys.dm_tran_active_transactions` | 13 | VSS | C | master |
| `sql.block.locks_summary` | Lock counts by session/resource type/mode (on demand) | `sys.dm_tran_locks` (grouped, `TOP`) | 13 | VSS | M | master |

## 8. Deadlocks (§7.8) — P2

| Key | Purpose | Objects | Ver | Perm | Cost | DB |
|---|---|---|---|---|---|---|
| `sql.dl.xel_path` | Locate `system_health` files | `sys.dm_os_server_diagnostics_log_configurations`, `sys.dm_xe_session_targets` / `sys.dm_xe_sessions` | 13 | VSS | C | master |
| `sql.dl.from_file` | `xml_deadlock_report` events (filtered by time client-side) | `sys.fn_xe_file_target_read_file(<log dir> + 'system_health*.xel', …)` — the directory comes from `sys.dm_os_server_diagnostics_log_configurations` **inside the query**; the client never passes a path | 13 | VSS | M | master |
| `sql.dl.from_file` (`.v14` variant) | Same with `timestamp_utc >= @Since` pushed down | as above | 14 | VSS | M | master |
| `sql.dl.from_ring` | Fallback: ring_buffer target | `sys.dm_xe_session_targets` + `sys.dm_xe_sessions` (`system_health`, `ring_buffer`) | 13 | VSS | M | master |

Graph parsing (victim, processes, statements, objects/indexes, lock modes) is client-side in
`RSM.Parsers.DeadlockXml`. No XE session is ever created or altered.

## 9. Transactions & version store (§7.9) — P2

| Key | Purpose | Objects | Ver | Perm | Cost | DB |
|---|---|---|---|---|---|---|
| `sql.tran.active` | Long/open transactions: begin time, type, state, log bytes used/reserved per DB | `sys.dm_tran_active_transactions`, `sys.dm_tran_session_transactions`, `sys.dm_tran_database_transactions`, `sys.dm_exec_sessions` | 13 | VSS | C | master |
| `sql.tran.version_store` | Version store size per DB | `sys.dm_tran_version_store_space_usage` | 13 SP2 | VSS | C | tempdb |

## 10. Top queries, plans, Query Store (§7.10) — P2

| Key | Purpose | Objects | Ver | Perm | Cost | DB |
|---|---|---|---|---|---|---|
| `sql.qs.top_queries` | Top-N by `@order` (cpu/duration/reads/writes/execs/grant) totals + averages | `sys.dm_exec_query_stats`, `sys.dm_exec_sql_text` | 13 | VSS | M | master |
| `sql.qs.top_queries.v13sp2` | + spills, grant columns | as above (`total_spills`, `max_grant_kb`, `max_used_grant_kb`) | 13 SP2 | VSS | M | master |
| `sql.qs.top_procs` | Top-N procedures | `sys.dm_exec_procedure_stats` | 13 | VSS | M | master |
| `sql.qs.plan_cache_health` | Plan cache size by objtype, single-use plan count/bytes | `sys.dm_exec_cached_plans` (grouped) | 13 | VSS | M | master |
| `sql.qs.plan_text` | Showplan XML (text form, no nesting limit) of one statement — top-N analysis or on click; also serves the running request's plan | `sys.dm_exec_text_query_plan(@PlanHandle, @StatementStart, @StatementEnd)` | 13 | VSS | M | master |
| `sql.qds.options` | Query Store state per DB | `sys.database_query_store_options` | 13 | VDS | C | each |
| `sql.qds.top` | Top consumers last @hours | `sys.query_store_query`, `query_store_query_text`, `query_store_plan`, `query_store_runtime_stats`, `query_store_runtime_stats_interval` | 13 | VDS | M | rk |
| `sql.qds.regressed` | Recent window vs baseline window (duration/CPU ratio, plan change) | same | 13 | VDS | M | rk |
| `sql.qds.forced_plans` | Forced plans + failures | `sys.query_store_plan` (`is_forced_plan`, `force_failure_count`, `last_force_failure_reason_desc`) | 13 | VDS | C | rk |

Plan analysis (missing-index hints, `CONVERT_IMPLICIT`, spills, key/RID lookups, large scans, excessive
grants, parallelism, `NoJoinPredicate`) is done **client-side** in `RSM.Parsers.Showplan` on plans
fetched for the top-N queries — never by scanning the plan cache XML in T-SQL (that heavy variant exists
only as on-demand `sql.qs.plan_xml` over the top-N handles).

## 11. Missing / unused indexes (§7.11) — P2

| Key | Purpose | Objects | Ver | Perm | Cost | DB |
|---|---|---|---|---|---|---|
| `sql.idx.missing` | Missing index suggestions + improvement measure | `sys.dm_db_missing_index_details`, `_groups`, `_group_stats` | 13 | VSS | C | master |
| `sql.idx.usage` | Seeks/scans/lookups/updates per index since restart | `sys.dm_db_index_usage_stats`, `sys.indexes`, `sys.objects` | 13 | VDS | M | rk |

Displayed only, with the fixed caution text (rule `DB-IDX-000`). `sql.idx.usage` runs in the Rahkaran database(s).

## 12. Memory (§7.12) — P1/P2

| Key | Purpose | Objects | Ver | Perm | Cost | DB | Ph |
|---|---|---|---|---|---|---|---|
| `sql.mem.process` | Physical in use, locked pages, memory-low notifications, utilization % | `sys.dm_os_process_memory` | 13 | VSS | C | master | 1 |
| `sql.mem.clerks` | Top 20 memory clerks | `sys.dm_os_memory_clerks` | 13 | VSS | C | master | 1 |
| `sql.mem.grants` | Current grants, waiting queries | `sys.dm_exec_query_memory_grants` | 13 | VSS | C | master | 2 |
| `sql.mem.semaphores` | Resource semaphore state | `sys.dm_exec_query_resource_semaphores` | 13 | VSS | C | master | 2 |
| `sql.mem.buffer_by_db` | Buffer pool by DB (**heavy**, on demand) | `sys.dm_os_buffer_descriptors` (grouped) | 13 | VSS | H | master | 2 |

## 13. TempDB (§7.13) — P1

| Key | Purpose | Objects | Ver | Perm | Cost | DB |
|---|---|---|---|---|---|---|
| `sql.tempdb.files` | File count, sizes, growth settings | `tempdb.sys.database_files` | 13 | public | C | tempdb |
| `sql.tempdb.space` | User/internal/version-store/free pages | `sys.dm_db_file_space_usage` | 13 | VSS | C | tempdb |
| `sql.tempdb.session_usage` | Top sessions by tempdb allocation | `sys.dm_db_session_space_usage`, `sys.dm_db_task_space_usage` | 13 | VSS | C | tempdb |
| `sql.tempdb.contention` | PAGELATCH waits on tempdb allocation pages (2:x:1/2/3, PFS/GAM/SGAM) | `sys.dm_os_waiting_tasks` | 13 | VSS | C | master |

## 14. I/O (§7.14) — P1

| Key | Purpose | Objects | Ver | Perm | Cost | DB |
|---|---|---|---|---|---|---|
| `sql.io.file_stats` | Cumulative reads/writes/stall per file → latency deltas | `sys.dm_io_virtual_file_stats(NULL,NULL)`, `sys.master_files` | 13 | VSS | C | master |
| `sql.io.pending` | Pending I/O requests | `sys.dm_io_pending_io_requests` | 13 | VSS | C | master |
| `sql.io.volumes` | Volume free space, mount point, FS type for every DB file | `sys.master_files` CROSS APPLY `sys.dm_os_volume_stats` | 13 | VSS | C | master |

## 15. SQL Server error log (§7.15) — P1

| Key | Purpose | Objects | Ver | Perm | Cost | DB |
|---|---|---|---|---|---|---|
| `sql.log.errorlog` | Read log #@lognum (0 = current) of type 1 (SQL) — `DECLARE @t TABLE(...); INSERT INTO @t EXEC master.sys.xp_readerrorlog @lognum, 1; SELECT … FROM @t WHERE LogDate >= @since` | `master.sys.xp_readerrorlog` (only whitelisted EXEC) | 13 | EXECUTE on xp_readerrorlog **or** securityadmin (varies by version — *verify*) | M | master |
| `sql.log.agentlog` | Same for Agent log (type 2) | same | 13 | same | M | master |
| `sql.log.path` | ERRORLOG path for file fallback | `SERVERPROPERTY('ErrorLogFileName')` | 13 | public | C | master |

Number of archives: RSM reads logs 0..N (setting, default 6) until "file not found"; `xp_enumerrorlogs`
is **not** whitelisted. Classification (823/824/825, 833, 17883/17884, 701/802, 9002, 1205, 18456 by
state/IP, 3041, stack dumps, cachestore flush) is client-side in `RSM.Parsers.ErrorLog`.

## 16. SQL Agent (§7.16) — P1

| Key | Purpose | Objects | Ver | Perm | Cost | DB |
|---|---|---|---|---|---|---|
| `sql.agent.service` | Agent service state/startup | `sys.dm_server_services` | 13 SP1 | VSS | C | master |
| `sql.agent.jobs` | Jobs, enabled, owner, category, schedules, next run | `msdb.dbo.sysjobs`, `sysjobschedules`, `sysschedules`, `syscategories` | 13 | msdb | C | msdb |
| `sql.agent.job_steps` | Step subsystem + command (for shrink/backup/UNC/CDC detection; masked in exports) | `msdb.dbo.sysjobsteps` | 13 | msdb | C | msdb |
| `sql.agent.history` | Outcomes & durations last @days (default 30) | `msdb.dbo.sysjobhistory` | 13 | msdb | M | msdb |
| `sql.agent.running` | Currently running jobs | `msdb.dbo.sysjobactivity` (latest `session_id` from `msdb.dbo.syssessions`) | 13 | msdb | C | msdb |
| `sql.agent.maint_plans` | Maintenance plans & subplans (mixing with ستاد فنی tool jobs) | `msdb.dbo.sysmaintplan_plans`, `sysmaintplan_subplans` | 13 | msdb | C | msdb |
| `sql.agent.cdc_jobs` | CDC capture/cleanup job registration | `msdb.dbo.cdc_jobs` (may not exist ⇒ `OBJECT_ID` check inside the query) | 13 | msdb | C | msdb |

Detection of "ستاد فنی tool jobs" vs. manual maintenance plans uses **configurable name/command patterns**
(unverified until the team supplies them — Q6).

## 17. Security posture (§7.17) — P1

| Key | Purpose | Objects | Ver | Perm | Cost | DB |
|---|---|---|---|---|---|---|
| `sql.sec.logins` | Logins: type, disabled, policy/expiration checked, default DB, `sa` (sid 0x01) renamed/disabled | `sys.server_principals`, `sys.sql_logins` | 13 | VAD | C | master |
| `sql.sec.sysadmins` | Members of sysadmin | `sys.server_role_members`, `sys.server_principals` | 13 | VAD | C | master |
| `sql.sec.sa_blank_pwd` | `sa` with empty password (only if `password_hash` visible — needs CONTROL SERVER; else "unknown") | `sys.sql_logins` + `PWDCOMPARE(N'', password_hash)` | 13 | CONTROL SERVER (optional) | C | master |
| `sql.sec.encryption` | Connection encryption distribution, auth scheme, protocol | `sys.dm_exec_connections` | 13 | VSS | C | master |
| `sql.sec.linked_servers` | Linked servers present (names, providers — no calls) | `sys.servers` (`is_linked = 1`) | 13 | VAD | C | master |
| `sql.sec.orphaned_users` | DB users without a matching login (SQL users) | `sys.database_principals` ⟕ `sys.server_principals` | 13 | VAD | C | each |
| `sql.sec.guest` | `guest` has CONNECT in user DBs | `sys.database_permissions` | 13 | VAD | C | each |

SQL Browser state comes from the Windows services collector.

## 18. High availability (§7.18) — P2 (informational)

| Key | Purpose | Objects | Ver | Perm | Cost | DB |
|---|---|---|---|---|---|---|
| `sql.ha.ag` | AG replicas, sync state, send/redo queues | `sys.dm_hadr_availability_replica_states`, `sys.dm_hadr_database_replica_states`, `sys.availability_groups`, `sys.availability_replicas` | 13 | VSS | C | master |
| `sql.ha.mirroring` | Mirroring role/state | `sys.database_mirroring` | 13 | VAD | C | master |
| `sql.ha.log_shipping` | Log shipping monitor | `msdb.dbo.log_shipping_monitor_primary`, `_secondary` | 13 | msdb | C | msdb |

---

## 19. Database module (§8) — P1

| Key | Purpose | Objects | Ver | Perm | Cost | DB |
|---|---|---|---|---|---|---|
| `db.list` | State, recovery model, page_verify, auto_close/shrink, auto stats, compat level, RCSI/snapshot, `log_reuse_wait_desc`, `is_cdc_enabled`, `is_query_store_on`, owner, create date | `sys.databases` | 13 | public | C | master |
| `db.files` | Size/used/free, growth type & value, max size, physical path | `sys.database_files` + `FILEPROPERTY(name,'SpaceUsed')` | 13 | public in DB | C | each |
| `db.log_space` | Log size/used % | `sys.dm_db_log_space_usage` | 13 | VDS | C | each |
| `db.log_stats` | Log since last log backup, last log backup time, active/total VLFs, recovery VLFs | `sys.dm_db_log_stats(DB_ID())` | 13 SP2 | VDS | C | each |
| `db.vlf` | VLF count/active/sizes (fallback: "unknown" below 13 SP2) | `sys.dm_db_log_info(DB_ID())` (aggregated) | 13 SP2 | VDS | M | each |
| `db.backups.last` | Last Full/Diff/Log per DB, copy_only, checksum, compressed size | `msdb.dbo.backupset` | 13 | msdb | C | msdb |
| `db.backups.history` | 90-day history: duration, throughput, size trend, destination (local vs UNC) | `msdb.dbo.backupset`, `msdb.dbo.backupmediafamily` | 13 | msdb | M | msdb |
| `db.backups.log_chain` | LSN chain gaps (`LAG` over `first_lsn`/`last_lsn`) since last full | `msdb.dbo.backupset` | 13 | msdb | M | msdb |
| `db.integrity.last_checkdb` | Last known good CHECKDB | `DATABASEPROPERTYEX(name,'LastGoodCheckDbTime')` | 13 SP2 | public | C | master |
| `db.integrity.suspect_pages` | Suspect pages | `msdb.dbo.suspect_pages` | 13 | msdb | C | msdb |
| `db.size.top_tables` | Top 50 tables by reserved/used/rows | `sys.dm_db_partition_stats`, `sys.objects`, `sys.schemas` | 13 | VDS | M | rk |
| `db.stats.properties` | Rows, modification_counter, last_updated → stale stats (top-N) | `sys.stats` CROSS APPLY `sys.dm_db_stats_properties` | 13 | VDS | M | rk |
| `db.idx.physical` | Fragmentation for one table (`@object_id`), LIMITED; called for top-N largest tables (**heavy**) | `sys.dm_db_index_physical_stats(DB_ID(), @object_id, NULL, NULL, 'LIMITED')` | 13 | VDS | H | rk |
| `db.cdc.tables` | Number of CDC-tracked tables | `sys.tables` (`is_tracked_by_cdc`) | 13 | VAD | C | each (CDC DBs) |
| `db.cdc.log_scan` | CDC log scan sessions: latency, errors, last run | `sys.dm_cdc_log_scan_sessions`, `sys.dm_cdc_errors` | 13 | VDS | C | each (CDC DBs) |
| `db.detect.activity` | Activity signal for Rahkaran DB auto-detection (sessions per DB + size) | `sys.dm_exec_sessions` (`database_id`), `sys.master_files` | 13 | VSS | C | master |

**Rahkaran DB detection** uses only size, session count and program names — no Rahkaran table names are
assumed. The user can override the selection per site.

## 20. SSRS (§11) — P4

| Key | Purpose | Objects | Ver | Perm | Cost | DB |
|---|---|---|---|---|---|---|
| `ssrs.detect` | ReportServer DBs the login can open (TempDB excluded) | `sys.databases` (`name LIKE N'ReportServer%'`), `HAS_DBACCESS` | 13 | public | C | master |
| `ssrs.exec.recent` | Executions of the last `@Hours` (≤ `@MaxRows`, newest first): item, user, request type, format, source, status, age, hour, total/data/processing/rendering ms, rows, bytes — aggregated in RSM (`ExecutionLogAggregator`: per-report p50/p95, data vs render, by hour/user/status) | `dbo.ExecutionLog3` (no `Parameters` column) | 13 | SELECT on view | M | rs |
| `ssrs.subscriptions` | Subscriptions with report path, delivery type, last status text, minutes since last run | `dbo.Subscriptions`, `dbo.Catalog` | 13 | SELECT on both (*verify per SSRS version*) | C | rs |

*As built (P4):* the planned `ssrs.exec.summary` / `slowest` / `hourly` became one row query plus aggregation in RSM,
so percentiles use the same nearest-rank definition as the IIS logs and the aggregation is unit-tested.

## 21. Topology (§6) — P4

| Key | Purpose | Objects | Ver | Perm | Cost | DB |
|---|---|---|---|---|---|---|
| `topo.sql_clients` | Connections per client host/program/login/DB (ARR/app-server balance) | `sys.dm_exec_sessions`, `sys.dm_exec_connections` (`client_net_address`) | 13 | VSS | C | master |

## 22. Query packs (§8.7) — P4

User-supplied `QueryPacks/<pack>/*.sql` + `manifest.json` (title fa/en, target DB = `rk`|named,
columns, thresholds, min version). Loaded at startup, **each file passes SqlGuard with the stricter
query-pack profile** (tables/views only, no UDF calls, no cross-DB names) or is rejected with the reason
shown in Settings → Query packs. Packs ship **empty**; Rahkaran-specific table names are added only by the
team once confirmed.

---

## 23. Things deliberately NOT queried

| Not used | Why | Alternative |
|---|---|---|
| `DBCC SQLPERF`, `DBCC LOGINFO`, `DBCC TRACESTATUS`, `DBCC OPENTRAN`, `DBCC INPUTBUFFER` | DBCC banned | DMVs above |
| `sp_WhoIsActive`, `sp_Blitz*`, any procedure | only `xp_readerrorlog` may be EXECuted | equivalent DMV queries |
| `xp_instance_regread`, `xp_enumerrorlogs`, `xp_fixeddrives`, `xp_msver` | not whitelisted | SERVERPROPERTY, `dm_os_volume_stats`, Windows collectors |
| `sp_MSforeachdb` / dynamic SQL | banned | executor opens one connection per DB |
| Creating XE sessions / traces / Query Store changes | write | read `system_health` only |
| `sys.dm_db_index_physical_stats` SAMPLED/DETAILED | too heavy | LIMITED, top-N, on demand |

## Phase 2 implementation notes

- Every query that reads a session/request view (`dm_exec_sessions`, `dm_exec_requests`, `dm_exec_connections`,
  `dm_os_waiting_tasks`, `dm_tran_session_transactions`, `dm_tran_locks`) excludes `program_name = 'RSM-ReadOnlyMonitor'`;
  `SqlCatalogTests.Session_views_exclude_rsm_own_sessions` enforces it (it found `sql.tempdb.contention` from P1 missing
  the filter — fixed). The only exception is `cap.self_overhead`, declared with `own_sessions: true`.
- `sql.qs.top_queries` also drops RSM's own batches (their text starts with the fixed hardening prefix).
- No `OPTION (MAXRECURSION …)`: SqlGuard allows only RECOMPILE/MAXDOP hints, so the blocking tree caps its depth with
  `chain_level < 32` instead.
- `sql.waits.session_waits` is not needed by the P2 pages and was not written.
- Ages (`TranAgeS`, `IdleTranAgeS`, `AgeS`) are computed **on the server** with `DATEDIFF(SECOND, …, GETDATE())`, so a
  client clock or time zone different from the server's cannot distort "open transaction > 5 min".
- `sql.dl.from_file`: `sys.dm_os_server_diagnostics_log_configurations.path` may or may not end with a separator
  (Windows `\`, Linux `/`); the query appends one with a `CASE` expression before `system_health*.xel`. The collector
  falls back to `sql.dl.from_ring` when the files cannot be read **or** return no deadlock (event files are flushed
  lazily — up to ~2 min).
- `sql.qs.plan_text` is called for the top 5 statements by CPU only (by plan handle and offsets, every 5 min); it is not a
  plan-cache scan, which stays an on-demand heavy operation.
- Live collectors and their queries: `sql.perf` (`sql.perf.counters`, `sql.waits.wait_stats`, `sql.io.file_stats`,
  `sql.waits.waiting_tasks`, `sql.io.pending`), `sql.cpu` (`sql.cpu.ring_buffer`), `sql.activity` (`sql.req.active`,
  `sql.block.chains`, `sql.req.idle_open_tran`, `sql.tran.active`, `sql.req.sessions_summary`), `sql.deadlocks`
  (`sql.dl.from_file` → `sql.dl.from_ring`), `sql.querystats` (`sql.qs.top_queries`, `sql.qs.plan_text`,
  `sql.qs.top_procs`, `sql.qs.plan_cache_health`, `sql.idx.missing`), `sql.querystore` (`sql.qds.options`,
  `sql.qds.regressed`, `sql.qds.forced_plans`, `sql.idx.usage` — Rahkaran DBs only), `sql.ha` (`sql.ha.ag`),
  `rsm.overhead.sql` (`cap.rtt`, `cap.self_overhead`). `sql.block.locks_summary` and `sql.req.input_buffer` are in the
  catalog for on-demand use (P3 UI) and not scheduled.

## Phase 1 implementation notes

Every embedded catalog file has a row above (checked by `RSM.Tests`). Planned keys that Phase 1 did **not** need as
separate queries:

| Planned key | Phase 1 implementation |
|---|---|
| `sql.log.agentlog` | `sql.log.errorlog` takes `@LogType` (1 = SQL Server, 2 = Agent) instead of a second file. |
| `sql.log.path` | `SERVERPROPERTY('ErrorLogFileName')` is read by the capability probe (`cap.*`) and `sql.inv.server_properties`. |
| `sql.cfg.auth_mode` | Folded into `sql.inv.server_properties` (`IsIntegratedSecurityOnly`). |
| `sql.agent.service` | Folded into `sql.inv.services` (`sys.dm_server_services`). |
| `sql.inv.nodes` | NUMA node count comes from `sql.inv.sys_info`. |
| `db.vlf` | VLF counts come from `db.log_stats` (`sys.dm_db_log_stats`, 13 SP2+); `dm_db_log_info` is not used. |
| `db.backups.log_chain` | Computed client-side from `db.backups_history` rows (`BackupAnalysis.IsLogChainBroken`): no window functions on msdb. |
| `sql.mem.grants`, `sql.mem.semaphores`, `sql.mem.buffer_by_db` | Live memory views — Phase 2. |

`sql.tempdb.session_usage` runs in `tempdb` (header `database: tempdb`); since `dcda00e` the executor always opens the
connection in the database a header declares, and rejects a conflicting argument.
