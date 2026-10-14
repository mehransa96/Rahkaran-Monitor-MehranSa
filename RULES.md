# RULES — initial health-check rules

Status: **approved** (rules ship as JSON from Phase 1; P0 delivers the rules engine). Each row becomes one JSON file in `rules/` (format in `ARCHITECTURE.md`
§9) with fa/en title, explanation and text-only recommendation, the evidence fact keys, and a fixture test.
Thresholds are parameters in the JSON so the team can tune them without a rebuild.

## Source legend

| Code | Source | UI label |
|---|---|---|
| **TV3** | ستاد فنی — "SQL Server Tuning V.3" (section names per spec §3; exact section numbers to be filled in — decision Q5) | "ستاد فنی baseline" |
| **KB-LOG** | Systemgroup KB — transaction-log growth (`log_reuse_wait_desc`, CDC capture job stopped/deleted) — "Log growth KB 1403/12/28" | "Systemgroup KB" |
| **KB-BKP** | Systemgroup KB — slow backups over network shares; maintenance plan + «ابزار پایگاه داده» conflicts / broken chain | "Systemgroup KB" |
| **KB-GC** | Systemgroup KB — high RAM / GC wait / StackOverflow in Rahkaran process (ProcDump guidance) | "Systemgroup KB" |
| **KB-ARR** | Systemgroup KB — external ARR farms, URL Rewrite, app path segment changes after update | "Systemgroup KB" |
| **ARCH** | Rahkaran architecture facts stated in spec §3 (NUMA Aux sites, Redis, CodeMeter, RabbitMQ, SSRS DB recovery) | "Rahkaran architecture" |
| **HPE** | HPE server preparation guide (informational) | "HPE guide" |
| **GEN** | General industry guidance (Microsoft docs / common DBA practice) | **"general guidance — confirm with ستاد فنی"** |

Flags: **⚠U** = depends on a product-specific name/pattern not stated in the docs ⇒ `verified:false`,
driven by a setting, shown with an "unverified" chip. Severities: **C**ritical, **W**arning, **I**nfo.
Shared-box = the node has both Data and App roles (SQL and IIS on one machine).

---

## 1. Windows OS & hardware (§9) — P2

| ID | Check (fires when…) | Sev | Source |
|---|---|---|---|
| OS-001 | Active power plan is not *High Performance* (or *Ultimate Performance*) | W | TV3 (OS) |
| OS-002 | CPU throttling: `% Processor Performance` < 95 % or current clock < max clock for ≥ 3 samples while CPU busy > 20 % | W | TV3 (OS) |
| OS-003 | Processor scheduling is not *Background services* (`Win32PrioritySeparation` decoded) | I | TV3 (OS) |
| OS-004 | Pagefile: system-managed, or min < RAM, or max > 1.5 × RAM | I | TV3 (OS) |
| OS-005 | OS is not 64-bit | C | TV3 (OS) |
| OS-006 | Available memory < 10 % (W) / < 5 % or < 2 GB (C), sustained 60 s | W/C | GEN |
| OS-007 | Committed bytes > 90 % of commit limit | W | GEN |
| OS-008 | Processor queue length > 2 × logical CPUs sustained 60 s | W | GEN |
| OS-009 | Pending reboot (CBS / WindowsUpdate / PendingFileRenameOperations) | I | GEN |
| OS-010 | Unexpected shutdown in last 7 days (Kernel-Power 41, EventLog 6008) | W | GEN |
| OS-011 | Antivirus real-time on and SQL data/log paths or `sqlservr.exe` not in Defender exclusions (when readable) | W | TV3 (Other) |
| OS-012 | Hardware is HPE → firmware/SPP reminder | I | HPE |
| OS-013 | HPE physical host with Hyper-Threading off (logical = physical cores) | I | HPE |
| OS-014 | Physical host, no hypervisor role, `VirtualizationFirmwareEnabled = true` | I | HPE |
| OS-015 | NIC throughput > 70 % of link speed sustained, or output queue > 2, or packet errors/discards increasing | W | GEN |
| OS-016 | TCP TimeWait > 50 % of dynamic port range / ephemeral-port pressure | W | GEN |
| OS-017 | Time zone *Iran Standard Time* still has a DST rule after 2022 (outdated tz data → log times off by 1 h) | I | GEN |
| OS-018 | Context switches/sec > 15 000 per core sustained | I | GEN |

## 2. Storage (§9.5, §7.14, TV3 Storage) — P1 (config) / P2 (live)

| ID | Check | Sev | Source |
|---|---|---|---|
| STO-001 | Volume hosting SQL data/log/tempdb has allocation unit ≠ 64 KB | W | TV3 (Storage) |
| STO-002 | SQL data, log or tempdb on the OS/pagefile volume | W | TV3 (Storage) |
| STO-003 | Data and log files of the Rahkaran DB on the same volume | I | TV3 (Storage, "when possible") |
| STO-004 | Volume free < 15 % (W) / < 5 % (C) | W/C | GEN |
| STO-005 | Free space on the data volume < 2 × Rahkaran DB size | W | TV3 (Database) |
| STO-006 | Data volume `Avg. Disk sec/Read` > 20 ms (W) / > 50 ms (C) sustained | W/C | GEN |
| STO-007 | Log volume `Avg. Disk sec/Write` > 5 ms (W) / > 15 ms (C) sustained | W/C | GEN |
| STO-008 | Disk/Ntfs events (Disk 7/11/51/153, Ntfs 55/98) in last 7 days | C | GEN |
| STO-009 | Growth forecast: volume full in < 30 days (backup-size trend + free space) | W | GEN |

## 3. SQL Server instance (§7) 

### 3.1 Version & configuration — P1

| ID | Check | Sev | Source |
|---|---|---|---|
| SQL-VER-001 | Major version below SQL Server 2022 | I | TV3 |
| SQL-VER-002 | Not on the latest CU listed in `known-builds.json` for its major version | W | TV3 |
| SQL-VER-003 | Major version out of Microsoft extended support | W | GEN |
| SQL-MEM-001 | `max server memory` left at default (2147483647) | C | TV3 (Instance) |
| SQL-MEM-002 | Shared-box: `max server memory` outside 65 % ± 5 % of RAM | W | TV3 (Instance) |
| SQL-MEM-003 | Shared-box: `min server memory` outside 15 % ± 5 % of RAM | I | TV3 (Instance) |
| SQL-MEM-004 | Dedicated DB server: `max server memory` leaves < max(4 GB, 10 % RAM) to the OS (decision Q4) | W | GEN |
| SQL-CFG-001 | `priority boost` = 1 | W | TV3 |
| SQL-CFG-002 | `max worker threads` ≠ 0 | W | TV3 |
| SQL-CFG-003 | `lightweight pooling` = 1 | W | TV3 |
| SQL-CFG-004 | `backup compression default` = 0 | W | TV3 |
| SQL-CFG-005 | `max degree of parallelism` ≠ 4 | W | TV3 |
| SQL-CFG-006 | `cost threshold for parallelism` > 100 (W); = 5 default (I: "verify the value set by the ستاد فنی tool") | W/I | TV3 |
| SQL-CFG-007 | `optimize for ad hoc workloads` = 0 | W | TV3 |
| SQL-CFG-008 | `value` ≠ `value_in_use` (pending RECONFIGURE/restart) | I | GEN |
| SQL-CFG-009 | `xp_cmdshell` enabled | W | GEN |
| SQL-CFG-010 | `Ole Automation Procedures` enabled | W | GEN |
| SQL-CFG-011 | `clr enabled` = 1 (info only; Rahkaran's use of CLR unknown) ⚠U | I | GEN |
| SQL-CFG-012 | `remote admin connections` = 0 (DAC not reachable remotely for emergencies) | I | GEN |
| SQL-CFG-013 | `cross db ownership chaining` = 1 | W | GEN |
| SQL-CFG-014 | Instant File Initialization disabled | I | GEN |
| SQL-CFG-015 | Offline schedulers (edition/affinity limits CPU use) | C | GEN |

### 3.2 Error log — P1

| ID | Check | Sev | Source |
|---|---|---|---|
| SQL-ERR-001 | Error 823 / 824 / 825 in the window | C | GEN |
| SQL-ERR-002 | Error 833 (I/O > 15 s) | W | GEN |
| SQL-ERR-003 | 17883 / 17884 / non-yielding scheduler | C | GEN |
| SQL-ERR-004 | 701 / 802 (memory) | C | GEN |
| SQL-ERR-005 | 9002 (log full) | C | KB-LOG |
| SQL-ERR-006 | 18456 failed logins > 20 / h (grouped by state, client IP) | W | GEN |
| SQL-ERR-007 | 3041 backup failure | C | KB-BKP |
| SQL-ERR-008 | Stack dump / `sys.dm_server_memory_dumps` new entries | C | GEN |
| SQL-ERR-009 | "cachestore flush" messages (plan cache cleared) | I | GEN |
| SQL-ERR-010 | Error log unreadable (no permission and no file access) — shown in Capability Matrix | I | — |

### 3.3 SQL Agent & maintenance — P1

| ID | Check | Sev | Source |
|---|---|---|---|
| SQL-AGT-001 | Agent service not *Automatic* or not running | C | TV3 (Maintenance) |
| SQL-AGT-002 | Job failures in last 7 days | W | GEN |
| SQL-AGT-003 | Job step contains `SHRINKDATABASE`/`SHRINKFILE` on a data file, or DB maintenance plan has a shrink task | W | TV3 (Maintenance) |
| SQL-AGT-004 | ستاد فنی tool jobs **and** a manual maintenance plan both take backups of the same DB ⚠U (job name patterns) | W | TV3 + KB-BKP |
| SQL-AGT-005 | Backup destination on a UNC path (slow-share risk; combine with throughput) | I | KB-BKP |
| SQL-AGT-006 | CDC-enabled DB without CDC capture/cleanup jobs, or capture job not running | C | KB-LOG |
| SQL-AGT-007 | Running job duration > 2 × its historical p95 | I | GEN |
| SQL-AGT-008 | No index/statistics maintenance job ran in last 48 h ⚠U (patterns) | I | TV3 (Maintenance) |
| SQL-AGT-009 | Full backup schedule not in the evening window (default 22:00 ± 2 h) | I | TV3 (Maintenance) |

### 3.4 Security posture — P1

| ID | Check | Sev | Source |
|---|---|---|---|
| SQL-SEC-001 | `sa` has an empty password (only if verifiable) | C | TV3 (Maintenance: "sa must have a password") |
| SQL-SEC-002 | `sa` enabled and not renamed | I | GEN |
| SQL-SEC-003 | SQL logins with `is_policy_checked = 0` | W | GEN |
| SQL-SEC-004 | More than 3 sysadmin members (excluding built-in service SIDs) | I | GEN |
| SQL-SEC-005 | Orphaned users in user DBs | I | GEN |
| SQL-SEC-006 | `guest` has CONNECT in a user DB | W | GEN |
| SQL-SEC-007 | Unencrypted connections from remote hosts | I | GEN |
| SQL-SEC-008 | Linked servers present | I | GEN |
| SQL-SEC-009 | RSM connected with a sysadmin login (banner: "high-privilege account used; RSM stays read-only") | I | spec §2.4 |

### 3.5 Live performance — P2

| ID | Check | Sev | Source |
|---|---|---|---|
| SQL-CPU-001 | Signal waits > 25 % of total waits (interval) | W | GEN |
| SQL-CPU-002 | SQL CPU > 80 % for ≥ 5 min | W | GEN |
| SQL-CPU-003 | Non-SQL CPU > 30 % on a Data node (ring buffer) | W | GEN |
| SQL-CPU-004 | THREADPOOL waits in interval | C | GEN |
| SQL-MEM-005 | `process_physical_memory_low` / memory-low notifications | W | GEN |
| SQL-MEM-006 | Memory Grants Pending > 0 for ≥ 3 samples | W | GEN |
| SQL-MEM-007 | PLE < 50 % of its hour-of-week baseline for ≥ 3 samples (absolute fallback: PLE < 300 s per 4 GB buffer pool) | W | GEN |
| SQL-MEM-008 | Shared-box competition: OS available < 10 % while SQL total = target and w3wp private bytes rising | W | GEN |
| SQL-WAIT-001 | Dominant wait category over interval (PAGEIOLATCH, WRITELOG, LCK_M, RESOURCE_SEMAPHORE, CX*, ASYNC_NETWORK_IO, PAGELATCH-tempdb) — emits a category-specific finding with next steps | I/W | GEN |
| SQL-IO-001 | Data file read latency > 20 ms (W) / > 50 ms (C) over interval | W/C | GEN |
| SQL-IO-002 | Log file write latency > 5 ms (W) / > 15 ms (C) over interval | W/C | GEN |
| SQL-IO-003 | Pending I/O older than 15 s | W | GEN |
| SQL-TDB-001 | tempdb data files < min(logical CPUs, 8) | W | GEN |
| SQL-TDB-002 | tempdb data files not equal size / growth | W | GEN |
| SQL-TDB-003 | Allocation-page contention (PAGELATCH on 2:x:1/2/3) | W | GEN |
| SQL-TDB-004 | Version store > 20 % of tempdb or growing for 30 min | W | GEN |
| SQL-BLK-001 | Blocking chain head blocking > 30 s (W) / > 120 s (C) | W/C | GEN |
| SQL-BLK-002 | Sleeping session with open transaction > 5 min | W | GEN |
| SQL-DL-001 | Deadlocks: > 0 in 24 h (I), > 10 in 24 h (W) | I/W | GEN |
| SQL-TRN-001 | Transaction open > 10 min or > 1 GB log used | W | KB-LOG |
| SQL-PLC-001 | Single-use plans > 30 % of plan cache bytes | I | TV3 (ties to ad hoc) |
| SQL-QS-001 | Query Store regressed query (recent avg duration > 2 × baseline with plan change) | W | GEN |
| SQL-QS-002 | Forced plan failing (`force_failure_count` > 0) | W | GEN |
| SQL-HA-001 | AG replica not healthy / send or redo queue > 1 GB | W | GEN |

## 4. Databases (§8) — P1

"Rahkaran DB" = auto-detected or user-selected main DB(s). "RS DBs" = ReportServer + ReportServerTempDB.

| ID | Check | Sev | Source |
|---|---|---|---|
| DB-RM-001 | Rahkaran DB recovery model is SIMPLE ("change to Simple is forbidden without ستاد فنی") | C | TV3 (Database / Other) |
| DB-RM-002 | Rahkaran DB in FULL/BULK_LOGGED and no log backup for > 2 h inside working hours (W) / > 24 h (C) | W/C | TV3 (Maintenance) |
| DB-RM-003 | RS DBs not in SIMPLE | W | ARCH / TV3 |
| DB-ST-001 | Database state ≠ ONLINE (except intentionally OFFLINE, user-ack) | C | GEN |
| DB-OPT-001 | `page_verify_option` ≠ CHECKSUM | W | TV3 (Database) |
| DB-OPT-002 | `auto_close` on | W | GEN |
| DB-OPT-003 | `auto_shrink` on | W | TV3 (never shrink) |
| DB-OPT-004 | Auto create/update stats off — informational only ("do not touch Automatic options"; verify with ستاد فنی) | I | TV3 (Database) |
| DB-OPT-005 | Compatibility level lower than the server's native level ⚠U (Rahkaran may require a specific level) | I | GEN |
| DB-LOG-001 | `log_reuse_wait_desc` ∉ {NOTHING, CHECKPOINT} and log used > 50 % — reason-specific fa/en text (LOG_BACKUP, ACTIVE_TRANSACTION, ACTIVE_BACKUP_OR_RESTORE, DATABASE_MIRRORING, AVAILABILITY_REPLICA, OLDEST_PAGE, XTP_CHECKPOINT) | W | KB-LOG |
| DB-LOG-002 | `log_reuse_wait_desc` = REPLICATION on a CDC-enabled DB → "CDC capture job stopped/deleted" | C | KB-LOG |
| DB-LOG-003 | VLF count > 1000 | W | GEN |
| DB-LOG-004 | Log file initial size < 1024 MB (or < 10240 MB when DB is "large" — decision Q4) | I | TV3 (Database) |
| DB-FILE-001 | Any file with percent autogrowth | W | TV3 (Database) |
| DB-FILE-002 | Data-file growth not a multiple of 1024 MB | I | TV3 (Database) |
| DB-FILE-003 | Data/log file max size restricted | W | TV3 (Database: unrestricted) |
| DB-FILE-004 | File < 10 % free and autogrowth disabled | C | GEN |
| DB-BKP-001 | No FULL in 26 h (or for "large" DBs: no DIFF in 26 h **and** no FULL in 8 d) | C | TV3 (Maintenance) |
| DB-BKP-002 | Log backup interval > 2 h during working hours | W | TV3 (Maintenance) |
| DB-BKP-003 | Log backup LSN chain gap since last full | C | KB-BKP |
| DB-BKP-004 | More than one non-COPY_ONLY full per day from different devices/jobs (chain-break risk) | W | TV3 + KB-BKP |
| DB-BKP-005 | Backups without CHECKSUM | I | GEN |
| DB-BKP-006 | Backup throughput < 50 % of its 30-day median (often slow UNC share) | I | KB-BKP |
| DB-INT-001 | Last good CHECKDB > 7 d (W) / > 14 d (C) / unknown (I) | W/C/I | TV3 (Maintenance: weekly) |
| DB-INT-002 | Rows in `msdb.dbo.suspect_pages` | C | GEN |
| DB-STAT-001 | Stats with `modification_counter` > 20 % of rows and not updated for > 7 d (top-N) | I | GEN |
| DB-IDX-000 | Fixed caution on every index view: "Rahkaran schema is vendor-owned; do not create/drop indexes without ستاد فنی." | I | spec §7.11 |
| DB-IDX-001 | (on demand) Fragmentation > 30 % on indexes > 1000 pages | I | GEN |
| DB-CDC-001 | CDC log-scan errors or latency > 5 min | W | KB-LOG |

## 5. IIS & web tier (§10) — P3

| ID | Check | Sev | Source |
|---|---|---|---|
| IIS-LOG-001 | W3C logging for a Rahkaran site does not include `time-taken` | W | GEN |
| IIS-POOL-001 | App pool of a Rahkaran site stopped | C | GEN |
| IIS-POOL-002 | Rapid-fail / crash events (WAS 5002, 5009, 5010, 5011, 5013) in last 24 h | C | GEN / KB-GC |
| IIS-POOL-003 | Worker process younger than 1 h without a configured recycle at that time | I | GEN |
| IIS-POOL-004 | ASP.NET Core site with managed runtime ≠ "No Managed Code" | I | GEN |
| IIS-POOL-005 | Pool settings differ across App nodes of the same farm (drift) | W | GEN |
| IIS-POOL-006 | Idle timeout > 0 (cold starts after idle) | I | GEN |
| IIS-POOL-007 | Periodic recycle at default 1740 min (lands at random business hours) | I | GEN |
| IIS-POOL-008 | `enable32BitAppOnWin64` = true | I | GEN |
| IIS-POOL-009 | Web garden (`maxProcesses` > 1) | I | GEN |
| IIS-NUMA-001 | Count of AuxWebsiteN sites ≠ NUMA node count, or pools' NUMA affinity inconsistent with "one site per NUMA node" ⚠U (name pattern configurable) | I | ARCH |
| IIS-Q-001 | HTTP Service Request Queue `CurrentQueueSize` > 0 for 30 s (W); `RejectedRequests` increasing (C) | W/C | GEN |
| IIS-REQ-001 | Live requests executing > 30 s | W | GEN |
| IIS-ERR-001 | 5xx rate > 1 % (W) / > 5 % (C) in window | W/C | GEN |
| IIS-ERR-002 | 503 burst (queue full / pool stopped) | C | GEN |
| IIS-ERR-003 | ASP.NET Core Module failures (502.3, 502.5, 500.30–500.37) or ANCM events | C | GEN |
| IIS-ERR-004 | HTTPERR `QueueFull` / `AppOffline` / `Connection_Abandoned_By_ReqQueue` | C | GEN |
| IIS-LAT-001 | p95 latency of a URL stem > 1.5 × its baseline p95 | W | GEN |
| IIS-CERT-001 | Bound certificate expires < 30 d (W) / < 7 d or expired (C) | W/C | GEN |
| IIS-CFG-001 | `web.config` hosting model / processPath / env vars differ across App nodes | W | GEN |
| IIS-CFG-002 | `stdoutLogEnabled = true` left on (disk growth) | I | GEN |

## 6. ARR & URL Rewrite (§10.9–10) — P3

| ID | Check | Sev | Source |
|---|---|---|---|
| ARR-001 | Farm server unhealthy / offline | C | KB-ARR |
| ARR-002 | Only part of a farm's servers available | W | KB-ARR |
| ARR-003 | Measured distribution deviates > 25 % from configured weights (IIS logs or runtime counters) | W | GEN |
| ARR-004 | Rewrite rule's application path segment does not match the deployed application path ("X changes after update") | C | KB-ARR |
| ARR-005 | Rewrite rule points to a farm that does not exist | C | KB-ARR |
| ARR-006 | Health-test URL not configured on a Rahkaran farm | I | GEN |

## 7. Services & events (§9.7–9.8) — P3

| ID | Check | Sev | Source |
|---|---|---|---|
| SVC-001 | Watched service *Automatic* but stopped (SQL, Agent, SSRS, W3SVC, WAS, Redis, CodeMeter, RabbitMQ…) | C | GEN |
| SVC-002 | Unexpected terminations (SCM 7031/7034) in last 24 h | W | GEN |
| SVC-003 | CodeMeter Runtime not running on the Lock node | C | ARCH |
| SVC-004 | Workflow role set but RabbitMQ / epmd not running | C | ARCH |
| SVC-005 | Attendance service stopped ⚠U (service name pattern is a setting) | W | ARCH |
| SVC-006 | SQL Browser running while not needed (single default instance) | I | GEN |
| EVT-001 | Resource-Exhaustion-Detector 2004 | C | GEN |
| EVT-002 | `.NET Runtime` 1026 or `Application Error` 1000 for w3wp/dotnet | W | KB-GC |
| EVT-003 | Windows Error Reporting 1001 for w3wp (crash report) | W | KB-GC |
| EVT-004 | ASP.NET 1309 | W | GEN |
| EVT-005 | Schannel 36887 / 36874 | I | GEN |
| EVT-006 | srv 2013 (low disk) | W | GEN |
| EVT-007 | Time-Service sync errors | I | GEN |

## 8. Redis, SSRS, RabbitMQ (§11) — P4

| ID | Check | Sev | Source |
|---|---|---|---|
| RDS-001 | Redis unreachable or AUTH fails | C | ARCH |
| RDS-002 | `evicted_keys` increasing | W | GEN |
| RDS-003 | `used_memory` > 90 % of `maxmemory` (W); `maxmemory` = 0 (I) | W/I | GEN |
| RDS-004 | `mem_fragmentation_ratio` > 1.5 (I) / < 1.0 (W, swapping) | I/W | GEN |
| RDS-005 | `uptime_in_seconds` < 3600 (recent restart) | W | GEN |
| RDS-006 | SLOWLOG entries in window > 10 or LATENCY spikes > 100 ms | I | GEN |
| RDS-007 | `rejected_connections` increasing | W | GEN |
| RDS-008 | `rdb_last_bgsave_status` / `aof_last_write_status` = err | W | GEN |
| RDS-009 | An App node has no connection in `CLIENT LIST` (not using Redis) | W | ARCH |
| SSRS-001 | SSRS service not running | C | GEN |
| SSRS-002 | Report failure rate > 5 % in 24 h | W | GEN |
| SSRS-003 | Report p95 > 60 s — labelled "data" (TimeDataRetrieval dominant → SQL) or "render" | I | GEN |
| SSRS-004 | Subscription last status = failure | W | GEN |
| RMQ-001 | Node memory/disk alarm, or fd/socket usage > 90 % | C | GEN |
| RMQ-002 | Queue with messages > 100 and consumers = 0 (workflow stuck) | W | ARCH |
| RMQ-003 | Unacked messages growing for 15 min | I | GEN |

## 9. .NET runtime of the Rahkaran process (§9.4, optional) — P4

| ID | Check | Sev | Source |
|---|---|---|---|
| NET-001 | % time in GC > 20 % sustained 60 s ("GC wait" symptom) | W | KB-GC (threshold GEN) |
| NET-002 | Gen2 collections > 1/min sustained, or GC heap > 80 % of memory available to the process | W | KB-GC (threshold GEN) |
| NET-003 | ThreadPool queue length > 0 sustained 30 s with thread count rising (starvation) | W | GEN |
| NET-004 | Exceptions/sec > 3 × baseline | I | GEN |
| NET-005 | w3wp/dotnet private bytes > 40 % of RAM | W | KB-GC (threshold GEN) |
| NET-006 | ASP.NET Core failed requests/sec > 1 % of requests/sec | W | GEN |

## 10. Rahkaran site-level (§6, §3) — P4

| ID | Check | Sev | Source |
|---|---|---|---|
| RK-001 | Rahkaran build/version differs across App nodes ⚠U (version source = configurable file path / query pack) | W | ARCH |
| RK-002 | SQL sessions per App host deviate > 30 % from even/weighted share | I | GEN |
| RK-003 | Report node and Data node are the same machine while site profile says separate | I | ARCH |

---

## Rule hygiene

- Every rule JSON has a fixture test: a facts file that must fire it, and one that must not.
- `general` rules display the GEN label everywhere (UI tooltip, report row, PDF).
- Rule edits by the team: drop-in JSON in `rules/` next to the exe overrides embedded defaults by `id`;
  the Settings page shows which rules are overridden.
- Rules never produce actions — the recommendation field is text only, by schema (no URLs to execute,
  no scripts).
