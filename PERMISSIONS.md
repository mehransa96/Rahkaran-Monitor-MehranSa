# PERMISSIONS — least-privilege access for RSM

RSM is read-only by construction, but the accounts it uses should also be **unable** to change anything. The
scripts below are run **by the customer's DBA / Windows administrator, never by RSM** (RSM's SqlGuard would
refuse them). Anything not granted simply shows up as *unavailable* in RSM's Capability Matrix, with the reason
and a pointer to the section below.

> **Verify on the target version.** Statements marked *verify* differ between SQL Server builds.

## 1. SQL Server — server level

```sql
-- Run as a sysadmin. Choose a strong password; RSM stores it only with Windows DPAPI (or not at all).
USE master;
CREATE LOGIN rsm_monitor WITH PASSWORD = N'<strong password>', CHECK_POLICY = ON;

GRANT VIEW SERVER STATE    TO rsm_monitor;  -- DMVs: waits, requests, blocking, memory, I/O, counters
                                            -- (SQL 2022: implies VIEW SERVER PERFORMANCE STATE + SECURITY STATE)
GRANT VIEW ANY DEFINITION  TO rsm_monitor;  -- security posture: logins, sysadmin members, linked servers
GRANT CONNECT ANY DATABASE TO rsm_monitor;  -- per-database files, VLFs, statistics (SQL 2014+)
```

Windows authentication instead of a SQL login: replace `CREATE LOGIN … WITH PASSWORD` by
`CREATE LOGIN [DOMAIN\rsm-svc] FROM WINDOWS;` and keep the grants.

## 2. SQL Server — msdb (backups, Agent jobs)

Decision Q7: **no `db_datareader` on msdb** — it would also expose mail profiles, SSIS packages and every other
msdb table. Only what RSM reads is granted:

```sql
USE msdb;
CREATE USER rsm_monitor FOR LOGIN rsm_monitor;
ALTER ROLE SQLAgentReaderRole ADD MEMBER rsm_monitor;

GRANT SELECT ON dbo.backupset             TO rsm_monitor;  -- last backups, LSN chain, throughput
GRANT SELECT ON dbo.backupmediafamily     TO rsm_monitor;  -- backup destinations (local vs UNC)
GRANT SELECT ON dbo.backupfile            TO rsm_monitor;
GRANT SELECT ON dbo.suspect_pages         TO rsm_monitor;  -- integrity
GRANT SELECT ON dbo.sysjobs               TO rsm_monitor;  -- Agent jobs
GRANT SELECT ON dbo.sysjobsteps           TO rsm_monitor;  -- shrink / backup / CDC detection (commands are masked in exports)
GRANT SELECT ON dbo.sysjobhistory         TO rsm_monitor;
GRANT SELECT ON dbo.sysjobactivity        TO rsm_monitor;
GRANT SELECT ON dbo.syssessions           TO rsm_monitor;
GRANT SELECT ON dbo.sysschedules          TO rsm_monitor;
GRANT SELECT ON dbo.sysjobschedules       TO rsm_monitor;
GRANT SELECT ON dbo.syscategories         TO rsm_monitor;
GRANT SELECT ON dbo.sysmaintplan_plans    TO rsm_monitor;  -- maintenance plans (conflict with ستاد فنی tool jobs)
GRANT SELECT ON dbo.sysmaintplan_subplans TO rsm_monitor;
-- Only where CDC is used:
-- GRANT SELECT ON dbo.cdc_jobs TO rsm_monitor;
-- Only where log shipping is used:
-- GRANT SELECT ON dbo.log_shipping_monitor_primary   TO rsm_monitor;
-- GRANT SELECT ON dbo.log_shipping_monitor_secondary TO rsm_monitor;
```

## 3. SQL Server error log (optional)

RSM reads the error log with `master.sys.xp_readerrorlog`, the only procedure it may execute. By default this
requires `securityadmin`. Whether an explicit grant is honoured differs by version — *verify*:

```sql
USE master;
GRANT EXECUTE ON sys.xp_readerrorlog TO rsm_monitor;  -- verify on the target version
```

If not granted, RSM falls back to reading the `ERRORLOG` file directly when it runs on the server (or through the
admin share), which needs read access to the SQL Server `Log` folder. Do **not** add `rsm_monitor` to
`securityadmin` just for this.

## 4. SSRS (optional, Report server)

```sql
USE ReportServer;  -- the actual name may be ReportServer$INSTANCE
CREATE USER rsm_monitor FOR LOGIN rsm_monitor;
GRANT SELECT ON dbo.ExecutionLog3 TO rsm_monitor;   -- verify object names per SSRS version
GRANT SELECT ON dbo.Subscriptions TO rsm_monitor;   -- subscription failures (P4)
GRANT SELECT ON dbo.Catalog       TO rsm_monitor;   -- report paths for subscriptions (P4)
```

## 5. Windows and IIS (Phase 2–3)

| Need | Grant |
|---|---|
| Performance counters (local / remote) | member of **Performance Monitor Users** |
| Event logs (System, Application) | member of **Event Log Readers**; firewall rule *Remote Event Log Management* for remote reads |
| WMI / CIM inventory and services (remote) | WinRM (5985/5986) or DCOM; read access to `root\cimv2` (and `root\StandardCimv2` for TCP connection counts) |
| Registry checks (remote) | the Remote Registry service and read rights for the current Windows user (whitelisted keys only) |
| IIS configuration (applicationHost.config, web.config) | local: **Administrators** (run RSM elevated); remote: read access to the admin share `\\server\C$\Windows\System32\inetsrv\config` and the sites' folders |
| IIS live requests and pool runtime state | run RSM **elevated on the IIS server itself** + the IIS **Request Monitor** feature (remote nodes show pool state from the WAS counters) |
| IIS W3C / HTTPERR / stdout / FREB logs | read access to the log folders (local or admin share, e.g. `\\server\C$\inetpub\logs\LogFiles`, `\\server\C$\Windows\System32\LogFiles\HTTPERR`) |
| TLS certificates of bindings | local only (LocalMachine store read); remote nodes show expiry as unknown |
| SQL ERRORLOG files via admin share | read access to the SQL `Log` folder |

RSM never needs write access to any of these: files are opened read-only with shared access, IIS configuration is
never committed, and there is no control for services, pools or sites.

## 6. Redis / RabbitMQ (Phase 4)

- **Redis:** network access to the Redis port (default 6379) and the Redis password if one is set. RSM sends only
  `AUTH`, `PING`, `INFO`, `DBSIZE`, `SLOWLOG GET|LEN`, `LATENCY LATEST`, `CLIENT LIST`, `CONFIG GET maxmemory*`.
- **RabbitMQ management API (off by default):** a user with the `monitoring` tag; RSM sends `GET` to
  `/api/overview`, `/api/queues`, `/api/nodes` only. HTTP 401/403 shows as *Permission denied* with that hint.
- **CodeMeter:** nothing extra — the service and its listening port come from CIM queries (`Win32_Service`,
  `MSFT_NetTCPConnection`); no licence API is called.

## 6a. .NET runtime counters (Phase 4, optional, off by default)

- Switched on per site (Sites → *.NET runtime counters*, with a warning) and read **only on the machine where RSM
  runs** (local-per-server mode on each App server).
- RSM must run as the pool identity or as a member of **Administrators** (elevated) to open the diagnostics endpoint
  of `w3wp.exe` (in-process hosting) or of the `dotnet.exe` child of `w3wp` (out-of-process hosting).
- The session carries the `System.Runtime` and `Microsoft.AspNetCore.Hosting` EventCounters only — no dumps,
  profilers, startup hooks, environment reads or other providers (compile-time bans + tests). .NET Framework pools
  have no endpoint and are reported as not covered.

## 6b. Query packs (Phase 4)

- Pack queries run with the same `rsm_monitor` login, in the Rahkaran database (or the database named in the
  manifest): the login needs `SELECT` on the tables/views a pack reads (`db_datareader` in that database, or
  object-level grants) — added only when the team installs a pack.

## 7. What RSM shows if you use `sysadmin`

RSM still works and stays read-only, but the Capability Matrix shows a banner: *"High-privilege account used;
RSM stays read-only."* A dedicated `rsm_monitor` login is recommended.
