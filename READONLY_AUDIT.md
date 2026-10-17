# READONLY_AUDIT — RSM against PROMPT v2 §2 (= PROMPT v1 §2)

Phase A deliverable 1 of PROMPT v2 (`docs/SPEC.md` is PROMPT v1; PROMPT v2 §2 is the same rule set, word for word).
Audited commit: `47b1767` (default branch after PR #3/#4). Method: source inventory of every outbound operation
(grep over `src/`, read of each transport), cross-checked against the enforcement tests. No code was changed for this
audit.

## 1. Verdict

**No violation of §2.1–§2.6 was found.** Every outbound operation is a read, goes through the documented transport,
and is covered by at least one compile-time or test-time guard (§4).

Five points are compliant but are *interpretations* of §2 rather than items §2 names explicitly. They are listed so the
owner can confirm them; none needs a code change unless the owner decides otherwise.

| # | Item | Where | Why it is compliant | Owner action |
|---|---|---|---|---|
| R1 | `SET NOCOUNT ON` is part of the hardening prefix (§2.2.5 lists three SETs) | `src/RSM.Safety/Sql/QueryCatalog.cs:224`; SqlGuard allows only `NOCOUNT ON` (`src/RSM.Safety/Sql/SqlGuard.cs:189`) | Session option that suppresses row-count messages; changes no server state. Already in `CLAUDE.md`. | Confirm (no change proposed) |
| R2 | .NET runtime counters open an **EventPipe session** in `w3wp`/`dotnet` (providers `System.Runtime`, `Microsoft.AspNetCore.Hosting`, counters only) | `src/RSM.Safety/DotNet/RuntimeCounterSource.cs:38`, `:60-63`; off by default (`src/RSM.Core/Profiles/SiteProfile.cs:59`, decision Q8) | Read-only telemetry; §2.1 does not ban it, the PROMPT v2 §3.1 inventory lists "EventPipe usage" as an expected operation. Every public `DiagnosticsClient` member is allowed or banned by `RSM.ArchTests`. | Confirm it stays opt-in |
| R3 | IIS live requests use `WorkerProcess.GetRequests` (IIS runtime state, Request Monitor) | `src/RSM.Collectors.Iis/LocalIisSources.cs:36` | Returns the in-flight request list; no `WorkerProcess` mutation. `ConfigurationMethodInstance.Execute` stays banned for our code. | Confirm |
| R4 | `PWDCOMPARE(N'', password_hash)` to detect a blank `sa` password (Tuning V.3: "sa must have a password") | `src/RSM.Collectors.Sql/Queries/sec/sa_blank_pwd.sql:9` | Built-in read-only function inside a `SELECT`; passes SqlGuard. Only a boolean is returned, never a hash. | Confirm |
| R5 | DNS lookup of the Redis host to match `CLIENT LIST` addresses (topology) | `src/RSM.Collectors.Redis/RedisCollector.cs:108` | Name resolution only; no packets to any other host. | Confirm |

## 2. Operations inventory

### 2.1 SQL Server — catalog queries (84 files, all executed by key through `SafeSqlExecutor`)

Rule: §2.1 (read-only T-SQL), §2.2.2 (catalog), §2.2.3 (SqlGuard), §2.2.5 (hardening). Every file has the header of
§2.2.2 and is checked by `SqlCatalogIsReadOnly` (`tests/RSM.Safety.Tests/SqlCatalogTests.cs:28`); every file that
reads a session/request view excludes `program_name = 'RSM-ReadOnlyMonitor'` (same file, `:47`). The only `EXEC` is
`master.sys.xp_readerrorlog` (`src/RSM.Collectors.Sql/Queries/log/errorlog.sql:9`, §2.3). The only *heavy* query,
`db.idx.physical` (LIMITED, top-N tables), runs only when heavy operations are requested
(`src/RSM.Collectors.Database/DatabaseCollector.cs:184-190`, §2.6).

| Key | File | Cost | Permission (header) |
|---|---|---|---|
| `db.detect.activity` | `src/RSM.Collectors.Database/Queries/db/activity.sql` | cheap | VIEW SERVER STATE |
| `db.backups.history` | `src/RSM.Collectors.Database/Queries/db/backups_history.sql` | medium | SELECT on msdb.dbo.backupset, backupmediafamily |
| `db.backups.last` | `src/RSM.Collectors.Database/Queries/db/backups_last.sql` | cheap | SELECT on msdb.dbo.backupset |
| `db.cdc.log_scan` | `src/RSM.Collectors.Database/Queries/db/cdc_log_scan.sql` | cheap | VIEW DATABASE STATE |
| `db.cdc.tables` | `src/RSM.Collectors.Database/Queries/db/cdc_tables.sql` | cheap | VIEW ANY DEFINITION |
| `db.files` | `src/RSM.Collectors.Database/Queries/db/files.sql` | cheap | CONNECT to the database |
| `db.idx.physical` | `src/RSM.Collectors.Database/Queries/db/index_physical.sql` | heavy | VIEW DATABASE STATE |
| `db.integrity.last_checkdb` | `src/RSM.Collectors.Database/Queries/db/last_checkdb.sql` | cheap | public |
| `db.list` | `src/RSM.Collectors.Database/Queries/db/list.sql` | cheap | public |
| `db.log_space` | `src/RSM.Collectors.Database/Queries/db/log_space.sql` | cheap | VIEW DATABASE STATE |
| `db.log_stats` | `src/RSM.Collectors.Database/Queries/db/log_stats.sql` | cheap | VIEW DATABASE STATE |
| `db.stats.properties` | `src/RSM.Collectors.Database/Queries/db/stats_properties.sql` | medium | VIEW DATABASE STATE |
| `db.integrity.suspect_pages` | `src/RSM.Collectors.Database/Queries/db/suspect_pages.sql` | cheap | SELECT on msdb.dbo.suspect_pages |
| `db.size.top_tables` | `src/RSM.Collectors.Database/Queries/db/top_tables.sql` | medium | VIEW DATABASE STATE |
| `sql.sec.guest` | `src/RSM.Collectors.Database/Queries/sec/guest.sql` | cheap | VIEW ANY DEFINITION |
| `sql.sec.orphaned_users` | `src/RSM.Collectors.Database/Queries/sec/orphaned_users.sql` | cheap | VIEW ANY DEFINITION |
| `sql.agent.cdc_jobs` | `src/RSM.Collectors.Sql/Queries/agent/cdc_jobs.sql` | cheap | SELECT on msdb.dbo.cdc_jobs |
| `sql.agent.history` | `src/RSM.Collectors.Sql/Queries/agent/history.sql` | medium | SELECT on msdb.dbo.sysjobhistory |
| `sql.agent.job_steps` | `src/RSM.Collectors.Sql/Queries/agent/job_steps.sql` | cheap | SELECT on msdb.dbo.sysjobsteps |
| `sql.agent.jobs` | `src/RSM.Collectors.Sql/Queries/agent/jobs.sql` | cheap | SELECT on msdb sysjobs/sysjobschedules/sysschedules/syscategories |
| `sql.agent.maint_plans` | `src/RSM.Collectors.Sql/Queries/agent/maint_plans.sql` | cheap | SELECT on msdb.dbo.sysmaintplan_plans, sysmaintplan_subplans |
| `sql.agent.running` | `src/RSM.Collectors.Sql/Queries/agent/running.sql` | cheap | SELECT on msdb.dbo.sysjobactivity, syssessions |
| `sql.block.chains` | `src/RSM.Collectors.Sql/Queries/block/chains.sql` | cheap | VIEW SERVER STATE |
| `sql.block.locks_summary` | `src/RSM.Collectors.Sql/Queries/block/locks_summary.sql` | medium | VIEW SERVER STATE |
| `cap.agent_role` | `src/RSM.Collectors.Sql/Queries/cap/agent_role.sql` | cheap | CONNECT to msdb (user mapped in msdb) |
| `cap.db_access` | `src/RSM.Collectors.Sql/Queries/cap/db_access.sql` | cheap | public |
| `cap.object_perms` | `src/RSM.Collectors.Sql/Queries/cap/object_perms.sql` | cheap | public |
| `cap.rtt` | `src/RSM.Collectors.Sql/Queries/cap/rtt.sql` | cheap | public |
| `cap.self_overhead` | `src/RSM.Collectors.Sql/Queries/cap/self_overhead.sql` | cheap | VIEW SERVER STATE |
| `cap.server_perms` | `src/RSM.Collectors.Sql/Queries/cap/server_perms.sql` | cheap | public |
| `cap.server_perms` | `src/RSM.Collectors.Sql/Queries/cap/server_perms.v16.sql` | cheap | public |
| `cap.ssrs_access` | `src/RSM.Collectors.Sql/Queries/cap/ssrs_access.sql` | cheap | CONNECT to the ReportServer DB + SELECT on dbo.ExecutionLog3 |
| `cap.version` | `src/RSM.Collectors.Sql/Queries/cap/version.sql` | cheap | public |
| `sql.cfg.configurations` | `src/RSM.Collectors.Sql/Queries/cfg/configurations.sql` | cheap | public |
| `sql.cpu.ring_buffer` | `src/RSM.Collectors.Sql/Queries/cpu/ring_buffer.sql` | medium | VIEW SERVER STATE |
| `sql.dl.from_file` | `src/RSM.Collectors.Sql/Queries/dl/from_file.sql` | medium | VIEW SERVER STATE |
| `sql.dl.from_file` | `src/RSM.Collectors.Sql/Queries/dl/from_file.v14.sql` | medium | VIEW SERVER STATE |
| `sql.dl.from_ring` | `src/RSM.Collectors.Sql/Queries/dl/from_ring.sql` | medium | VIEW SERVER STATE |
| `sql.ha.ag` | `src/RSM.Collectors.Sql/Queries/ha/ag.sql` | cheap | VIEW SERVER STATE |
| `sql.idx.missing` | `src/RSM.Collectors.Sql/Queries/idx/missing.sql` | cheap | VIEW SERVER STATE |
| `sql.idx.usage` | `src/RSM.Collectors.Sql/Queries/idx/usage.sql` | medium | VIEW DATABASE STATE |
| `sql.inv.host_info` | `src/RSM.Collectors.Sql/Queries/inv/host_info.sql` | cheap | public |
| `sql.inv.memory_dumps` | `src/RSM.Collectors.Sql/Queries/inv/memory_dumps.sql` | cheap | VIEW SERVER STATE |
| `sql.inv.schedulers` | `src/RSM.Collectors.Sql/Queries/inv/schedulers.sql` | cheap | VIEW SERVER STATE |
| `sql.inv.server_properties` | `src/RSM.Collectors.Sql/Queries/inv/server_properties.sql` | cheap | public |
| `sql.inv.services` | `src/RSM.Collectors.Sql/Queries/inv/services.sql` | cheap | VIEW SERVER STATE |
| `sql.inv.sys_info` | `src/RSM.Collectors.Sql/Queries/inv/sys_info.sql` | cheap | VIEW SERVER STATE |
| `sql.inv.sys_info` | `src/RSM.Collectors.Sql/Queries/inv/sys_info.v13sp2.sql` | cheap | VIEW SERVER STATE |
| `sql.inv.sys_memory` | `src/RSM.Collectors.Sql/Queries/inv/sys_memory.sql` | cheap | VIEW SERVER STATE |
| `sql.io.file_stats` | `src/RSM.Collectors.Sql/Queries/io/file_stats.sql` | cheap | VIEW SERVER STATE |
| `sql.io.pending` | `src/RSM.Collectors.Sql/Queries/io/pending.sql` | cheap | VIEW SERVER STATE |
| `sql.io.volumes` | `src/RSM.Collectors.Sql/Queries/io/volumes.sql` | cheap | VIEW SERVER STATE |
| `sql.log.errorlog` | `src/RSM.Collectors.Sql/Queries/log/errorlog.sql` | medium | EXECUTE on xp_readerrorlog or securityadmin (verify per version) |
| `sql.mem.clerks` | `src/RSM.Collectors.Sql/Queries/mem/clerks.sql` | cheap | VIEW SERVER STATE |
| `sql.mem.process` | `src/RSM.Collectors.Sql/Queries/mem/process.sql` | cheap | VIEW SERVER STATE |
| `sql.perf.counters` | `src/RSM.Collectors.Sql/Queries/perf/counters.sql` | cheap | VIEW SERVER STATE |
| `sql.qds.forced_plans` | `src/RSM.Collectors.Sql/Queries/qds/forced_plans.sql` | cheap | VIEW DATABASE STATE |
| `sql.qds.options` | `src/RSM.Collectors.Sql/Queries/qds/options.sql` | cheap | VIEW DATABASE STATE |
| `sql.qds.regressed` | `src/RSM.Collectors.Sql/Queries/qds/regressed.sql` | medium | VIEW DATABASE STATE |
| `sql.qs.plan_cache_health` | `src/RSM.Collectors.Sql/Queries/qs/plan_cache_health.sql` | medium | VIEW SERVER STATE |
| `sql.qs.plan_text` | `src/RSM.Collectors.Sql/Queries/qs/plan_text.sql` | medium | VIEW SERVER STATE |
| `sql.qs.top_procs` | `src/RSM.Collectors.Sql/Queries/qs/top_procs.sql` | medium | VIEW SERVER STATE |
| `sql.qs.top_queries` | `src/RSM.Collectors.Sql/Queries/qs/top_queries.sql` | medium | VIEW SERVER STATE |
| `sql.qs.top_queries` | `src/RSM.Collectors.Sql/Queries/qs/top_queries.v13sp2.sql` | medium | VIEW SERVER STATE |
| `sql.req.active` | `src/RSM.Collectors.Sql/Queries/req/active.sql` | cheap | VIEW SERVER STATE |
| `sql.req.idle_open_tran` | `src/RSM.Collectors.Sql/Queries/req/idle_open_tran.sql` | cheap | VIEW SERVER STATE |
| `sql.req.input_buffer` | `src/RSM.Collectors.Sql/Queries/req/input_buffer.sql` | cheap | VIEW SERVER STATE |
| `sql.req.sessions_summary` | `src/RSM.Collectors.Sql/Queries/req/sessions_summary.sql` | cheap | VIEW SERVER STATE |
| `sql.sec.encryption` | `src/RSM.Collectors.Sql/Queries/sec/encryption.sql` | cheap | VIEW SERVER STATE |
| `sql.sec.linked_servers` | `src/RSM.Collectors.Sql/Queries/sec/linked_servers.sql` | cheap | VIEW ANY DEFINITION |
| `sql.sec.logins` | `src/RSM.Collectors.Sql/Queries/sec/logins.sql` | cheap | VIEW ANY DEFINITION |
| `sql.sec.sa_blank_pwd` | `src/RSM.Collectors.Sql/Queries/sec/sa_blank_pwd.sql` | cheap | CONTROL SERVER (optional) |
| `sql.sec.sysadmins` | `src/RSM.Collectors.Sql/Queries/sec/sysadmins.sql` | cheap | VIEW ANY DEFINITION |
| `ssrs.detect` | `src/RSM.Collectors.Sql/Queries/ssrs/detect.sql` | cheap | public |
| `ssrs.exec.recent` | `src/RSM.Collectors.Sql/Queries/ssrs/exec_recent.sql` | medium | SELECT on dbo.ExecutionLog3 |
| `ssrs.subscriptions` | `src/RSM.Collectors.Sql/Queries/ssrs/subscriptions.sql` | cheap | SELECT on dbo.Subscriptions and dbo.Catalog |
| `sql.tempdb.contention` | `src/RSM.Collectors.Sql/Queries/tempdb/contention.sql` | cheap | VIEW SERVER STATE |
| `sql.tempdb.files` | `src/RSM.Collectors.Sql/Queries/tempdb/files.sql` | cheap | public |
| `sql.tempdb.session_usage` | `src/RSM.Collectors.Sql/Queries/tempdb/session_usage.sql` | cheap | VIEW SERVER STATE |
| `sql.tempdb.space` | `src/RSM.Collectors.Sql/Queries/tempdb/space.sql` | cheap | VIEW SERVER STATE |
| `topo.sql_clients` | `src/RSM.Collectors.Sql/Queries/topo/sql_clients.sql` | cheap | VIEW SERVER STATE |
| `sql.tran.active` | `src/RSM.Collectors.Sql/Queries/tran/active.sql` | cheap | VIEW SERVER STATE |
| `sql.waits.wait_stats` | `src/RSM.Collectors.Sql/Queries/waits/wait_stats.sql` | cheap | VIEW SERVER STATE |
| `sql.waits.waiting_tasks` | `src/RSM.Collectors.Sql/Queries/waits/waiting_tasks.sql` | cheap | VIEW SERVER STATE |

**User query packs** (`docs/query-packs/`, loaded from the site profile): parsed and guarded with the stricter
query-pack profile of SqlGuard before use (`src/RSM.Safety/Sql/QueryPacks.cs`, `src/RSM.Collectors.Database/QueryPackCollector.cs:28`);
a pack containing `UPDATE` is refused (`tests/RSM.Safety.Tests/QueryPackTests.cs`).

**Connection** (§2.2.5, §2.5): `Application Name=RSM-ReadOnlyMonitor`, `ApplicationIntent=ReadOnly`, `Connect Timeout=5`,
per-command timeout, `Encrypt` Mandatory/Optional/Strict and `TrustServerCertificate` per endpoint
(`src/RSM.Safety/Sql/SafeSqlExecutor.cs:62-69`); hardening prefix `src/RSM.Safety/Sql/QueryCatalog.cs:224`.

### 2.2 Windows performance counters (§2.1 allows reads; local PDH, remote CIM `Win32_PerfRawData_*`/`Win32_PerfFormattedData_*`)

All 36 metrics are declared in one table, `src/RSM.Collectors.Windows/WindowsMetricCatalog.cs:64-99`:
Processor Information (% Processor Time, % Processor Performance, Processor Frequency), System (Processor Queue
Length, Context Switches/sec), Memory (Available MBytes, % Committed Bytes In Use, Pages/sec), LogicalDisk (Avg. Disk
sec/Read, sec/Write, Current Disk Queue Length, Disk Reads/sec, Disk Writes/sec, % Free Space), Network Interface
(Bytes Total/sec, Current Bandwidth, Output Queue Length, Packets Received Errors, Packets Outbound Errors, Packets
Received Discarded), TCPv4 (Connections Established), Web Service (Current Connections, Total Method Requests/sec,
Get/Post Requests/sec, Bytes Total/sec), W3SVC_W3WP (Active Requests, Requests / Sec, Active Threads Count), HTTP
Service Request Queues (CurrentQueueSize, RejectedRequests, ArrivalRate), APP_POOL_WAS (Current Application Pool
State, Total/Recent Worker Process Failures, Current Application Pool Uptime). Local reads open every counter with
`new PerformanceCounter(category, counter, instance, readOnly: true)` (`src/RSM.Collectors.Windows/PdhCounterSource.cs:72`);
`PerformanceCounter.RawValue` (getter and setter) is banned.

### 2.3 WMI / CIM — queries only (§2.1, §2.2.6)

Single entry point: `CimSession.QueryInstances` in `src/RSM.Collectors.Windows/CimSessionQuery.cs:84`. No
`InvokeMethod`, `CreateInstance`, `ModifyInstance`, `DeleteInstance` anywhere (banned, canary-checked). Classes read:

| Namespace | Class | Used in |
|---|---|---|
| `root\cimv2` | `Win32_OperatingSystem` | `WindowsInventoryCollector.cs:45`, `WindowsPerfCollector.cs:38`, `WindowsAccess.cs:99`, `IisAccess.cs:27` (all under `src/RSM.Collectors.Windows/` or `src/RSM.Collectors.Iis/`) |
| `root\cimv2` | `Win32_ComputerSystem`, `Win32_Processor`, `Win32_PageFileSetting`, `Win32_Volume`, `Win32_TimeZone`, `Win32_NetworkAdapterConfiguration` | `src/RSM.Collectors.Windows/WindowsInventoryCollector.cs:47-57` |
| `root\cimv2\power` | `Win32_PowerPlan` | `src/RSM.Collectors.Windows/WindowsInventoryCollector.cs:54` |
| `root\cimv2` | `Win32_Service` | `src/RSM.Collectors.Windows/WindowsServicesCollector.cs:50,61,150`, `WindowsAccess.cs:101-103` |
| `root\cimv2` | `Win32_Process` (read: name, PID, command line for pool mapping) | `src/RSM.Collectors.Windows/WindowsServicesCollector.cs:62`, `src/RSM.Collectors.Iis/IisRuntimeCollector.cs:28`, `src/RSM.Collectors.Rahkaran/DotNetRuntimeCollector.cs:74` |
| `root\cimv2` | `Win32_PerfFormattedData_PerfProc_Process` (top processes) | `src/RSM.Collectors.Windows/WindowsProcessCollector.cs:36` |
| `root\cimv2` | `Win32_PerfRawData_*` / `Win32_PerfFormattedData_*` (remote counters of §2.2) | `src/RSM.Collectors.Windows/WindowsMetricCatalog.cs:19-60`, `src/RSM.Collectors.Iis/IisConfigCollector.cs:32` |
| `root\StandardCimv2` | `MSFT_NetTCPConnection`, `MSFT_NetTCPSetting` | `src/RSM.Collectors.Windows/WindowsProcessCollector.cs:71-74`, `WindowsServicesCollector.cs:64-66` |

`Win32_Process.Create` is a method call and is banned with all other CIM methods.

### 2.4 Registry — read-only, fixed whitelist (§2.1)

`RegistryKey.OpenBaseKey`/`OpenRemoteBaseKey` + `OpenSubKey(name)` (read-only overload; the `writable` overload is
banned) in `src/RSM.Collectors.Windows/RegistryValues.cs:43-61`; a key outside the whitelist is rejected before it is
opened (`:67`). Whitelist (`src/RSM.Collectors.Windows/RegistryValues.cs:14-21`), all under HKLM:

- `SYSTEM\CurrentControlSet\Control\PriorityControl` → `Win32PrioritySeparation`
- `SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending` (key exists)
- `SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired` (key exists)
- `SYSTEM\CurrentControlSet\Control\Session Manager` → `PendingFileRenameOperations`
- `SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes` → `ActivePowerScheme`
- `SOFTWARE\Microsoft\Windows Defender\Real-Time Protection` → `DisableRealtimeMonitoring`
- `SOFTWARE\Microsoft\Windows Defender\Exclusions\Paths` and `\Processes` → value names

The `RemoteRegistryReader` audited class of ARCHITECTURE §6.4 was never needed; `src/RSM.Safety/Audited/` contains only
its README.

### 2.5 Event logs — read only (§2.1)

`EventLogReader` over an `EventLogSession` (local or remote, Remote Event Log Management) with an XPath query,
`src/RSM.Collectors.Windows/EventLogReaderSource.cs:27-57`. Channels: `System`, `Application`
(`src/RSM.Collectors.Windows/WindowsEventLogCollector.cs:45`). `EventLog.CreateEventSource`/`WriteEntry`/`Clear`,
`EventLogSession.ClearLog`/`ExportLog*` and the whole `EventLogConfiguration` type are banned.

### 2.6 Server files — `FileAccess.Read` + `FileShare.ReadWrite | FileShare.Delete` (§2.1)

Only through `RSM.Safety.Files.ReadOnlyFile` (`src/RSM.Safety/Files/ReadOnlyFile.cs:10-15`), locally or over the admin
share. Files read:

| File | Default path (overridable from IIS config / site profile) | Where |
|---|---|---|
| `applicationHost.config` | `%SystemRoot%\System32\inetsrv\config\applicationHost.config` | `src/RSM.Collectors.Iis/IisTransport.cs:165` |
| `web.config` of each Rahkaran site | from the site's physical path in applicationHost.config | `src/RSM.Parsers/Iis/WebConfig.cs:101` |
| IIS W3C logs (tail / offset reads, size-capped) | `%SystemDrive%\inetpub\logs\LogFiles` or the site's `logFile.directory` | `src/RSM.Collectors.Iis/IisLogAnalysis.cs:30`, `IisAccess.cs:104` |
| HTTPERR logs | `%SystemRoot%\System32\LogFiles\HTTPERR` | `src/RSM.Collectors.Iis/IisTransport.cs:167` |
| SQL Server `ERRORLOG` (fallback when `xp_readerrorlog` is denied, §2.3) | path from `SERVERPROPERTY('ErrorLogFileName')`, local or admin share | `src/RSM.Collectors.Sql/Instance/SqlErrorLogCollector.cs:107-128` |
| Rahkaran version file (*unverified*, path entered by the owner in site settings — not hard-coded) | `SiteSettings.RahkaranVersionFile` (`src/RSM.Core/Profiles/SiteProfile.cs:53`) | `src/RSM.Collectors.Iis/IisConfigCollector.cs:82,120` |
| RSM's own inputs: imported bundles, rule overrides | user-chosen paths | `src/RSM.Hosting/BundleImportService.cs:24`, `src/RSM.Hosting/RuleRepository.cs:31-35` |

The web.config viewer redacts secret values (`tests/RSM.Tests` IIS parser tests, PLAN P3).

### 2.7 IIS (§2.1, §2.2.8)

`new ServerManager(readOnly: true, applicationHostPath)` only (`src/RSM.Collectors.Iis/LocalIisSources.cs:20`); the
parameterless and read-write constructors, `OpenRemote`, `CommitChanges`, `ApplicationPool.Start/Stop/Recycle`,
`Site.Start/Stop`, `ConfigurationMethodInstance.Execute`, `SetAttributeValue`, `SetMetadata` are banned. Members read:
application pools and sites (state), `WorkerProcesses` (PID, pool, state) and `WorkerProcess.GetRequests(minElapsedMs)`
(`src/RSM.Collectors.Iis/LocalIisSources.cs:28-36`, see R3). Remote IIS configuration is read as a file (§2.6), never
through `OpenRemote`. Certificates: `X509Store` (LocalMachine) opened `ReadOnly | OpenExistingOnly`, chains built
offline (`src/RSM.Collectors.Iis/LocalIisSources.cs:71-74`).

### 2.8 Redis (§2.3)

Only `RespWhitelistClient` (`src/RSM.Safety/Redis/RespWhitelistClient.cs:27`, raw TCP owned by `RSM.Safety`). Every
command is validated before it reaches the socket by `RedisCommandWhitelist.Validate`
(`src/RSM.Safety/Redis/RedisCommandWhitelist.cs:19-62`): `AUTH`, `PING`, `INFO [section]`, `DBSIZE`, `SLOWLOG GET n`
(1–1000), `SLOWLOG LEN`, `LATENCY LATEST`, `CLIENT LIST`, `CONFIG GET maxmemory*`; CR/LF/NUL in arguments rejected;
`AUTH` arguments never echoed. Tests: `tests/RSM.Safety.Tests/RedisTests.cs` (28 refused commands); CI confirmed with
`INFO commandstats` on a live Redis that only whitelisted commands arrived (PLAN P0).

### 2.9 HTTP (§2.3)

Only `RabbitGetClient` (`src/RSM.Safety/Http/RabbitGetClient.cs`): `HttpMethod.Get` (`:48`), paths limited to
`api/overview`, `api/queues`, `api/nodes` (`:14`, checked at `:43`), no redirects, no proxy (`:19`). The RabbitMQ
endpoint is configured per node and is absent by default (`src/RSM.Core/Profiles/SiteProfile.cs:134`). Tests:
`tests/RSM.Safety.Tests/RabbitGetClientTests.cs`. No other HTTP client exists (`HttpClient`, `WebClient`,
`WebRequest` banned outside `RSM.Safety`). The WebView2 report viewer is confined to the virtual host
`rsm-report.invalid` (arch test, P5).

### 2.10 Processes (§2.1, §2.2.6)

**Zero** call sites of `Process.Start`, `Kill`, PowerShell, runspaces or `Process` members at all outside tests: the
whole `Process` type is in `BannedSymbols.NonSafety.txt`, and the process list comes from CIM (§2.3). The audited
`ProcessSnapshot` class of ARCHITECTURE §6.4 was not needed. EventPipe: see R2.

### 2.11 Local writes (§2.1, §2.5)

Only `IDataFolder` (`src/RSM.Safety/Files/DataFolder.cs:44-137`) writes, and it rejects any path that escapes the
data folder (`DataFolderEscapeException`, `:42`; `tests/RSM.Safety.Tests/DataFolderTests.cs`). Writers: storage
(history, snapshots, bundles), reporting (HTML/PDF/JSON), Serilog file sink with the redaction enricher
(`src/RSM.Safety/Logging/Redaction.cs`), DPAPI secret store (`src/RSM.Safety/Secrets/SecretProtector.cs`).

### 2.12 Other network

DNS resolution of the Redis host (R5). No other outbound connection: no internet, telemetry or auto-update (§2.5);
certificate chains are built with `DisableCertificateDownloads = true` and `X509RevocationMode.NoCheck`.

## 3. §2.1 forbidden list — status

| §2.1 item | Status | Guard |
|---|---|---|
| Non read-only T-SQL, `EXEC` outside whitelist, `DBCC`, `KILL`, `RECONFIGURE`, `BACKUP`, `CHECKPOINT`, `SHUTDOWN`, `xp_cmdshell`, `OPENROWSET`/`OPENQUERY`, linked servers, XE/trace creation, Query Store changes | none in catalog or packs | SqlGuard (fail-closed allow-list), `SqlCatalogIsReadOnly`, malicious corpus (`tests/RSM.Safety.Tests/SqlGuardTests.cs`, 90 cases) |
| Starting processes (`Process.Start`, PowerShell, cmd, wevtutil, appcmd, netsh, typeperf, sc.exe, `Win32_Process.Create`) | none | `BannedSymbols.txt` / `.NonSafety.txt`, analyzer canary (`build/analyzer-canary.sh`, `tests/RSM.ArchTests/AnalyzerCanaryTests.cs`) |
| Service changes | none (services read via CIM `Win32_Service`) | banned `ServiceController` mutators |
| IIS changes, FREB/log fields | none | read-only `ServerManager`, banned mutators |
| WMI/CIM method calls | none | banned `InvokeMethod*`/`CreateInstance`/`ModifyInstance`/`DeleteInstance`, `ManagementObject.InvokeMethod`/`Put`/`Delete` |
| Registry writes, `EventLog.CreateEventSource`/`WriteEntry` | none | banned; writable `OpenSubKey` overload banned |
| File writes outside the data folder; delete/move of server files | none | `File`/`Directory`/`FileStream` banned outside `RSM.Safety`; `IDataFolder` escape check |
| Redis outside whitelist | none | `RedisCommandWhitelist` |
| HTTP other than GET | none | `RabbitGetClient` |
| Kill / recycle / restart / shrink / rebuild buttons (even disabled) | none | arch source rule on XAML command names (`tests/RSM.ArchTests/SourceRulesTests.cs`) |
| Free-text SQL editor | none | no SQL text input exists; catalog-key-only executor |

## 4. §2.2 enforcement mechanisms — evidence

| §2.2 | Mechanism | Evidence |
|---|---|---|
| 1 | Banned API analyzer, warnings as errors | `BannedSymbols.txt`, `BannedSymbols.NonSafety.txt`, `Directory.Build.props`; canary script + 21 in-process canaries |
| 2 | SQL query catalog, key-only execution, `SqlParameter` only | `src/RSM.Safety/Sql/QueryCatalog.cs`; scalar-parameter check in `SafeSqlExecutor.ValidateParameters`; arch rule "no C# string shaped like T-SQL" |
| 3 | SqlGuard (`TSql160Parser`, AST walk, table-variable INSERT, no `SELECT INTO`/temp tables) | `src/RSM.Safety/Sql/SqlGuard.cs`; rule ids SG001–SG067 (ARCHITECTURE §6.2) |
| 4 | `SqlCatalogIsReadOnly` + malicious corpus | `tests/RSM.Safety.Tests/SqlCatalogTests.cs:28`, `tests/RSM.Safety.Tests/SqlGuardTests.cs` |
| 5 | Connection hardening, own sessions excluded | §2.1 above; `tests/RSM.Safety.Tests/SqlCatalogTests.cs:47` |
| 6 | Architecture tests | `tests/RSM.ArchTests/DependencyTests.cs`, `SourceRulesTests.cs` |
| 7 | Whitelist RESP client | §2.8 above |
| 8 | Read-only IIS configuration | §2.7 above |
| 9 | Read-only proof (integration) with the §2.4 login | CI `integration` job: `tests/integration/setup-rsm-monitor.sql` creates the login, `tests/RSM.Integration.Tests/FullDiagnosisTests.cs` runs a full snapshot, `tests/integration/fingerprint.sql` proves the server unchanged; IIS config + pool state unchanged in the Windows job (CI run 23) |

## 5. §2.3–§2.6

- **§2.3 whitelists:** SQL `EXEC` (1 file), Redis (9 commands), HTTP (3 GET paths) — exactly as written.
- **§2.4 least privilege:** `PERMISSIONS.md` holds the DBA script (text only; RSM never runs it); Capability Matrix
  per node (`src/RSM.Core/Capabilities/CapabilityCatalog.cs`); sysadmin info banner fact `rsm.login_is_sysadmin`
  (`src/RSM.Hosting/SnapshotService.cs:111`).
- **§2.5 credentials & privacy:** DPAPI CurrentUser or "don't remember"; redaction enricher on every sink
  (`tests/RSM.Safety.Tests/RedactionTests.cs`); `Encrypt`/`TrustServerCertificate` per connection; report masking
  (`src/RSM.Reporting/Masking/Masker.cs`, 16 sensitive lab tokens tested in P1).
- **§2.6 low overhead:** sampling tiers and adaptive back-off (ARCHITECTURE §5); global SQL concurrency cap 2 per
  server; heavy operations on demand only (§2.1 above). Measured: ≤ 0.8 % of one core in the P2 CI soak
  (`docs/phase-notes/P2.md`). **Open:** the 1-hour soak on a loaded Windows lab server (PLAN P2 acceptance) — a
  human task, see `PERFORMANCE.md` once Phase F measures it.

## 6. What this audit cannot prove from source alone

PROMPT v2 Phase A is *done* only when "a full snapshot on a lab server with the least-privilege login of §2.4 shows
only read operations". CI proves this on SQL Server 2022 (Linux container) and IIS on `windows-2022`; the run on a
real Rahkaran lab server is the owner's task (guide: `/mnt/project-files/windows-2022-test/steps.fa.md`).
The Operations Audit log planned for Phase A (GAP_ANALYSIS.md) will make that lab evidence one click to export.
