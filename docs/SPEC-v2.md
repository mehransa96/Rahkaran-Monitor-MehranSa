# Final Prompt (v2) — Rahkaran Server Monitor: Rahkaran Slowness & Error Intelligence
# پرامپت نهایی (نسخه ۲) — پایشگر سرور راهکاران

> **راهنمای استفاده (فارسی):** این پرامپت تکمیلی است و روی همان مخزن کدی اجرا می‌شود که با پرامپت نسخه ۱ ساخته‌اید. محدودیت‌های «فقط‌خواندنی» (بخش ۲) **عیناً همان بخش ۲ پرامپت نسخه ۱ است** و هیچ قانونی به آن اضافه یا از آن کم نشده است. در ریشه پروژه Claude Code را باز کنید و بگویید: «فایل PROMPT-v2.md را کامل بخوان و از بخش 0 شروع کن». Claude Code اول کد فعلی را بررسی می‌کند (رعایت بخش ۲ + کمبودها)، گزارش می‌دهد و منتظر تأیید شما می‌ماند؛ بعد فازهای A تا F را یکی‌یکی اجرا می‌کند. خروجی‌های کاربر (UI، گزارش، راهنماها) دوزبانه فارسی/انگلیسی می‌مانند.

---

## 0. Context and how to work

The repository contains **RSM (Rahkaran Server Monitor)**, a .NET 8 WPF single-file desktop tool already built from *PROMPT v1* (phases 0–5: safety layer, SQL/DB health, live SQL + Windows, IIS/ARR/services/events, Redis/SSRS/CodeMeter/RabbitMQ/topology/.NET counters, history/baseline/slowness wizard/PDF/bundles). It is used by the Rahkaran (راهکاران, Systemgroup / همکاران سیستم) **technical support team** to find the cause of **slowness** and **errors** on customer servers.

This prompt is the **final specialization pass**. Goals:
1. Verify the existing code fully complies with the read-only rules of **§2 (identical to PROMPT v1 §2)** and close gaps vs PROMPT v1 (Phase A).
2. Make RSM **Rahkaran-specific**: knowledge pack, symptom playbooks, Rahkaran-aware correlation, update/reset awareness, team-ready reports (Phases B–E).
3. Ship a release-quality build with a Persian user guide for the team (Phase F).

Working rules:
- First read `CLAUDE.md`, `ARCHITECTURE.md`, `SQL_CATALOG.md`, `RULES.md`, `PERMISSIONS.md`, and the code. Do **not** rewrite working parts; extend them.
- **§2 is the same rule set as PROMPT v1 §2. Do not add new restrictions and do not relax any.** Every new feature in this prompt must be implemented within §2 exactly as it is.
- Start in **plan mode**. Your first deliverables are `READONLY_AUDIT.md` and `GAP_ANALYSIS.md` (Phase A) plus a plan for Phases A–F. **Stop and wait for my approval.**
- One phase at a time; at the end of each: build green, all tests green, demo notes, update docs and `CLAUDE.md`, git commit. Wait for my confirmation before the next phase.
- **Never invent Rahkaran internals** (table names, schema→module names, service names, log paths, DLL names, URL patterns). Anything not stated in §1 must be a **configurable mapping** filled in by the team, with a discovery screen showing real values found on the server. Mark such items "unverified" until configured.
- Any threshold or recommendation not taken from the Systemgroup documents in §1 must be labeled **"general guidance"** in rule metadata, UI and reports.

---

## 1. Rahkaran domain knowledge (from Systemgroup / ستاد فنی documents) — use for rules & playbooks

**Architecture:** SQL Server (2022 + latest CU) main DB in **Full** recovery with log backups; **ReportServer/ReportServerTempDB** in **Simple**; SSRS for reports (customer-specific reports in separate folders). IIS-hosted app on **.NET Core** since v19 "Pioneer"/v20 (ASP.NET Core Module, in-process `w3wp` or out-of-process `dotnet`), config in `web.config`. **NUMA servers**: one site per node (**AuxWebsite1, AuxWebsite2, …**) behind **AppRouterWebsite** (internal ARR); NUMA app-pool settings depend on CPU generation/cores. **External ARR** server with farms like `RahkaranAppFarm_<City>` (setup tool) + manual farms for **ES-CS** (خدمات الکترونیک کارمند/مشتری) and **Mobile**, URL Rewrite rules; after each Rahkaran update the app path segment ("X") changes and rewrite rules must be updated. **Redis** (6379 default; may sit on an app server). **CodeMeter** licensing (LockBoxSerial + GUID; dongle may be on another server via network server). **Workflow (گردش کار)** needs **RabbitMQ + Erlang**. **Attendance (تردد)** has its own Windows service. Setup tool `SgSetupTool.exe`; split App/Data installs run the tool on both servers with the identical package. Topologies: single Data-App; App + Data/Report; large multi-server with external ARR, NUMA app servers and separate Report/Data; "ابرآمد" cloud customers (only port 80).

**ستاد فنی baseline (SQL Server Tuning V.3):** 64-bit OS; Power plan High Performance; CPU not below base frequency; Processor scheduling = Background services; pagefile min = RAM, max ≤ 1.5 × RAM; SQL volumes 64 KB allocation unit; battery-backed RAID cache; Data/Log/TempDB separate from OS/pagefile when possible; Min server memory 15 %, Max 65 % of RAM (app + SQL on one box); Priority boost off; Max worker threads 0; Lightweight pooling off; Backup compression on; MAXDOP 4; Cost threshold from ستاد فنی tool (warn > 100); Optimize for ad hoc on; log initial 1024 MB (large: 10240 MB); data autogrowth in MB, multiple of 1024; free space ≥ 2 × DB; Page verify CHECKSUM; don't touch Automatic/Cursor/Miscellaneous DB options; Full daily 22:00, Log every 2 h in work hours, CHECKDB weekly, nightly index/stats, cleanup > 1 week; large DBs daily Diff + weekly Full; extra backups COPY_ONLY; never schedule shrink of DB/data files; never mix ستاد فنی tool jobs with a manual maintenance plan; slow network backup targets lengthen jobs and slow Rahkaran; SQL Agent Automatic & running; sa must have a password; antivirus exclusions for SQL process & data files.

**Support KB knowledge:**
- **Log growth:** interpret `log_reuse_wait_desc` (NOTHING, CHECKPOINT, LOG_BACKUP, ACTIVE_BACKUP_OR_RESTORE, ACTIVE_TRANSACTION — RSM shows open transactions from DMVs, DATABASE_MIRRORING, **REPLICATION → in Rahkaran almost always CDC: capture job missing/disabled/failing**, AVAILABILITY_REPLICA, OLDEST_PAGE, XTP_CHECKPOINT). Switching Rahkaran to Simple recovery is forbidden without ستاد فنی (recommendation text).
- **Memory dump KB:** high RAM / **GC Wait** in Rahkaran and **StackOverflow** crashes are diagnosed with ProcDump full dumps of `w3wp.exe` (`procdump -ma <PID>`, `-m <MB>` threshold, `-i -ma` for crash); the process is suspended during the dump → coordinate downtime or remove the server from ARR first; dump goes to تیم تولید. RSM only **recommends** this in text, showing the right PID and pool name.
- **NUMA + ARR reset order (KB):** on ARR: farm servers "Make Server Unavailable Gracefully" then "Offline" (all servers of a farm, never a subset; ES-CS and Mobile farms too) → on each NUMA server stop AppRouterWebsite, AuxWebsite1, AuxWebsite2 pools then IIS; normal servers AuxWebsite1 then IIS → restart Redis + FLUSHDB → bring servers up one by one (IIS → AuxWebsite1 → AuxWebsite2 → browse to login page → AppRouterWebsite) → on ARR "Bring Server Online" + "Available". RSM never performs any of this; it **validates the resulting state** (playbook P11).
- **v20 upgrade KB:** check server specs, CodeMeter updated, RabbitMQ/Erlang if workflow, lock updated only on version change, IIS + Redis (+ attendance service) stopped during update, backups of DBs/RDLs/web.config, ابرآمد port-80 specifics.
- **SGProactiveMonitor** (Systemgroup agent) collects: last backup date, DB size, hardware summary, data-drive free space, OS/SQL versions, Rahkaran user count & version, fragmentation of key indexes — RSM should cover all of these too.

---

## 2. NON-NEGOTIABLE: read-only by construction (verbatim from PROMPT v1 §2 — unchanged, nothing added or removed)

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

---

## 3. Phase A — Compliance check with §2 & gap closure (FIRST)

1. **`READONLY_AUDIT.md`**: inventory every outbound operation in the codebase (SQL catalog keys, WMI classes, counters, file paths, registry keys, IIS APIs, Redis commands, HTTP calls, process APIs, EventPipe usage), each mapped to the §2 rule that allows it, with file:line. List anything that does not comply with §2.1–§2.6 exactly as written.
2. **`GAP_ANALYSIS.md`**: every PROMPT v1 requirement → Implemented / Partial / Missing / Buggy, with evidence. Include known defects, crashes, slow screens, and version-gating gaps (SQL 2016–2022, IIS 8.5–10, Windows Server 2016–2022).
3. After approval: fix every non-compliance with §2 and close high-priority gaps.
4. **Transparency features (within §2, no new rules):**
   - **Operations Audit log** («شفافیت عملیات»): RSM records locally everything it read from each monitored server (catalog key, WMI class, counter set, file path, IIS API, Redis command, HTTP GET) with time, target, duration and result. Shown in a UI panel and attachable as an appendix to exported reports.
   - **Read-only self-test screen** («خودآزمایی فقط‌خواندنی»): runs the existing SqlGuard/catalog checks on the loaded queries and query packs, shows the effective permissions of the SQL login (and the existing sysadmin info banner of §2.4), and summarizes the session's audit log.
5. **Done when:** all §2.2 enforcement tests green; audit log and self-test work; a full snapshot on a lab server with the least-privilege login of §2.4 shows only read operations.

---

## 4. Phase B — Rahkaran Knowledge Pack (rules + explanations)

- Convert §1 into data-driven rules (`rules/rahkaran/*.json`): id, layer, condition, thresholds, severity, `title/explanation/recommendation` in fa & en, `responsibleRole` (DBA / IT / ستاد فنی / تیم تولید), `source` (e.g. "SQL Server Tuning V.3", "KB Log Growth 1403/12/28", "KB Memory Dump 1404/10/14", "KB ARR/NUMA reset", "general guidance"), `verified` flag.
- **Rahkaran health index** («شاخص سلامت راهکاران», 0–100) per site and per layer, with transparent weights in `RULES.md`.
- **Daily check («چک روزانه ۵ دقیقه‌ای»)**: one screen for the morning check — services, backups (last Full/Log vs policy), failed Agent jobs, CHECKDB age, disk/log free space and time-to-full, SQL errorlog/IO errors since yesterday, IIS 5xx/503 since yesterday, app-pool crashes/recycles, ARR farm health, Redis restarts/evictions, CodeMeter/RabbitMQ/attendance service status, pending reboot, certificate expiry.
- **Coverage of SGProactiveMonitor fields** (last backup, DB size, hardware, data-drive free space, OS/SQL version, Rahkaran version & user count via query packs of PROMPT v1 §8.7, key index fragmentation on demand per §2.6).

---

## 5. Phase C — Rahkaran slowness forensics

1. **Flight recorder:** keep the last 30 min (configurable) of live metrics, waits, active requests, blocking chains, IIS live requests and counters in memory. Button/hotkey **«ثبت لحظه کندی» (Mark slowness)** saves −15/+5 min around the mark to SQLite with a user note (who reported, which form/page, users affected), so intermittent slowness can be analyzed afterwards.
2. **Blocking & long-request history (sampled):** every N seconds (default 10) record blocking chains (head blocker, statement, wait resource, duration, host, program) and long-running requests (> threshold) with their waits into SQLite → "Blocking history" and "Long requests history" timelines. Deadlocks still come from `system_health` as in PROMPT v1.
3. **Query regression detection:** track `query_hash`/`query_plan_hash` stats over time; flag avg duration/CPU/reads jump vs baseline, **plan change** for the same query_hash, and parameter-sensitivity hints (high duration variance for the same query_hash). Use Query Store data when enabled.
4. **Rahkaran module attribution (configurable):**
   - *SQL side:* discovery screen lists schemas, top objects and distinct `program_name/host_name/login_name/context_info` patterns actually seen; the team maps **schema/object prefix → Rahkaran module (Persian name)** and **session pattern → app server/site**. Top queries, waits, blocking and IO can then be grouped **by module** ("کندی عمدتاً از ماژول … است").
   - *Web side:* map **URL patterns → module/form** for IIS log analysis (p50/p95/p99 by module/form, slowest forms).
   - *Correlation:* align slow IIS requests (time, app server) with SQL requests from the same host in the same window; show "probable SQL statements behind this slow page" with a confidence level (heuristic, labeled).
5. **USE/RED analysis:** for every resource (CPU, memory, disk, network, SQL workers, IIS queue, threadpool, Redis) show Utilization / Saturation / Errors; for IIS sites and ARR farms show Rate / Errors / Duration.
6. **Capacity & time-to-full:** projections for data/log files, volumes, TempDB and backups; log growth rate vs log backup interval.
7. **Business calendar for baselines:** Jalali calendar with Iranian weekends/holidays (editable) and business events (month-end payroll/حقوق, year-end closing, custom events) so baselines compare like with like; mark Rahkaran update events automatically (version/path change detection) and manually.
8. **Root-cause engine v2:** evidence-weighted ranking across layers using the playbooks of §6; each cause lists evidence (values + timestamps + chart links), confidence, responsible role and text-only next steps with KB reference.

---

## 6. Symptom playbooks («راهنمای عیب‌یابی بر اساس علامت») — part of Phase C

Each playbook is a guided screen: the user selects the symptom; RSM runs the relevant checks (live sample + history) within §2, evaluates the decision tree, and outputs ranked findings + a ticket-ready Persian summary. Recommendations are text only, as in §2.1.

| # | Symptom (fa) | What RSM checks | Typical text recommendation / reference |
|---|---|---|---|
| P1 | کندی عمومی همه کاربران (ساعات اوج) | Host CPU split (SQL vs w3wp vs others, ring buffer), signal-wait %, CPU throttling/power plan, PLE & memory grants pending, Available MB & paging, SQL max memory vs IIS on same box, disk latency per file/volume, blocking chains, IIS queue/threadpool/GC, ARR distribution & unhealthy nodes, Redis latency/evictions, network saturation | Tuning V.3 baseline items; DBA/IT actions in text |
| P2 | یک فرم یا گزارش خاص کند است | IIS logs by URL/module (time-taken), live IIS requests for that URL, SQL requests from that host in the same window, plan warnings (implicit conversion, scans, lookups, spills, missing index), stale statistics, regression/plan change, SSRS ExecutionLog (data vs processing vs rendering) | Send query/plan evidence to ستاد فنی/تیم تولید; no index changes on vendor schema without ستاد فنی |
| P3 | خطای 503 / صفحه سفید / قطع ناگهانی | App-pool state & recent recycles, WAS 5002/5009/5010/5011/5117, rapid-fail, Application Error 1000 (exception code `0xc00000fd` = StackOverflow), .NET Runtime 1026, WER 1001, ANCM 502.5/500.30–500.37 & stdout logs, HTTPERR QueueFull/AppOffline/Connection_Dropped, ARR server offline/unhealthy, CodeMeter service/errors | Memory Dump KB (ProcDump `-i -ma` for crash); coordinate downtime / remove from ARR first |
| P4 | مصرف بالای RAM / GC Wait | w3wp private bytes trend per pool, % time in GC, gen2 rate, LOH, allocation rate, threadpool queue; SQL memory clerks; total RAM vs SQL max memory | Memory Dump KB (`procdump -ma -m <MB> <PID>`), show PID + pool name |
| P5 | لاگ دیتابیس بزرگ / دیسک پر | log_reuse_wait_desc with KB explanation, last log backup, open/long transactions, CDC enabled + capture job state, recovery model, VLF count, time-to-full | Log-growth KB; no switch to Simple without ستاد فنی |
| P6 | مشکل بعد از آپدیت / نصب Release | Version consistency across app servers, URL Rewrite path "X" vs deployed path, ARR farms all available/online, web.config drift, ASP.NET Core Module present in IIS global modules, CodeMeter runtime version, RabbitMQ/Erlang present if workflow, before/after comparison (waits, top queries, IIS p95, errors) | v20 upgrade KB, ARR KB |
| P7 | گزارش‌های SSRS کند یا خطادار | ExecutionLog (time split, failures, top reports/users), ReportServer DB recovery Simple, SSRS service, SQL waits during report runs | Data-retrieval-heavy → SQL side; rendering-heavy → report design |
| P8 | بکاپ / نگهداری مشکل دارد | Backup recency vs policy, LSN chain gaps, failed jobs, ستاد فنی tool + manual plan both present, shrink steps, UNC backup targets & duration, CHECKDB age, suspect_pages | Tuning V.3 maintenance section |
| P9 | گردش کار گیر کرده | RabbitMQ/Erlang services, queue depth/unacked growth, queues with 0 consumers, node alarms | Text for workflow owner |
| P10 | تردد کار نمی‌کند | Attendance service (configured name) status/restarts, related event-log errors, DB connectivity from that host | Text |
| P11 | اعتبارسنجی بعد از ریست NUMA/ARR | Final-state check per reset KB: all farm servers (main, ES-CS, Mobile) Online & Available, on each NUMA server AuxWebsite1/2 + AppRouterWebsite started and serving, IIS started, Redis uptime shows restart, no 503 burst after reset | ARR/NUMA reset KB (RSM only validates) |
| P12 | کندی ورود / قطع Session | Redis reachability/rejected connections/evictions/restarts, SQL 18456 bursts, IIS 401/403/5xx on login URLs (configured pattern), ARR node health | Text |

Each playbook result includes: symptom, time window, nodes, ranked findings, evidence table, charts on one timeline, responsible role, KB/source references, and «متن آماده تیکت» (copy-ready Persian summary).

---

## 7. Phase D — Error intelligence

1. **Error signatures:** normalize and deduplicate errors from all sources (Windows events, SQL errorlog, Agent, deadlocks, IIS 5xx, HTTPERR, ANCM/stdout, Rahkaran app logs, service crashes, Redis restarts, backup failures) into signatures with first seen / last seen / count / trend / affected nodes; flag **"new since last report"** and **"spiking"**.
2. **Rahkaran application logs (configurable):** the team configures log folders and file patterns; RSM tails them read-only (as files are read in PROMPT v1) with configurable regex rules (severity, module, Persian explanation). Ship no guessed paths.
3. **Built-in error knowledge base** (fa/en, editable JSON): Windows/IIS/WAS/ANCM/SQL error IDs from PROMPT v1 plus Rahkaran KB items; each entry → explanation, likely cause, responsible role, reference.
4. **Update & restart awareness on the timeline:** detected version changes, app-pool recycles, service restarts, Redis restarts, SQL restarts, reboots — shown as markers on every chart and on the error timeline.

---

## 8. Phase E — Team reporting & in-app alerts

1. **Report templates** (HTML/PDF/JSON bundle, fa/en, with the masking option of §2.5):
   - «گزارش سلامت دوره‌ای» (periodic health check, compliance vs ستاد فنی baseline, health-index trend),
   - «گزارش حادثه / کندی» (from a playbook or a slowness mark: timeline, evidence, root-cause ranking),
   - «گزارش قبل/بعد از آپدیت»,
   - «بسته ارسال به ستاد فنی» (standard sections + raw bundle + capability matrix + optional Operations Audit appendix).
2. **Ticket-ready summary:** template-based Persian text with customer/site, time window, symptom, top 3 findings with evidence, recommended actions, attachments list — one-click copy.
3. **In-app alerts (desktop notifications while RSM is running):** blocking > N s, head-blocker age, IIS queue > 0 sustained, 5xx/503 spike, app-pool crash/recycle, PLE drop vs baseline, memory grants pending, disk latency, log space > 80 % / time-to-full < X h, backup overdue, service stopped, Redis evictions/restart, ARR node unhealthy. Configurable thresholds, snooze, alert history.

---

## 9. Phase F — Quality, performance proof, documentation, release

1. **Overhead proof:** measure RSM's own impact (its SQL sessions' CPU/reads from DMVs, host CPU) in live and flight-recorder modes against the §2.6 target (< 2 % CPU); show it in the status bar and in `PERFORMANCE.md`.
2. **Lab scenarios** (separate `lab/` folder and solution, never shipped, not part of the RSM query catalog): scripts a tester runs manually in a lab to create blocking, CPU burn, IO pressure, memory pressure, log growth without log backup, IIS queue saturation, app-pool crash → verify each playbook detects the injected cause. Document in `LAB_RESULTS.md`.
3. **Tests:** parsers, rules (fixtures per playbook), Verify snapshot tests for every report template in fa and en, masking tests, audit-log tests, version-gating tests (2016/2017/2019/2022 fixtures), plus the existing §2.2 safety tests.
4. **Docs for the team (Persian first, English second):** `docs/fa/راهنمای-کاربر.md` (connecting with the least-privilege login, daily check, each playbook step by step with screenshots, reading the reports, sending to ستاد فنی), `docs/fa/سوالات-متداول.md`, updated `PERMISSIONS.md`, `RULES.md`, `SQL_CATALOG.md`, `READONLY_AUDIT.md`, release notes.
5. **Release:** versioned single-file self-contained exe, About screen with version/build/commit and the read-only statement, SHA-256 checksum published with the build.

---

## 10. Definition of Done

- [ ] `READONLY_AUDIT.md` confirms full compliance with §2 (unchanged from PROMPT v1); all §2.2 tests green.
- [ ] Operations Audit panel and Self-Test screen work.
- [ ] Rahkaran Knowledge Pack rules, health index, daily check and all 12 playbooks implemented with fa/en texts, responsible roles and source references.
- [ ] Flight recorder + slowness mark, blocking/long-request history, regression detection, module attribution (configurable), USE/RED, time-to-full, business calendar, update/restart markers.
- [ ] Error signatures, app-log tailing (configurable), error KB, in-app alerts.
- [ ] Report templates (health, incident, before/after, ستاد فنی bundle) and ticket-ready Persian summary.
- [ ] Measured overhead within §2.6; lab scenarios detected; Persian user guide complete; release build with checksum.
- [ ] Nothing Rahkaran-specific was guessed; all product-specific mappings are configurable and marked "unverified" until configured.

**Your first response:** `READONLY_AUDIT.md`, `GAP_ANALYSIS.md`, and the Phase A–F plan with estimates and questions. Do not change code before I approve.
