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
| OS-019 | OS is not the site baseline *Windows Server 2022 Standard* (`os.build` ≠ 20348 or `os.sku` ≠ 7; both are rule params) | I | GEN (site baseline set by the owner) |

## 2. Storage (§9.5, §7.14, TV3 Storage) — P1 (config) / P2 (live)

*P1 ships STO-003, STO-004, STO-005 (from SQL Server's view of the volumes). STO-001/002 need the Windows collector
(block size, system drive) and STO-006…009 need live counters — they arrive in P2.*

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
| SQL-VER-003 | Extended support ends within 365 days (I) or has ended (W) — dates from `known-builds.json` | I/W | GEN |
| SQL-VER-004 | Latest CU installed but the newer security update (CU + GDR) is missing | I | GEN |
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
| SQL-AGT-008 | No index/statistics maintenance job ran in last 48 h ⚠U (patterns) — *deferred until the job-name patterns are confirmed (decision Q6); DB-STAT-001 covers the symptom* | I | TV3 (Maintenance) |
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
| SQL-SEC-009 | RSM connected with a sysadmin login (banner: "high-privilege account used; RSM stays read-only") | I | GEN (spec §2.4) |

### 3.5 Live performance — P2

*Shipped in P1 already (snapshot values, no sampling needed):* SQL-MEM-005, SQL-TDB-001, SQL-TDB-002, SQL-TDB-004.
*Facts collected in P1 for the report only (rules in P2, need live deltas):* cumulative file latency per database.

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
| DB-BKP-002 | Non-Rahkaran user DB in FULL recovery without a log backup for > 24 h (log keeps growing). The 2-hour rule for the Rahkaran DB is DB-RM-002 | W | KB-LOG |
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

## 11. Query packs (§8.7, user-supplied) — P4

| ID | Check | Sev | Source |
|---|---|---|---|
| PACK-001 | A query-pack threshold is exceeded (severity Info/Warning/Critical and source as declared in the manifest; ⚠U) | I/W/C | GEN (per manifest) |
| PACK-002 | A query-pack query could not run (missing object, permission, timeout) ⚠U | I | GEN |

## 12. Slowness causes (§12, `rules/slowness/`) — P5

Scored by `RSM.Core.Slowness` from a synchronized capture (default 120 s, all nodes). Confidence = weighted mean of the
best detector strengths (0..1) across nodes, multiplied by exclusion factors, capped at 0.95; causes below 0.15 are not
listed; a cause with a missing *required* detector is skipped. Every cause and detector is `general` guidance —
confirm with ستاد فنی. Next steps are text only. Acceptance fixtures: `tests/RSM.Tests/SlownessTests.cs` (one per
`tests/lab` scenario).

| Cause | Evidence (weight, * = required) | Excluded by (above → ×factor) | Source |
|---|---|---|---|
| `SLOW-APP-CONSUMING` The application tier is slow consuming SQL results | AsyncNetworkIo 0.5*, AppCpuHigh 0.25, GcPressure 0.25 | — | GEN |
| `SLOW-ARR` ARR imbalance or an unhealthy App server | ArrImbalance 0.5, ArrNodeUnhealthy 0.5 | — | GEN |
| `SLOW-BLOCKING` Blocking chain | BlockingChain 0.6*, LockWaitShare 0.4 | — | GEN |
| `SLOW-CLIENT-NETWORK` Client or network slow to read SQL results | AsyncNetworkIo 0.5*, NetworkSaturation 0.25, SqlRttHigh 0.25 | AppCpuHigh >0.5 → ×0.5, GcPressure >0.5 → ×0.5 | GEN |
| `SLOW-CPU-APP` IIS worker processes are using most of the CPU | AppCpuHigh 0.5*, HostCpuSaturated 0.3, GcPressure 0.2 | — | GEN |
| `SLOW-CPU-NONSQL` Host CPU saturated by a process other than SQL Server | HostCpuSaturated 0.6*, NonSqlCpu 0.4 | SqlCpuHigh >0.5 → ×0.5, AppCpuHigh >0.5 → ×0.6 | GEN |
| `SLOW-CPU-SQL` SQL Server CPU is high, with high signal waits | SqlCpuHigh 0.5*, SignalWaitsHigh 0.3, HostCpuSaturated 0.2 | — | GEN |
| `SLOW-CPU-THROTTLED` CPU is throttled by the power plan or firmware | CpuThrottled 0.6*, PowerPlanFinding 0.4 | — | GEN |
| `SLOW-DISK-DATA` Disk latency on the SQL Server data volumes | DiskLatencySqlData 0.4, DiskReadLatencyOs 0.3, IoDataWaitShare 0.3 | — | GEN |
| `SLOW-DISK-LOG` Slow transaction-log writes (WRITELOG) | WritelogShare 0.5, LogWriteLatency 0.3, DiskWriteLatencyOs 0.2 | — | GEN |
| `SLOW-EXPENSIVE-QUERY` Expensive queries running now or regressed plans | LongRunningRequest 0.4, SqlCpuHigh 0.3, PlanRegression 0.3 | BlockingChain >0.5 → ×0.4 | GEN |
| `SLOW-GC` High .NET garbage-collection time in the application | GcPressure 0.6*, AppCpuHigh 0.2, ThreadPoolStarvation 0.2 | — | GEN |
| `SLOW-HTTP-503` HTTP 503 errors or dropped requests | Http503Burst 0.5, Http5xxRate 0.3, RecentRecycleOrRapidFail 0.2 | — | GEN |
| `SLOW-IIS-QUEUE` IIS request queue is backing up | IisQueue 0.5*, ThreadPoolStarvation 0.2, Http503Burst 0.15, AppCpuHigh 0.15 | — | GEN |
| `SLOW-MEM-OS` Memory pressure on the server | MemoryPressureOs 0.6*, Paging 0.4 | — | GEN |
| `SLOW-MEM-SQL` SQL Server memory pressure (grants pending, PLE drop) | MemoryGrantsPending 0.5, PleDrop 0.3, MemoryPressureOs 0.2 | — | GEN |
| `SLOW-NETWORK` Network saturation or high SQL round-trip time | NetworkSaturation 0.5, SqlRttHigh 0.5 | AsyncNetworkIo >0.5 → ×0.6 | GEN |
| `SLOW-POOL-RECYCLE` Application pool recycled or rapid-failed recently | RecentRecycleOrRapidFail 0.7*, Http5xxRate 0.3 | — | GEN |
| `SLOW-REDIS` Redis latency, evictions or restart | RedisLatency 0.4, RedisEvictions 0.35, RedisRestart 0.25 | — | GEN |
| `SLOW-SQL-WORKERS` SQL Server is running out of worker threads | SqlWorkerStarvation 0.7*, BlockingChain 0.3 | — | GEN |
| `SLOW-TEMPDB` tempdb allocation contention | TempdbContention 1* | — | GEN |
| `SLOW-THREADPOOL` .NET thread-pool starvation | ThreadPoolStarvation 0.7*, IisQueue 0.3 | — | GEN |

Detectors (strength ramps linearly from *from* (0) to *to* (1); from > to = lower is worse):

| Detector | Input | Stat | From → To | Source |
|---|---|---|---|---|
| HostCpuSaturated | `win.cpu.total_pct` | P50 | 70 → 95 % | GEN |
| NonSqlCpu | `sql.cpu.other_pct` | P50 | 30 → 70 % | GEN |
| SqlCpuHigh | `sql.cpu.sql_pct` | P50 | 60 → 90 % | GEN |
| SignalWaitsHigh | `sql.waits.signal_pct` | Avg | 15 → 40 % | GEN |
| CpuThrottled | fact `win.cpu.throttled_samples` | — | 0 → 5 | GEN |
| PowerPlanFinding | findings OS-001, OS-002 | — | active = 1 (info 0.6) | GEN |
| MemoryPressureOs | `win.mem.available_mb` | Min | 2048 → 256 MB | GEN |
| Paging | `win.mem.pages_sec` | Avg | 500 → 5000 /s | GEN |
| MemoryGrantsPending | `sql.perf.memory_grants_pending` | Max | 0.5 → 5 | GEN |
| PleDrop | `sql.perf.ple_s` | DropPct | 30 → 80 % | GEN |
| GcPressure | `dotnet.time_in_gc_pct` | Avg | 10 → 40 % | GEN |
| AppCpuHigh | `win.process.cpu_pct`[w3wp] | P50 | 40 → 85 % | GEN |
| DiskLatencySqlData | `sql.io.data_read_ms`[*] | P50 | 20 → 100 ms | GEN |
| DiskReadLatencyOs | `win.disk.read_ms`[*] | P50 | 20 → 100 ms | GEN |
| DiskWriteLatencyOs | `win.disk.write_ms`[*] | P50 | 20 → 100 ms | GEN |
| IoDataWaitShare | `sql.waits.ms_per_sec`[IoData] share of `_Total` | Avg | 20 → 60 % | GEN |
| LogWriteLatency | `sql.io.log_write_ms`[*] | P50 | 5 → 30 ms | GEN |
| WritelogShare | `sql.waits.ms_per_sec`[IoLog] share of `_Total` | Avg | 20 → 60 % | GEN |
| BlockingChain | `sql.blocking.max_wait_s` | Max | 5 → 60 s | GEN |
| LockWaitShare | `sql.waits.ms_per_sec`[Locking] share of `_Total` | Avg | 20 → 60 % | GEN |
| LongRunningRequest | fact `sql.req.longest_running_s` | — | 30 → 300 s | GEN |
| PlanRegression | fact `db[*].qds.regressed_count` | — | 0 → 5 | GEN |
| TempdbContention | `sql.tempdb.alloc_contention_waiters` | Max | 1 → 10 | GEN |
| AsyncNetworkIo | `sql.waits.ms_per_sec`[Network] share of `_Total` | Avg | 20 → 60 % | GEN |
| SqlWorkerStarvation | `sql.waits.ms_per_sec`[Worker] share of `_Total` | Avg | 5 → 30 % | GEN |
| NetworkSaturation | `win.net.util_pct`[*] | P50 | 60 → 90 % | GEN |
| SqlRttHigh | `rsm.sql_rtt_ms` | P50 | 5 → 50 ms | GEN |
| IisQueue | `win.iis.queue.size`[*] | Max | 1 → 50 | GEN |
| ThreadPoolStarvation | `dotnet.threadpool_queue` | P50 | 10 → 500 | GEN |
| Http503Burst | findings IIS-ERR-002, IIS-ERR-004, IIS-Q-001 | — | active = 1 (info 0.6) | GEN |
| Http5xxRate | `iis.log.err5xx_pct_15m`[*] | Last | 1 → 10 % | GEN |
| RecentRecycleOrRapidFail | findings IIS-POOL-002, IIS-POOL-003, IIS-POOL-001 | — | active = 1 (info 0.6) | GEN |
| ArrImbalance | fact `farm[*].distribution_deviation_pct` | — | 30 → 80 % | GEN |
| ArrNodeUnhealthy | findings ARR-001, ARR-002 | — | active = 1 (info 0.6) | GEN |
| RedisLatency | fact `redis.latency_max_ms` | — | 20 → 200 ms | GEN |
| RedisEvictions | findings RDS-002, RDS-006 | — | active = 1 (info 0.6) | GEN |
| RedisRestart | findings RDS-001, RDS-005, RDS-007 | — | active = 1 (info 0.6) | GEN |

---

## Implementation status

Rules marked P1 above live in [`rules/`](rules/) — one JSON file per rule, named after its id, with fa/en title,
explanation and recommendation, source, parameters and **built-in examples** (at least one that fires with the expected
severity and one that stays quiet). `RSM.Tests.RuleCatalogTests` runs every example and checks that each rule id has a
row in this file. Instance-scoped rules use `"forEach": "db"` / `"volume"` / `"net"` (network adapter) / `"ag"` (availability replica)
and, from Phase 3, `"site"` / `"pool"` (IIS application pool) / `"farm"` (ARR) / `"rewrite"` (URL Rewrite rule) / `"cert"` (https binding).

### Compliance baseline (Phase 1)

33 rules carry a `baseline` block — `item` and `expected` (fa + en) plus the `actual` fact key. The report's
compliance table lists one row per such rule (per Rahkaran / ReportServer database for `forEach: db` rules), so the
expected value is written next to the threshold and source it describes. `RuleCatalogTests` checks that every
`baseline.actual` is a fact a collector emits and that the spec §7.2 configuration items are all covered;
`ReportTests` checks that every fact a rule reads is emitted by a collector.

Changes made while building the report (behaviour, not thresholds):
- `SQL-AGT-004` evaluates `agent.maint_plan_mentions_backup` first, so it is quiet instead of *unknown* when no
  ستاد فنی tool-job patterns are configured.
- `SQL-AGT-009` requires `db.has_full_backup`; a database that was never backed up is reported by `DB-BKP-001`
  instead of leaving the schedule rule *unknown*.
- `DB-INT-001` / `DB-RM-002` titles no longer print the "never" sentinel (99999); reports show "never".
- Rule placeholders that hold lists (user or job names) render as "a, b, c" (max 10).

### Phase 2 (live)

40 rules added: §1 OS-001…009, 011…015, 017, 018; §2 STO-001, 002, 006, 007; §3.5 SQL-CPU-001…004, SQL-MEM-006…008,
SQL-WAIT-001, SQL-IO-001…003, SQL-TDB-003, SQL-BLK-001/002, SQL-DL-001, SQL-TRN-001, SQL-PLC-001, SQL-QS-001/002,
SQL-HA-001 (SQL-MEM-005, SQL-TDB-001/002/004 already existed). Six of them carry a compliance `baseline` (OS-001, 003,
004, 005, STO-001, 002); the report's compliance table now also lists volumes that hold SQL Server files.

"Sustained" conditions are computed by the collectors as window facts, so the rule language stays simple:
`…_min3` / `…_max3` (last three samples), `…_min5` (last five one-minute CPU points), `…_min60` / `…_max60` (the last
60 s, only once the samples cover ≥ 75 % of the window), `win.cpu.throttled_samples` (trailing count). A one-shot
diagnosis takes a 5 s baseline for interval values (waits, counters, file latency); window facts that need more
samples stay *unknown* there and are decided in the live view.

Deferred, with the reason:
- **OS-010** (unexpected shutdowns) needs the event logs → done in P3.
- **OS-016** (TCP TimeWait / ephemeral ports) has no performance counter for TimeWait → done in P3 with
  `MSFT_NetTCPConnection`.
- **SQL-MEM-007** uses the absolute fallback (PLE < 300 s per 4 GB of buffer pool, three samples); the
  hour-of-week baseline arrives with history in P5.
- **OS-011** checks the `sqlservr.exe` process exclusion and lists excluded paths; matching every data/log path
  against the exclusions → done in P3 (site analyzer on the same node).
- **SQL-TDB-003** counts PAGELATCH waiters on tempdb allocation pages (PFS/GAM/SGAM by page number) at the moment
  of sampling (≥ 3); it is a point-in-time signal, not a rate.

Phase 1 noise removed (seen in CI run 5): `DB-IDX-001` now requires `db.is_rahkaran` (fragmentation is only
scanned on demand in the Rahkaran DB), `DB-FILE-004` and `DB-LOG-003` skip tempdb (`db.database_id != 2`; tempdb is
recreated at startup and has its own rules).

### Phase 3 (IIS, ARR, services, events)

42 rules added: §5 IIS-LOG-001, IIS-POOL-001…009, IIS-NUMA-001, IIS-Q-001, IIS-REQ-001, IIS-ERR-001…004, IIS-LAT-001,
IIS-CERT-001, IIS-CFG-001/002; §6 ARR-001…006; §7 SVC-001…006, EVT-001…007; and the P2 deferrals OS-010, OS-016.
OS-011 now also fires when a SQL Server data/log folder is not under a Defender path exclusion.

Cross-node and cross-layer facts come from *site analyzers* that run after every node is collected (and, live, after
`iis.config` / `iis.runtime` / `iis.logs` / `win.inventory` / `sql.storage` results): pool and web.config drift between
App nodes (`pool.drift_keys`, `site.config_drift_keys`; secrets are masked before comparison so they never differ),
rewrite path segments deployed on any node (`rewrite.segment_deployed`), worker starts outside the recycle schedule
(`pool.recent_unplanned_start`), the ARR measured distribution (`farm.distribution_deviation_pct`, from the App
nodes' IIS logs; only when every enabled farm server maps to a monitored node and ≥ 200 requests were logged) and
the Defender folder match for OS-011.

Behaviour notes (sources and thresholds as in the tables above):
- **ARR-001 / ARR-002** are configuration-based: ARR's runtime health is exposed only through IIS configuration
  *methods*, which RSM may not execute (spec §2), so "unavailable" means disabled or no server in the farm; unhealthy
  servers show up in ARR-003 (distribution) and in the App nodes' own findings.
- **ARR-005** judges only bare host names (no dot, not an IP, not `localhost`) in a rewrite target, because that is how
  ARR names a farm; targets with a domain or IP are real servers and are not judged.
- **IIS-LAT-001** compares each URL stem's p95 of the last 15 minutes with the median of its own earlier 15-minute p95
  values (up to 24 h, at least three windows, ≥ 20 requests, and at least 200 ms slower) — a per-stem baseline that
  needs the live view; a one-shot diagnosis leaves it quiet.
- **IIS-ERR-001** needs ≥ 50 requests in the 15-minute window. **IIS-Q-001** escalates to critical when HTTP.sys is
  rejecting requests. **IIS-POOL-001** reads the pool state from IIS (local, elevated) or from the WAS counters (remote).
- **EVT-005** (Schannel) fires from 10 events per 24 h: single TLS alerts from scanners and old clients are normal.
- **SVC-001** ignores Windows Update (trigger-started) and the attendance service, which has its own rule (SVC-005,
  unverified pattern).
- **OS-016** counts `MSFT_NetTCPConnection` rows in TimeWait against `MSFT_NetTCPSetting.DynamicPortRangeNumberOfPorts`
  every minute; older systems without the NetTCPIP provider leave it *unknown*.

### Phase 4 (Redis, SSRS, RabbitMQ, .NET runtime, topology, query packs)

29 rules added (→ 194): §8 RDS-001…009, SSRS-001…004, RMQ-001…003; §9 NET-001…006; §10 RK-001…003; §11 PACK-001/002.

Behaviour notes (sources and thresholds as in the tables above):
- **RDS-\*** are judged only when Redis answered (`redis.reachable`); an unreachable Redis or a failed AUTH is RDS-001
  alone. Increasing counters (RDS-002, RDS-007) need two samples of the live view. RDS-009 resolves the site's App
  nodes by DNS and compares with the source addresses in `CLIENT LIST` (RSM's own connection excluded).
- **SSRS-002** needs ≥ 20 executions in 24 h; **SSRS-003** needs ≥ 5 runs of a report and labels it *data* when
  `TimeDataRetrieval` dominates (the SQL side), otherwise processing/rendering. At most 20 000 ExecutionLog3 rows are
  read; report parameters are never read.
- **RMQ-\*** need the RabbitMQ management API (off by default). RMQ-003 needs 15 minutes of live samples in which the
  total of unacknowledged messages never fell and ended higher.
- **NET-\*** need the optional .NET runtime counters (site setting, local App server only). "Sustained" means every
  sample in the window (collected every 30 s): NET-001 60 s above 20 % time in GC, NET-002 3 minutes above one Gen2
  collection per minute or a GC heap above 80 % of RAM, NET-003 30 s of a non-empty thread-pool queue with a rising
  thread count. NET-004 compares with the median of the same monitoring session (≥ 10 samples). NET-005 uses the
  working set (EventPipe has no private-bytes counter). NET-006 computes the failed-request rate from the change of
  the ASP.NET Core `failed-requests` total between samples.
- **RK-001** compares the fingerprint (SHA-256 prefix) of the *unverified* version-file setting across App nodes, or —
  when that is not set or not found — the application path segments of the Rahkaran sites. **RK-002** needs two or
  more App nodes and ≥ 20 SQL sessions from them; the expected share follows the ARR farm weights when one farm
  covers every App node, otherwise an even split. **RK-003** is judged only when both nodes report a machine name
  or addresses.
- **PACK-001** takes severity and source from the pack manifest (Info/Warning/Critical; `general` or
  `stad-fanni:…`) and is always labelled unverified; **PACK-002** reports a pack query that could not run.

## Rule hygiene

- Every rule JSON has a fixture test: a facts file that must fire it, and one that must not.
- `general` rules display the GEN label everywhere (UI tooltip, report row, PDF).
- Rule edits by the team: drop-in JSON in `rules/` next to the exe overrides embedded defaults by `id`;
  the Settings page shows which rules are overridden.
- Rules never produce actions — the recommendation field is text only, by schema (no URLs to execute,
  no scripts).
