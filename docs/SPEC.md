# Build Prompt — Rahkaran Server Monitor (RSM) / پایشگر سرور راهکاران

> **راهنمای استفاده (فارسی):** کل این فایل را در یک پوشه خالی به Claude Code بدهید (مثلاً با `claude` و سپس: «فایل PROMPT.md را بخوان و طبق آن جلو برو»). متن پرامپت عمداً انگلیسی است تا اصطلاحات فنی (DMV، Wait، Counter و …) دقیق منتقل شوند؛ رابط کاربری و گزارش‌های ابزار دوزبانه فارسی/انگلیسی خواهد بود. Claude Code باید اول برنامه و معماری را تحویل دهد و منتظر تأیید شما بماند، بعد فاز به فاز بسازد.

---

## 0. Your role and how to work

You are a senior Windows/.NET engineer **and** a senior SQL Server DBA / IIS performance engineer. You will design and build a **strictly read-only** diagnostic and monitoring desktop tool for servers running **Rahkaran** (راهکاران), the ERP of Systemgroup (همکاران سیستم), which runs on Windows Server + IIS + SQL Server.

Working rules:
1. **Start in plan mode.** Before writing code, deliver: `ARCHITECTURE.md` (solution layout, data flow, collector interfaces, safety layer), `SQL_CATALOG.md` (every query you plan to run, with DMV/permission/min SQL version), `RULES.md` (initial health-check rules), and a phased plan with acceptance criteria. **Stop and wait for my approval.**
2. Create a `CLAUDE.md` at the repo root that records the non-negotiable safety rules from §2 so every future session obeys them.
3. Build phase by phase (§18). At the end of each phase: build passes, all tests pass, short demo notes, git commit. Do not start the next phase until I confirm.
4. **Never invent Rahkaran internals** (table names, service names, DLL names). Anything product-specific that is not stated in §3 must be a configurable setting or a user-supplied read-only query file, clearly marked "unverified".
5. When a thresholds/recommendation is general industry guidance (not from the Systemgroup docs in §3), label it as such in `RULES.md` and in the UI.
6. Prefer correctness and safety over feature count. If an API needs elevation or a feature is missing on the server, degrade gracefully and say why in the UI — never crash, never "fix" the server.

---

## 1. Product goal

A portable, single-file Windows desktop application that an implementation/support engineer runs on (or against) a customer's Rahkaran servers to:
- See the **live** health of every layer (SQL Server, databases, Windows OS, CPU/RAM/disk/network, IIS & web server, ARR, Redis, SSRS, Windows services, event logs).
- **Find the root cause of slowness** ("چرا راهکاران کند است؟") with a guided, evidence-based investigation across all layers on a single timeline.
- **Find errors** across all layers in one unified error timeline.
- Produce a **one-click full diagnostic report** (HTML/PDF + raw JSON bundle) to send to ستاد فنی.
- Keep **history and baselines** locally to compare "now" with "normal" and "before/after an update".
- Monitor **multiple servers** of one customer site (Data, Report, App ×N, ARR, Redis) together.

Confirmed decisions:
| Topic | Decision |
|---|---|
| Platform | **.NET 8 (LTS) WPF desktop**, self-contained single-file `win-x64` exe, no installer |
| UI language | **Bilingual Persian (RTL) / English (LTR)**, switchable at runtime; reports in the chosen language |
| v1 features | Live dashboard · Snapshot report export · Multi-server / remote · History & baseline |
| Hard constraint | **Read-only. The tool must not be able to change anything or execute any command on any server.** |

---

## 2. NON-NEGOTIABLE: read-only by construction

The tool is a reporting tool only. It must be **impossible by design**, not just by convention, for it to modify a server. Enforce all of the following and cover them with tests.

### 2.1 Forbidden actions (anywhere in the codebase)
- Any T-SQL other than read-only `SELECT` (incl. CTEs), `DECLARE`/`SET` of local variables, and inserts into batch-local table variables (see §2.2). No DML, DDL, DCL, `EXEC`/`sp_executesql` (except the explicit whitelist in §2.3), `DBCC` (none), `KILL`, `RECONFIGURE`, `sp_configure` writes, `BACKUP`, `CHECKPOINT`, `SHUTDOWN`, `xp_cmdshell`, `OPENROWSET`, `OPENQUERY`, linked-server calls, XE session creation, trace creation, Query Store changes.
- Starting any process: `Process.Start`, PowerShell runspaces, `cmd.exe`, `wevtutil`, `appcmd`, `netsh`, `typeperf`, `sc.exe`, WMI `Win32_Process.Create`.
- Changing services: `ServiceController.Start/Stop/Pause/Continue/ExecuteCommand`, changing startup type.
- Changing IIS: `ServerManager.CommitChanges`, `ApplicationPool.Start/Stop/Recycle`, `Site.Start/Stop`, `WorkerProcess` mutation, enabling FREB/logging fields.
- WMI/CIM `InvokeMethod` / any method call; CIM is used for **queries only**.
- Registry writes, `EventLog.CreateEventSource`, `EventLog.WriteEntry` (both write to the server registry/logs).
- File writes outside the tool's own data folder; deleting/moving any server file.
- Redis commands outside the whitelist in §2.3 (never `FLUSHDB`, `FLUSHALL`, `CONFIG SET`, `SHUTDOWN`, `DEBUG`, `CLIENT KILL`, `SAVE`, `BGSAVE`, writes).
- HTTP methods other than `GET` for optional management APIs (RabbitMQ).
- No "kill session", "recycle pool", "restart service", "shrink", "rebuild index" buttons — not even disabled ones. Recommendations are **text only**.
- No free-text SQL editor in v1.

### 2.2 Enforcement mechanisms (all required)
1. **Banned API analyzer:** add `Microsoft.CodeAnalysis.BannedApiAnalyzers` with a `BannedSymbols.txt` covering every API above (Process.Start, ServiceController mutating members, ServerManager.CommitChanges, ApplicationPool.Start/Stop/Recycle, Site.Start/Stop, CimSession.InvokeMethod*, ManagementObject.InvokeMethod, Registry*.SetValue/DeleteValue/CreateSubKey, EventLog.CreateEventSource/WriteEntry, File.Delete/Move, etc.). Treat warnings as errors.
2. **SQL query catalog:** every T-SQL statement lives in an embedded `.sql` resource file under `Queries/` (one file per query, with a header comment: purpose, min version, required permission, expected cost). Code may only execute queries by catalog key — no string-built SQL. Parameters only via `SqlParameter`.
3. **SqlGuard:** before any execution, parse the text with `Microsoft.SqlServer.TransactSql.ScriptDom` (`TSql160Parser`) and walk the AST; reject anything that is not a read-only `SELECT` (incl. `WITH`), `DECLARE`, or `SET @localVar`, except whitelisted `EXEC` targets in §2.3. Also reject `SELECT … INTO` and any `#temp`/`##temp` table. The only allowed write target is a batch-local table variable (`DECLARE @t TABLE …` followed by `INSERT INTO @t SELECT …`); SqlGuard must verify the INSERT target is a declared table variable.
4. **Unit test `SqlCatalogIsReadOnly`:** loads every embedded `.sql`, parses it, runs SqlGuard, fails the build on any violation. Second test: SqlGuard rejects a corpus of malicious samples (`; DROP`, `EXEC('…')`, `INSERT`, `UPDATE`, `MERGE`, `xp_cmdshell`, `SELECT INTO`, `OPENROWSET`, `WAITFOR`+DML, comments hiding statements, etc.).
5. **Connection hardening:** `Application Name=RSM-ReadOnlyMonitor`, `ApplicationIntent=ReadOnly`, per-command `CommandTimeout` (default 10 s, heavy queries configurable), and each batch prefixed by `SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED; SET LOCK_TIMEOUT 2000; SET DEADLOCK_PRIORITY LOW;` so the tool never blocks Rahkaran. Exclude the tool's own sessions (by program_name) from every session/request view.
6. **Architecture tests** (`NetArchTest.Rules`): collectors may not reference `System.Diagnostics.Process` (except read-only `GetProcesses`/`GetProcessById` — wrap them in one audited class), `System.ServiceProcess.ServiceController` mutators, etc.
7. **Redis client:** implement a tiny RESP client over a socket that can only send commands from a hard-coded whitelist (do **not** use a general client that exposes write APIs).
8. **IIS:** open configuration read-only (`new ServerManager(readOnly: true, …)` where the API supports it), never call `CommitChanges` (banned).
9. **Read-only proof test (integration):** run a full snapshot against a local SQL Server using a login that has only the permissions from §2.4; assert no permission error other than the expected "optional feature unavailable" ones, proving no write was attempted.

### 2.3 Explicit whitelists
- **SQL `EXEC` whitelist:** `master.sys.xp_readerrorlog` (read-only; may require extra permission — if denied, fall back to reading the ERRORLOG file via file access when local/admin-share is available). Nothing else.
- **Redis whitelist:** `AUTH`, `PING`, `INFO [section]`, `DBSIZE`, `SLOWLOG GET n`, `SLOWLOG LEN`, `LATENCY LATEST`, `CLIENT LIST`, `CONFIG GET maxmemory*` (tolerate "unknown/renamed command").
- **HTTP:** `GET` only, only to RabbitMQ management API endpoints `/api/overview`, `/api/queues`, `/api/nodes` (optional, off by default).

### 2.4 Least-privilege accounts (document in `PERMISSIONS.md`; the tool never runs these scripts — the customer's DBA does)
Provide a T-SQL script for the DBA (text in docs only), e.g.:
```sql
-- run by DBA, not by RSM
CREATE LOGIN rsm_monitor WITH PASSWORD = N'<strong password>', CHECK_POLICY = ON;
GRANT VIEW SERVER STATE      TO rsm_monitor;  -- 2022: covers VIEW SERVER PERFORMANCE/SECURITY STATE
GRANT VIEW ANY DEFINITION    TO rsm_monitor;
GRANT CONNECT ANY DATABASE   TO rsm_monitor;  -- 2014+
USE msdb;        CREATE USER rsm_monitor FOR LOGIN rsm_monitor;
                 ALTER ROLE SQLAgentReaderRole ADD MEMBER rsm_monitor;
                 ALTER ROLE db_datareader     ADD MEMBER rsm_monitor;  -- backupset, job history
USE ReportServer; CREATE USER rsm_monitor FOR LOGIN rsm_monitor;
                 GRANT SELECT ON dbo.ExecutionLog3 TO rsm_monitor;    -- verify object names per SSRS version
-- optional, verify on target version: USE master; GRANT EXECUTE ON sys.xp_readerrorlog TO rsm_monitor;
```
- Windows side: membership in **Performance Monitor Users**, **Event Log Readers**, WMI remote read (root\cimv2, root\WebAdministration), and — only for IIS config/worker-process requests and some logs — local Administrator. The UI must show a **Capability Matrix** per server (what works with the current credentials and why something is unavailable).
- If the SQL login is `sysadmin`, show an info banner ("high-privilege account used; RSM stays read-only").

### 2.5 Credentials & privacy
- SQL Authentication (primary) and Windows Authentication (optional). Windows credentials for remote CIM/WinRM optional.
- Passwords stored only with DPAPI (`ProtectedData`, CurrentUser scope) or not stored at all ("don't remember" option). Never logged. Logs (Serilog, file sink in tool data folder) must redact passwords, connection strings, and secrets.
- `Encrypt` (Mandatory/Optional/Strict) and `TrustServerCertificate` exposed per connection with a warning (Microsoft.Data.SqlClient defaults to encryption; older customer servers often have self-signed certs).
- No internet access, no telemetry, no auto-update.
- Report export has a **"mask sensitive data"** option (server names, IPs, logins, host names, DB names, query literals).

### 2.6 Low overhead — the tool must not slow Rahkaran down
- Default live sampling: 5 s for cheap counters, 15–30 s for DMV snapshots, 60 s+ for heavy views; all configurable; adaptive back-off when a query exceeds its budget.
- One in-flight query per server per collector; cancellable; global SQL concurrency cap per server (default 2).
- **Heavy operations are on-demand only, with a warning and time estimate:** `sys.dm_db_index_physical_stats` (LIMITED mode only, top-N largest tables), `sys.dm_os_buffer_descriptors`, full plan-cache XML scans, IIS log parsing of large files.
- Target overhead: < 2 % CPU on the monitored server in live mode.

---

## 3. Domain context — Rahkaran architecture (from Systemgroup / ستاد فنی docs)

Use this to build product-aware checks, topology discovery and Persian explanations. Items marked *(configurable)* must not be hard-coded.

**Tiers & components**
- **Data tier:** SQL Server (2022 + latest CU per ستاد فنی "SQL Server Tuning V.3"). Main Rahkaran DB must be **Full** recovery (or Bulk-logged) with transaction-log backups; **ReportServer / ReportServerTempDB** in **Simple**.
- **Reporting:** SSRS (ReportServer DBs). Customer-specific reports live in separate SSRS folders.
- **Application tier:** IIS-hosted web app (w3wp per app pool). Since v19 "Pioneer"/v20 it runs on **.NET Core / ASP.NET Core Module** (handle in-process `w3wp` and out-of-process `dotnet.exe` hosting). Config in `web.config`.
- **NUMA deployments:** one site per NUMA node — **AuxWebsite1, AuxWebsite2, …** — behind an **AppRouterWebsite** (internal ARR). App-pool NUMA settings depend on CPU generation/cores and are set with ستاد فنی.
- **External ARR server:** IIS ARR server farms, e.g. `RahkaranAppFarm_<City>` (created by the setup tool) plus manual farms for e-services (**ES-CS**, خدمات الکترونیک کارمند/مشتری) and **Mobile**, with URL Rewrite rules. After every Rahkaran update the app path segment ("X") changes and rewrite rules must be updated → a valuable automated check. Servers in a farm have weights (Weighted Round Robin), health/availability states.
- **Redis** (default port 6379) — cache/session; may live on an app server, not necessarily the DB server.
- **Licensing:** **CodeMeter** runtime (lock: LockBoxSerial + GUID); the dongle may be on a different server (network server).
- **Optional modules:** Workflow (گردش کار) requires **RabbitMQ + Erlang**; Attendance (تردد) runs its own Windows service *(name configurable)*; e-services and Mobile.
- **Setup tool:** `SgSetupTool.exe`. Typical topologies: single Data-App server; separate App + Data/Report; large multi-server with external ARR, NUMA app servers, separate Report and Data servers; "ابرآمد" cloud customers (only port 80 open).
- **Known symptoms from support KB:** high RAM / **GC wait** in the Rahkaran process and StackOverflow crashes (diagnosed with ProcDump full dumps of `w3wp.exe`); transaction-log growth (log_reuse_wait_desc, CDC capture job stopped/deleted); slow backups over slow network shares; maintenance-plan + "ابزار پایگاه داده" tool running together → job conflicts / broken backup chain.

**ستاد فنی baseline (SQL Server Tuning V.3) — encode as rules with source reference**
| Area | Expected |
|---|---|
| OS | 64-bit Windows; Power plan **High Performance**; CPU speed not below base frequency / not fluctuating; Processor scheduling = **Background services**; pagefile min = RAM, max ≤ 1.5 × RAM |
| Storage | SQL volumes formatted with **64 KB** allocation unit; hardware RAID with battery-backed cache; Data, Log, TempDB on disks separate from OS/pagefile when possible |
| Instance | Min server memory **15 %**, Max server memory **65 %** of RAM (when app + SQL share the server); Priority boost **off**; Max worker threads **0**; Lightweight pooling **off**; Backup compression default **on**; **MAXDOP 4**; Cost threshold for parallelism = value from ستاد فنی tool (warn if manually > 100); Optimize for ad hoc workloads **on** |
| Database | Log initial size 1024 MB (large customers 10240 MB); data-file autogrowth in **MB, multiple of 1024**, unrestricted; free space on data drive ≥ 2 × DB size; Page verify **CHECKSUM**; do not touch Automatic/Cursor/Miscellaneous options |
| Maintenance | Full daily (22:00), Log backup every 2 h in working hours, CHECKDB weekly, index rebuild/reorganize + stats nightly, cleanup > 1 week; large DBs: daily Differential + weekly Full; extra backups must be COPY_ONLY; **never schedule shrink of database/data files**; don't mix ستاد فنی tool jobs with a manual maintenance plan; SQL Agent Automatic & running; sa must have a password |
| Other | Antivirus exclusions for SQL Server process and data files; recovery-model change to Simple is forbidden without ستاد فنی |

**HPE server prep (informational checks when hardware is HPE):** Power profile Maximum Performance, Hyper-Threading enabled, Virtualization disabled on physical non-virtualized hosts, RAID 10 with 64 KiB stripe for SQL, physical drive write cache disabled, firmware via SPP. Only report what is readable from Windows (model, BIOS version, HT on/off, logical vs physical cores, VM detection).

---

## 4. Tech stack

- **.NET 8**, C# 12, nullable enabled, `TreatWarningsAsErrors`.
- **WPF + MVVM:** `CommunityToolkit.Mvvm`; DI/hosting: `Microsoft.Extensions.Hosting`; UI kit: `WPF-UI` (lepoco/wpfui) Fluent style, light/dark — verify RTL rendering; fall back to plain styled WPF where RTL breaks.
- **Charts:** `ScottPlot.WPF` 5 (streaming-friendly) — or justify an alternative.
- **SQL:** `Microsoft.Data.SqlClient`, `Dapper`, `Microsoft.SqlServer.TransactSql.ScriptDom`.
- **Windows:** `System.Diagnostics.PerformanceCounter` / PDH (local), `Microsoft.Management.Infrastructure` (CIM over WinRM/DCOM, queries only) for remote, `System.Diagnostics.Eventing.Reader` (local & remote event logs), `System.ServiceProcess.ServiceController` (read members only).
- **IIS:** `Microsoft.Web.Administration` (local; remote where permitted), fallback CIM namespace `root\WebAdministration`.
- **.NET runtime telemetry of the Rahkaran process (optional, off by default):** `Microsoft.Diagnostics.NETCore.Client` EventPipe session with `System.Runtime` and `Microsoft.AspNetCore.Hosting` counters (GC heap, % time in GC, gen0/1/2 counts, threadpool queue length/thread count, exceptions/sec, requests/sec, current requests, failed requests). Read-only telemetry; no dumps.
- **Storage:** `Microsoft.Data.Sqlite` in the tool's data folder (portable mode: `./data` next to exe; otherwise `%LOCALAPPDATA%\RSM`).
- **Reports:** HTML via `Scriban` templates (self-contained, inline CSS/SVG, RTL/LTR); PDF via `QuestPDF` (check Community license fits the employer; otherwise PDF via printing the HTML) ; raw bundle = zip of JSON.
- **Localization:** `.resx` (`Strings.fa.resx`, `Strings.en.resx`), runtime switch, `FlowDirection` toggle, embedded **Vazirmatn** font (OFL), Persian (Jalali, `PersianCalendar`) + Gregorian timestamps, optional Persian digits.
- **Logging:** Serilog (file, rolling, redaction enricher).
- **Tests:** xUnit, FluentAssertions, NetArchTest, Verify (snapshot tests for report HTML).
- **Publish:** `PublishSingleFile`, `SelfContained`, `IncludeNativeLibrariesForSelfExtract`, no trimming (WPF). Manifest `asInvoker`; show which capabilities need "Run as administrator" and offer to relaunch the tool itself elevated (that only restarts RSM, nothing on the server).
- **Compatibility targets:** Windows Server 2016–2022 (best effort 2012 R2), SQL Server 2016–2022 (version-gate every query; record min version in the catalog), IIS 8.5–10, Redis for Windows 3.x / Memurai / Redis 5+.

---

## 5. Solution structure (proposal — refine in ARCHITECTURE.md)

```
RSM.sln
 ├─ src/RSM.App                 WPF shell, views, view-models, localization, theming
 ├─ src/RSM.Core                domain models, ICollector, metric/finding/evidence models, rules engine, scheduler
 ├─ src/RSM.Safety              SqlGuard, query catalog loader, Redis whitelist, BannedSymbols.txt
 ├─ src/RSM.Collectors.Sql      instance, waits, requests, blocking, deadlocks, queries, IO, memory, tempdb, agent, backups, security
 ├─ src/RSM.Collectors.Database per-database health, files, VLFs, log reuse, CDC, integrity, indexes/stats (on demand)
 ├─ src/RSM.Collectors.Windows  perf counters, CIM inventory, services, event logs, disks, network, pending reboot
 ├─ src/RSM.Collectors.Iis      sites, pools, worker processes & live requests, counters, IIS/HTTPERR/ANCM logs, ARR, URL Rewrite, certs
 ├─ src/RSM.Collectors.Redis    RESP whitelist client + INFO/SLOWLOG parsing
 ├─ src/RSM.Collectors.Rahkaran topology discovery, version consistency, CodeMeter/RabbitMQ/attendance services, SSRS execution log, custom query packs
 ├─ src/RSM.Storage             SQLite schema, downsampling, retention, baselines
 ├─ src/RSM.Reporting           HTML/PDF/JSON bundle, masking, bundle import/merge
 ├─ src/RSM.Cli                 headless mode: --snapshot, --collect --duration, --export (same read-only collectors)
 └─ tests/ RSM.Tests, RSM.Safety.Tests, RSM.ArchTests, RSM.Integration.Tests
```

Core abstractions:
- `ICollector` → `CollectAsync(ServerContext, CancellationToken)` returning typed results + `CollectorStatus` (OK / Partial / Unavailable(reason) / Error).
- `IMetricSource` with `LocalPdhSource` and `RemoteCimSource` implementations (same metric keys).
- `Finding { RuleId, Severity(Critical/Warning/Info/OK), Layer, Title(fa/en), Explanation(fa/en), Evidence[], Recommendation(fa/en), SourceRef, Verified(bool), Confidence }`.
- Rules are **data-driven** (`rules/*.json` editable by the team): id, layer, condition (expression over collected values), thresholds, severity, texts, source reference ("SQL Server Tuning V.3 §3", "Log growth KB 1403/12/28", "general guidance").

---

## 6. Connections, sites & topology

- **Site profile** (= one customer) contains **server nodes** with roles: Data, Report, App, ARR, Redis, Lock (CodeMeter), Workflow (RabbitMQ). One machine can have several roles.
- Per node: host/IP, optional Windows credentials (remote CIM/WinRM, event logs, admin-share file read), SQL instance + auth (SQL Auth default), Redis endpoint + password, optional RabbitMQ management URL + creds.
- **Test connection** produces the Capability Matrix (SQL connect, effective permissions via `fn_my_permissions`/`HAS_PERMS_BY_NAME`, SQL version, WMI reachable, counters readable, event log readable, IIS config readable, Request Monitor feature present, log folders readable, Redis reachable).
- **Topology auto-discovery** (read-only): from IIS sites/pools/ARR farms/URL Rewrite + SQL `sys.dm_exec_sessions` (host_name, program_name) + Redis `CLIENT LIST` → draw a map: Clients → ARR → App servers (Aux sites/NUMA nodes) → SQL / Redis / SSRS. Show connection counts per app host to detect ARR imbalance.
- **Two operating modes:** (a) *Remote mode*: one RSM instance reaches all nodes; (b) *Local-per-server mode*: run RSM on each server, export a snapshot bundle, then **import & merge bundles** in one RSM to get a unified site report (for sites where remote access is not allowed).

---

## 7. SQL Server instance module

Everything below via catalog queries; version-gate; show Persian/English explanation tooltips for every metric (what it is, normal range, why it matters for Rahkaran).

1. **Inventory:** `@@VERSION`, `SERVERPROPERTY` (ProductVersion, ProductLevel, ProductUpdateLevel, Edition, Collation, IsClustered, IsHadrEnabled), `sys.dm_os_sys_info` (cpu_count, hyperthread_ratio, socket/cores, numa_node_count, physical_memory_kb, committed/target, sqlserver_start_time, virtual_machine_type_desc), `sys.dm_os_host_info` (2017+), `sys.dm_server_services` (account, startup, instant_file_initialization_enabled), `sys.dm_os_nodes`/schedulers. **CU check** against a shipped, editable `known-builds.json` (no internet).
2. **Configuration compliance:** `sys.configurations` (value vs value_in_use, pending restart) compared with the ستاد فنی baseline (§3) — max/min server memory as % of RAM (consider whether IIS shares the box), MAXDOP, cost threshold, optimize for ad hoc, backup compression, priority boost, max worker threads, lightweight pooling; security-relevant: xp_cmdshell, Ole Automation, CLR, remote admin connections, cross db ownership chaining.
3. **Live performance counters** from `sys.dm_os_performance_counters` with correct delta math for per-second counters (cntr_type 272696576) and ratio/base counters: Batch Requests/sec, SQL Compilations/sec, Re-Compilations/sec, Page Life Expectancy (overall and per NUMA via Buffer Node), Lazy writes/sec, Free list stalls/sec, Checkpoint pages/sec, Memory Grants Pending/Outstanding, Target vs Total Server Memory, User Connections, Processes blocked, Lock Waits/sec, Lock Timeouts/sec, Number of Deadlocks/sec, Full Scans/sec, Page Splits/sec, Log Flushes/sec, Log Flush Wait Time, Transactions/sec per DB.
4. **CPU attribution:** `sys.dm_os_ring_buffers` (RING_BUFFER_SCHEDULER_MONITOR) → SQL CPU % vs other-process CPU % for the last ~4 hours (answers "is it SQL or the app/IIS on the same box?"). Signal-wait % from wait stats.
5. **Wait statistics:** `sys.dm_os_wait_stats` deltas between samples, with the standard benign-wait exclusion list (sleep/queue/XE/broker/QDS/HADR idle waits, etc.). Group into categories with Persian explanations and next-step hints: CPU (SOS_SCHEDULER_YIELD, CXPACKET/CXCONSUMER, THREADPOOL), IO (PAGEIOLATCH_*, IO_COMPLETION, WRITELOG, ASYNC_IO_COMPLETION), Locking (LCK_M_*), Memory (RESOURCE_SEMAPHORE, RESOURCE_SEMAPHORE_QUERY_COMPILE), Latch/TempDB (PAGELATCH_* on db 2), Network/client (ASYNC_NETWORK_IO = application not consuming results fast enough → points to the Rahkaran app tier), Log (LOGBUFFER, WRITELOG). Also per-session waits `sys.dm_exec_session_wait_stats`.
6. **Active requests ("what is running now"):** `sys.dm_exec_requests` + `sys.dm_exec_sessions` + `sys.dm_exec_connections` + `sys.dm_exec_sql_text` (statement offset extraction) + `sys.dm_exec_input_buffer` + `sys.dm_exec_text_query_plan` (on click): session, login, host, program, database, status, command, wait type/time/resource, blocking_session_id, CPU, reads, writes, elapsed, granted memory, DOP, percent_complete, open transaction count. Plan viewer: show XML + parsed warnings (no SSMS needed).
7. **Blocking:** live blocking chains with head-blocker tree (recursive CTE), duration, lock resources (`sys.dm_tran_locks` summarized, on demand), the head blocker's last statement and open transaction age. Text-only guidance (never a KILL button).
8. **Deadlocks:** read `xml_deadlock_report` from the `system_health` session (`sys.fn_xe_file_target_read_file('system_health*.xel', …)`, fallback ring_buffer) — never create XE sessions. Parse graph: victim, processes, statements, objects/indexes, lock modes; list + visual graph; frequency over time.
9. **Long/open transactions:** `sys.dm_tran_active_transactions`, `sys.dm_tran_session_transactions`, `sys.dm_tran_database_transactions` (begin time, log bytes used/reserved) — link to log growth.
10. **Top queries:** `sys.dm_exec_query_stats` (+ `sys.dm_exec_procedure_stats`) top-N by total/avg CPU, duration, logical reads, writes, executions, memory grant, spills (2016 SP2+ columns); Query Store per DB when enabled (top consumers, regressed queries, forced plans — read only). **Plan analysis** of showplan XML: missing-index hints, CONVERT_IMPLICIT, spills to tempdb, key/RID lookups, large scans, excessive memory grants, parallelism, no-join-predicate warnings. Plan-cache health: single-use plan bloat (ties to "optimize for ad hoc").
11. **Missing / unused indexes:** `sys.dm_db_missing_index_*` (improvement measure) and `sys.dm_db_index_usage_stats` — **display only**, with a fixed caution: "Rahkaran schema is vendor-owned; do not create/drop indexes without ستاد فنی."
12. **Memory:** `sys.dm_os_process_memory` (memory-low notifications, locked pages), `sys.dm_os_memory_clerks` top, plan cache size by objtype, memory grants `sys.dm_exec_query_memory_grants`, buffer pool by DB (on demand only).
13. **TempDB:** file count vs logical cores (up to 8), equal size/growth, version store size, per-session usage (`sys.dm_db_session_space_usage`, `sys.dm_db_task_space_usage`), PAGELATCH contention on allocation pages.
14. **I/O:** `sys.dm_io_virtual_file_stats` deltas → read/write latency per file; thresholds (general guidance, configurable): data read > 20 ms warn / > 50 ms critical, log write > 5 ms warn / > 15 ms critical; `sys.dm_io_pending_io_requests`; `sys.dm_os_volume_stats` free space per volume; detect data/log/tempdb on the same volume.
15. **SQL Server error log:** via whitelisted `xp_readerrorlog` (current + N archives) or file read fallback. Classify: 823/824/825 (I/O corruption risk), 833 (I/O > 15 s), 17883/17884 (non-yielding scheduler), 701/802 (memory), 9002 (log full), 1205 (deadlock), 18456 (failed logins grouped by state/IP), 3041 (backup failure), stack dumps, "SQL Server has encountered n occurrence(s) of cachestore flush".
16. **SQL Agent:** service state; `msdb` jobs, schedules, last outcome, failures (last 7/30 days), currently running, duration anomalies vs history. Specific checks: backup jobs exist and succeed; **CDC capture/cleanup jobs** exist & running for CDC-enabled DBs; detect both ستاد فنی-tool jobs and a manual maintenance plan doing backups (chain-break risk); job steps containing `SHRINKDATABASE`/`SHRINKFILE` on data files (warning); backup destinations on UNC paths (slow network risk).
17. **Security posture (read-only audit):** auth mode, `sa` enabled/renamed/has password policy, sysadmin members, logins with `is_policy_checked = 0`, orphaned users, guest enabled in user DBs, TLS/encryption of connections (`sys.dm_exec_connections.encrypt_option`), SQL Browser, linked servers present.
18. **HA (if present):** Always On (`sys.dm_hadr_*` — send/redo queues, sync state), mirroring, log shipping — informational.

---

## 8. Database module (per database; auto-detect the main Rahkaran DB(s) by size/activity, user-overridable)

1. `sys.databases`: state, recovery model (**Full** required for main Rahkaran DB; **Simple** expected for ReportServer/ReportServerTempDB), page_verify (CHECKSUM), auto_close/auto_shrink (must be off), auto stats, compatibility level, RCSI/snapshot (report only), `log_reuse_wait_desc` with Persian explanation & action from the log-growth KB (NOTHING, CHECKPOINT, LOG_BACKUP, ACTIVE_BACKUP_OR_RESTORE, ACTIVE_TRANSACTION, DATABASE_MIRRORING, REPLICATION → in Rahkaran almost always **CDC**, AVAILABILITY_REPLICA, OLDEST_PAGE, XTP_CHECKPOINT), `is_cdc_enabled`.
2. **Files:** size, used, free, growth type/value (percent growth → warning; not a multiple of 1024 MB → info), max size, volume free space vs ≥ 2 × DB size, log size vs 1024/10240 MB guideline, **VLF count** (`sys.dm_db_log_info`, 2016 SP2+) warn > 1000, `sys.dm_db_log_space_usage`.
3. **Backups (msdb.backupset/backupmediafamily):** last Full/Diff/Log, RPO now, log-backup interval vs 2 h guideline, gaps in LSN chain, COPY_ONLY usage, checksum flag, duration & throughput trend, destination (local vs UNC), size trend → growth forecast and recommended autogrowth size.
4. **Integrity:** `DATABASEPROPERTYEX(name,'LastGoodCheckDbTime')` (2016 SP2+; otherwise "unknown"), `msdb.dbo.suspect_pages`.
5. **Size & growth:** top tables by reserved/used (`sys.dm_db_partition_stats`), growth from backup history.
6. **Index & statistics health (on demand, with warning):** `sys.dm_db_index_physical_stats` LIMITED for top-N largest tables; `sys.dm_db_stats_properties` (rows, modification_counter, last_updated) → stale stats list.
7. **Custom query packs (Rahkaran-specific insights):** a `QueryPacks/` folder where the team can drop `.sql` files + a small JSON manifest (title fa/en, target DB, columns, thresholds). Every file passes SqlGuard at load time or is rejected with the reason. Use this for things like Rahkaran version/user-count tables once the team confirms their names (do not guess them).

---

## 9. Windows OS module (local PDH or remote CIM, same metric keys)

1. **Inventory:** OS caption/build, uptime/last boot, installed hotfixes, manufacturer/model/BIOS (flag HPE → firmware/SPP reminder), physical vs virtual (hypervisor present, model), sockets/cores/logical processors (HT on?), NUMA node count, RAM, pagefile settings & usage vs rule (min = RAM, max ≤ 1.5 × RAM), active **power plan** (`root\cimv2\power` Win32_PowerPlan IsActive) must be High Performance, **CPU throttling** (`Processor Information(_Total)\% Processor Performance` / current vs max clock), processor scheduling (registry `Win32PrioritySeparation`, read only), time zone, domain, **pending reboot** (CBS RebootPending, WindowsUpdate RebootRequired, PendingFileRenameOperations — registry reads only), Defender status & exclusions if readable (SQL data/log paths and sqlservr.exe should be excluded).
2. **CPU live:** total & per core, per NUMA node, Processor Queue Length, context switches/sec, % Privileged/User/Interrupt time; **top processes** by CPU/RAM with friendly role mapping: sqlservr, w3wp → **app pool name** (via IIS worker process list), dotnet (out-of-process ASP.NET Core), redis-server, CodeMeter, erl/epmd (RabbitMQ), ReportingServicesService, attendance service *(configurable)*, antivirus processes.
3. **Memory:** Available MBytes (warn < 10 %, critical < 5 % or < 2 GB — general guidance), Committed Bytes vs Commit Limit, Pages/sec, Page Faults/sec, Pool Nonpaged, Standby/Modified lists, per-process Working Set & Private Bytes (w3wp per pool, sqlservr, redis). Detect SQL + w3wp competing for RAM on a shared Data-App box against max server memory.
4. **.NET runtime of Rahkaran (optional toggle):** EventPipe counters for the w3wp/dotnet process of each Rahkaran pool: GC heap size, % time in GC (**GC wait** symptom from KB), gen2 collections rate, LOH size, allocation rate, threadpool queue length & thread count (starvation), exceptions/sec, monitor lock contention, working set; ASP.NET Core requests/sec, current requests, failed requests. For .NET Framework processes use ".NET CLR Memory/Exceptions" counters if present.
5. **Disk:** per logical disk % free, Avg. Disk sec/Read & sec/Write, Disk Transfers/sec, Current/Avg Disk Queue Length, Split IO/sec; volume **block size** (Win32_Volume.BlockSize) for SQL data/log/tempdb volumes must be 64 KB; flag Data+Log+TempDB+OS+pagefile on one volume.
6. **Network:** per NIC bytes/sec vs link speed, output queue length, packets received errors/discarded; TCP connection states (Established/TimeWait) and ephemeral-port pressure; SQL round-trip latency from the RSM host (`SELECT 1` timing) and App→DB placement hints.
7. **Services (display only — no controls):** status, startup type, account, PID, uptime for: MSSQLSERVER / MSSQL$*, SQLSERVERAGENT / SQLAgent$*, SQLBrowser, SSRS (`SQLServerReportingServices` / ReportServer*), W3SVC, WAS, Redis, CodeMeter Runtime, RabbitMQ, attendance *(configurable pattern)*, SGProactiveMonitor *(configurable)*, Windows Update, antivirus. Findings: Automatic but stopped; recent unexpected terminations (SCM events 7031/7034); recent restarts.
8. **Event logs (System, Application, optional Microsoft-Windows-IIS-*, CodeMeter):** last N hours, errors/criticals grouped by source+ID with counts, first/last occurrence, sample message. Built-in knowledge base (fa/en explanation + next step) for at least: Kernel-Power 41, EventLog 6008, User32 1074, Resource-Exhaustion-Detector 2004, srv 2013 (low disk), SCM 7031/7034/7000/7009/7023, Application Error 1000, .NET Runtime 1026 (unhandled exception, include w3wp), Windows Error Reporting 1001, WAS 5002/5009/5010/5011/5013/5117 (app-pool rapid-fail/ping timeout/crash), ASP.NET 1309, IIS AspNetCore Module (ANCM) start failures, Disk 7/11/51/153, Ntfs 55/98, MSSQLSERVER 823/824/833/17883/701/9002/18456, Schannel 36887/36874, Time-Service.

---

## 10. IIS & web-server module

1. **Sites:** name, id, state, bindings (protocol/host/port/cert), physical path, applications, log folder & enabled W3C fields (**warn if `time-taken` is not logged** — it is required for latency analysis; never change it), Rahkaran role detection (AuxWebsiteN, AppRouterWebsite, ES-CS, Mobile — name patterns configurable).
2. **Application pools:** state, managed runtime ("No Managed Code" expected for ASP.NET Core), pipeline mode, identity, start mode, idle timeout, recycling (periodic time, requests, memory limits, specific times), rapid-fail protection, queue length, maxProcesses (web garden), CPU limit/action, 32-bit flag, **NUMA settings** (processor affinity, `numaNodeAssignment`, `numaNodeAffinityMode`). Compare pools across app servers in the same farm (config drift = finding). Recommendations for pool settings that are not in the Systemgroup docs must be shown as "general guidance — confirm with ستاد فنی".
3. **Worker processes & live requests:** PID per pool, CPU/RAM, uptime (recent recycle?), and **currently executing requests** via `WorkerProcess.GetRequests(timeElapsedFilterMs)` (needs admin + IIS *Request Monitor* feature): URL, verb, client IP, time elapsed, pipeline stage, current module — the best live view for "which pages are slow right now".
4. **Counters:** Web Service (per site) current connections, requests/sec by verb, bytes; `W3SVC_W3WP` (per pool) active requests, requests/sec, active threads; **HTTP Service Request Queues** (per pool) CurrentQueueSize, RejectedRequests, ArrivalRate (queue > 0 sustained = app saturated); `APP_POOL_WAS` current state, worker-process failures, recent failures, uptime; ASP.NET (Framework) requests queued/execution time if present.
5. **IIS W3C logs (read-only, FileShare.ReadWrite, streaming parser):** selectable time range; requests/min, status distribution (4xx/5xx with substatus & win32-status), **latency percentiles p50/p95/p99 by URL stem**, slowest URLs, top clients (ARR servers / X-Forwarded-For if logged), bursts; special decoding: 503 (queue full / pool stopped), 500.x, **502.3 / 502.5 / 500.30–500.37 (ASP.NET Core Module)**, win32 64/1236 (client disconnect).
6. **HTTPERR logs** (`%SystemRoot%\System32\LogFiles\HTTPERR`): Timer_ConnectionIdle, Timer_MinBytesPerSecond, QueueFull, AppOffline, Connection_Dropped, Connection_Abandoned_By_ReqQueue — counts over time.
7. **ASP.NET Core stdout / ANCM logs** if `stdoutLogEnabled` paths exist (tail, error highlights). **FREB** files if they already exist (list/parse; never enable tracing).
8. **web.config (read-only, secrets masked):** hosting model (in/out-of-process), `processPath`, environment variables, request limits, connection-string targets (server/DB only; mask credentials and any key matching password|pwd|secret|token|key), Redis address. Compare across app servers (drift).
9. **ARR (on the ARR server and internal AppRouterWebsite):** web farms, servers, weights, load-balancing algorithm, health-test URL/interval, availability (available / unavailable gracefully / offline), and runtime counters (current/total/failed requests, response time, health) if readable from the ARR runtime elements — verify against the ARR 3.0 schema; fallback: config-only + distribution measured from IIS logs. Findings: unhealthy/offline servers, distribution far from configured weights, a farm where only part of the servers are available.
10. **URL Rewrite:** global & site rules (pattern, conditions, action/URL, target farm). **Rahkaran check:** rewrite rules whose path segment no longer matches the deployed application path after an update (the "X changes after update" problem from the ARR KB).
11. **TLS certificates** bound to sites: expiry (warn < 30 days), key size, chain issues (read-only from LocalMachine\My).

---

## 11. Redis, SSRS, CodeMeter, RabbitMQ

- **Redis (whitelist client):** version/mode, uptime (recent restart?), used_memory vs maxmemory, mem_fragmentation_ratio, evicted_keys, expired_keys, keyspace hit ratio, connected/blocked/rejected clients, instantaneous ops/sec, persistence status (rdb/aof last status), SLOWLOG entries, LATENCY LATEST, clients per source host (CLIENT LIST) to see which app servers use it. Windows service status on its host.
- **SSRS:** service status; from `ReportServer` DB (`ExecutionLog3` or version equivalent): top slow reports (TimeDataRetrieval vs TimeProcessing vs TimeRendering), failures (Status ≠ rsSuccess), top users/reports, peak hours, subscription failures (last status). Explain whether slowness is data (SQL) or rendering.
- **CodeMeter:** service/process running, listening port, recent CodeMeter/SCM errors in event logs (no license API calls in v1).
- **RabbitMQ (Workflow module, optional):** services (RabbitMQ, epmd/erl) status; optional management API `GET` only: node memory/fd/socket alarms, queue depth, unacked messages, consumers=0 queues (workflow stuck).

---

## 12. Guided "Why is Rahkaran slow?" investigation (key feature)

- One button starts a **synchronized capture** (default 120 s, configurable) across all nodes: SQL waits/requests/blocking/IO/counters, Windows CPU/RAM/disk/network, IIS queue/active requests/live requests, .NET runtime counters (if enabled), Redis INFO, plus IIS log tail.
- Then a **decision engine** (rules + evidence scoring) produces a ranked list of likely causes with evidence and confidence, e.g.:
  - Host CPU saturated by a non-SQL process / SQL CPU high with high signal waits / CPU throttled by power plan.
  - Memory pressure (low Available MB, paging, SQL memory grants pending, PLE drop, SQL vs w3wp competition, high % time in GC).
  - Disk latency on data/log/tempdb; WRITELOG + slow log disk.
  - Blocking chain with head blocker X (statement, open transaction age).
  - Expensive queries currently running / plan regressions (Query Store).
  - ASYNC_NETWORK_IO → app tier slow consuming results (look at w3wp CPU/GC/threadpool).
  - IIS request queue > 0, threadpool starvation, app pool recycled/rapid-failed recently, 503s.
  - ARR imbalance or an unhealthy node; Redis evictions/latency/restart.
  - Network saturation or high SQL round-trip latency.
- Each cause shows: evidence values with timestamps, the charts on a **shared timeline**, Persian/English explanation, and **text-only next steps** (e.g. "capture a full memory dump of w3wp with ProcDump per KB; coordinate downtime or take the server out of ARR first" — the tool itself never does it).
- Result can be saved as a snapshot and exported.

---

## 13. Unified error investigation

A single **Errors & Events timeline** merging: Windows events, SQL error log, SQL Agent failures, deadlocks, IIS 5xx bursts, HTTPERR, ANCM/stdout errors, service crashes, app-pool failures, Redis restarts, backup failures. Filters: time range, node, layer, severity, text. Correlation view: events ±5 min around a selected slowness spike. Each known event ID/error number links to the built-in fa/en knowledge base.

---

## 14. Live dashboard & UI

- Left navigation (fa/en): نمای کلی · SQL Server · دیتابیس‌ها · کوئری‌ها و Blocking · ویندوز (CPU/RAM/دیسک/شبکه) · IIS و وب‌سرور · ARR · Redis · SSRS · سرویس‌ها · رخدادها و خطاها · بررسی کندی · گزارش‌ها · تاریخچه و Baseline · تنظیمات.
- **Overview:** site topology map with per-node status (green/amber/red), health score per layer, top 10 active findings, key sparklines.
- Charts with 15/30/60-minute rolling windows, pause/resume, refresh interval selector (2/5/10/30/60 s), threshold bands, hover values, multi-node overlay.
- Grids: sort, filter, column chooser, copy, CSV export; SQL text with copy button and syntax highlighting (AvalonEdit, read-only).
- Every metric/finding: tooltip with "what / normal range / why it matters / source".
- Theme light/dark; full RTL mirroring in Persian; Vazirmatn font; Jalali + Gregorian dates; numbers optionally Persian digits.
- Status bar: connection state per node, collector errors, current overhead estimate.

---

## 15. History, baseline & comparison (SQLite)

- Schema (refine): `sites`, `nodes`, `metric_samples(ts, node_id, metric_key, instance, value)` with downsampling (raw 24 h → 1-min 7 days → 15-min 90 days; configurable retention), `snapshots(id, ts, node_id, kind, json_zstd)` for waits/query stats/requests/IIS summaries, `findings_history`, `events_cache`.
- **Baseline:** per metric, per hour-of-week median & p95 over the last N weeks; flag anomalies (e.g. > p95 × 1.5 for ≥ 3 samples) in live view.
- **Compare periods:** "this week vs last week", "before vs after update" — side-by-side KPIs, waits, top queries, IIS latency percentiles.
- **Headless collection:** `RSM.Cli --collect --site <profile> --duration 8h` (same read-only collectors; runs only while the user keeps it running; never installs a service or scheduled task).

---

## 16. Snapshot report

- **"Full diagnosis" button:** runs all collectors once (heavy ones optional), evaluates rules, produces:
  - **HTML** (self-contained, inline CSS/SVG charts, collapsible sections, RTL/LTR by language),
  - **PDF**,
  - **JSON bundle (zip)** with raw collected data + tool version + capability matrix (importable/mergeable by another RSM).
- Structure: cover (site, nodes, date Jalali/Gregorian, RSM version) → executive summary (overall score, critical/warning counts, top 5 issues) → per-layer sections (inventory, compliance table vs ستاد فنی baseline, findings with evidence/recommendation/source, key charts) → appendix (capability matrix, collectors that failed and why, glossary).
- Masking option for sharing; file names `RSM_<site>_<yyyyMMdd-HHmm>`.

---

## 17. Non-functional requirements

- Resilient: any collector failure yields partial results + a visible reason; timeouts never freeze the UI (async everywhere, cancellation tokens).
- Performance: UI stays responsive with 6 nodes × live mode; memory footprint of RSM < 500 MB.
- Security: §2 fully enforced; no secrets in logs/reports; signed release optional.
- Offline-first: no internet dependency.
- Portability: single exe + optional `data/` folder; settings JSON human-readable (except DPAPI blobs).
- Documentation: `README.fa.md`, `README.en.md`, `PERMISSIONS.md`, `RULES.md`, `SQL_CATALOG.md`, `ARCHITECTURE.md`, user guide with screenshots.

---

## 18. Phased delivery & acceptance criteria

| Phase | Scope | Done when |
|---|---|---|
| **0 – Foundation & safety** | Solution skeleton, DI, logging, localization scaffold (fa/en + RTL), site/node profiles, DPAPI secrets, **RSM.Safety** (SqlGuard, catalog loader, BannedSymbols, Redis whitelist), Capability Matrix, arch tests | Safety tests pass (malicious corpus rejected, catalog read-only), analyzer blocks a deliberately added `Process.Start`, connection test shows capability matrix |
| **1 – SQL & DB health + report v1** | §7 items 1–2, 12–17 (inventory, config compliance, memory, tempdb, IO, errorlog, agent, security), §8 database module, rules engine with ستاد فنی baseline, HTML report | Snapshot against a test instance produces correct compliance table & findings; report renders in fa and en |
| **2 – Live SQL + Windows** | §7 items 3–11 (counters, CPU ring buffer, waits, requests, blocking, deadlocks, transactions, top queries, plan warnings), §9 Windows module (local + remote CIM), live dashboard & charts | Live view stable for 1 h with < 2 % server CPU overhead; blocking demo (two sessions) shows correct chain; deadlock demo parsed |
| **3 – IIS / ARR / services / events** | §10 complete, services & event-log KB, unified error timeline (§13) | Live requests visible on a slow test endpoint; IIS log percentiles verified against a known log sample; ARR farm/rewrite checks verified on a lab ARR |
| **4 – Redis, SSRS, CodeMeter, RabbitMQ, topology, .NET counters** | §11, topology discovery & map, EventPipe runtime counters, query packs | Topology map correct for a 3-node lab; GC/threadpool counters shown for an ASP.NET Core test app |
| **5 – History, baseline, slowness wizard, PDF, bundles, polish** | §12, §15, PDF, bundle import/merge, CLI, docs, single-file publish | Wizard identifies injected causes in lab scenarios (CPU burn, blocking, disk latency, memory pressure, IIS queue); release exe runs on a clean Windows Server 2019/2022 |

Testing requirements (all phases): unit tests for every parser (IIS W3C, HTTPERR, errorlog, deadlock XML, showplan XML, RESP/INFO, ExecutionLog), rules engine with JSON fixtures, snapshot (Verify) tests for HTML report in fa and en, SqlGuard & catalog tests, arch tests, integration tests behind an env-var connection string.

---

## 19. Definition of done (checklist)

- [ ] No code path can modify SQL Server, Windows, IIS, services, registry, files (outside RSM data folder), Redis or RabbitMQ — enforced by analyzer + SqlGuard + tests.
- [ ] Every SQL query is in the catalog with min version & permission, and is version-gated.
- [ ] Works with SQL Authentication using only the least-privilege login from `PERMISSIONS.md`; missing permissions degrade gracefully.
- [ ] Capability Matrix explains every unavailable feature.
- [ ] Live dashboard, slowness wizard, error timeline, snapshot report (HTML/PDF/JSON), history/baseline and multi-node all working.
- [ ] Full Persian RTL and English UI & reports; every finding has fa/en explanation, evidence, text-only recommendation and source reference (ستاد فنی doc vs general guidance).
- [ ] Measured overhead < 2 % CPU on the monitored server in default live mode.
- [ ] Single-file self-contained exe; documentation complete.

**First response expected from you:** the plan-mode deliverables from §0.1 (ARCHITECTURE.md outline, SQL_CATALOG.md draft list, RULES.md draft, phase plan) and any questions where my requirements are ambiguous. Do not write production code before I approve.
