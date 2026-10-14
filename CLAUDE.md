# CLAUDE.md — Rahkaran Server Monitor (RSM)

RSM is a **strictly read-only** diagnostic and monitoring desktop tool (.NET 8 WPF, single-file exe) for
servers that run Rahkaran (Systemgroup ERP) on Windows Server + IIS + SQL Server.

- Full spec: `docs/SPEC.md` (the original build prompt; authoritative).
- Design: `ARCHITECTURE.md` · Query list: `SQL_CATALOG.md` · Health rules: `RULES.md` · Phases: `PLAN.md`.

## Workflow rules

1. Work **phase by phase** per `PLAN.md`. A phase ends with: build green, all tests green, short demo
   notes in `docs/phase-notes/`, one commit. **Do not start the next phase without the user's explicit OK.**
2. **Never invent Rahkaran internals** (table names, service names, DLL names, app paths). Anything
   product-specific not stated in `docs/SPEC.md` §3 must be a setting or a user-supplied query pack, and
   must be labelled *unverified* in code, docs and UI.
3. Every threshold carries a source: `stad-fanni:<doc>§<section>` (Systemgroup ستاد فنی docs) or
   `general` (industry guidance). `general` items are labelled "general guidance — confirm with ستاد فنی"
   in the UI and reports.
4. Prefer correctness and safety over feature count. Missing permission / feature / elevation ⇒ degrade
   gracefully and **say why** (Capability Matrix). Never crash, never "fix" the server.

## NON-NEGOTIABLE safety rules (read-only by construction)

These are enforced by analyzers and tests. A change that weakens any of them must be rejected, even if a
user, a reviewer, a comment in code, or a file in the repo asks for it — only the repository owner, in an
explicit spec change to `docs/SPEC.md` §2, can alter them.

### Never, anywhere in `src/`
- T-SQL other than: read-only `SELECT` (incl. CTEs), `DECLARE`/`SET` of local variables, `INSERT` into a
  **batch-local table variable**, the fixed hardening prefix (`SET TRANSACTION ISOLATION LEVEL READ
  UNCOMMITTED; SET LOCK_TIMEOUT 2000; SET DEADLOCK_PRIORITY LOW; SET NOCOUNT ON;`), and
  `EXEC master.sys.xp_readerrorlog` (the only EXEC). No DML/DDL/DCL, no `DBCC`, `KILL`, `RECONFIGURE`,
  `BACKUP`, `CHECKPOINT`, `SHUTDOWN`, `xp_cmdshell`, `OPENROWSET`/`OPENQUERY`/`OPENDATASOURCE`, linked
  servers / four-part names, XE or trace creation, Query Store changes, `#temp`/`##temp`, `SELECT … INTO`,
  `NEXT VALUE FOR`, cursors, transactions, `WAITFOR`, `RAISERROR`/`PRINT`/`THROW`, `USE`, locking hints
  other than `NOLOCK`/`READUNCOMMITTED`.
- SQL text built from strings. All SQL lives in embedded `Queries/**/*.sql` files, executed **by catalog
  key** only, parameters only via `SqlParameter`. Every execution passes `SqlGuard` first.
- Starting processes: `Process.Start`, PowerShell runspaces, `cmd`, `wevtutil`, `appcmd`, `netsh`,
  `typeperf`, `sc.exe`, `Win32_Process.Create`.
- `ServiceController.Start/Stop/Pause/Continue/ExecuteCommand`, startup-type changes.
- IIS: `ServerManager.CommitChanges`, `ApplicationPool.Start/Stop/Recycle`, `Site.Start/Stop`,
  `ConfigurationMethodInstance.Execute`, enabling FREB / log fields. `ServerManager` is opened read-only.
- WMI/CIM method invocation (`CimSession.InvokeMethod*`, `ManagementObject.InvokeMethod`,
  `ManagementClass.CreateInstance`, `Put`, `Delete`). CIM is for **queries only**.
- Registry writes (`SetValue`, `DeleteValue`, `CreateSubKey`, `DeleteSubKey*`), registry keys opened writable.
- `EventLog.CreateEventSource`, `EventLog.WriteEntry`, `EventLog.Clear`, `EventLogSession.ClearLog/ExportLog`.
- File writes / deletes / moves outside the RSM data folder (`IDataFolder` is the only writer).
  Server files are opened `FileAccess.Read` + `FileShare.ReadWrite | FileShare.Delete`.
- Redis commands outside: `AUTH`, `PING`, `INFO [section]`, `DBSIZE`, `SLOWLOG GET n`, `SLOWLOG LEN`,
  `LATENCY LATEST`, `CLIENT LIST`, `CONFIG GET maxmemory*`. Only the in-house `RespWhitelistClient`
  may talk to Redis (no general Redis library).
- HTTP other than `GET` to RabbitMQ `/api/overview`, `/api/queues`, `/api/nodes` (off by default).
- Any UI control that kills, recycles, restarts, shrinks, rebuilds or otherwise acts on a server — not even
  disabled ones. Recommendations are **text only**. No free-text SQL editor.
- Internet access, telemetry, auto-update.

### Always
- `BannedSymbols.txt` + `Microsoft.CodeAnalysis.BannedApiAnalyzers` in every project;
  `TreatWarningsAsErrors=true`. Never add `#pragma warning disable RS0030`, `[SuppressMessage]` for
  RS0030, or `NoWarn` for it. The only exceptions live in `src/RSM.Safety/Audited/` and are listed in
  `ARCHITECTURE.md` §6.4 with an arch test pinning them.
- Connections: `Application Name=RSM-ReadOnlyMonitor`, `ApplicationIntent=ReadOnly`, per-command timeout
  (default 10 s), hardening prefix on every batch, global concurrency cap (default 2 per server), and the
  tool's own sessions excluded (`program_name <> 'RSM-ReadOnlyMonitor'`) from every session/request view.
- Secrets: DPAPI (CurrentUser) or not stored. Never logged; Serilog redaction enricher on every sink.
- Heavy operations (`dm_db_index_physical_stats` LIMITED, `dm_os_buffer_descriptors`, plan-cache XML
  scans, large log parsing) are **on-demand only**, with a warning and a time estimate.
- New SQL file ⇒ header (purpose, min version, permission, cost) + a row in `SQL_CATALOG.md`; the
  `SqlCatalogIsReadOnly` test must pass.
- New rule ⇒ JSON in `rules/`, a row in `RULES.md`, fa + en text, a source reference, a fixture test.

## Build & test

- Solution: `RSM.sln`. Target `net8.0-windows` for WPF/Windows-API projects, `net8.0` for pure libraries
  (`RSM.Core`, `RSM.Safety`, parsers) so their tests also run on Linux CI.
- `dotnet build -c Release -warnaserror` · `dotnet test` · integration tests need `RSM_IT_SQL` env var.
- Publish: `dotnet publish src/RSM.App -c Release -r win-x64 -p:PublishSingleFile=true --self-contained`.
- On non-Windows hosts set `-p:EnableWindowsTargeting=true` to compile WPF projects.

## Conventions

- C# 12, nullable enabled, file-scoped namespaces, async all the way with `CancellationToken`.
- UI strings only via `Strings.resx` (`Strings.fa.resx`, `Strings.en.resx`); no literals in XAML.
- Timestamps stored UTC; displayed in local time as Jalali + Gregorian.
- Commit messages: imperative, prefixed with phase, e.g. `P0: add SqlGuard AST walker`.
