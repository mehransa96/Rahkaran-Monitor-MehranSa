# ARCHITECTURE — Rahkaran Server Monitor (RSM)

Status: **approved**; Phase 0 implemented (Core, Safety, Collectors.Sql/Redis probes, Storage, Hosting, App shell, tests).
Sections describe the target design; notes marked *P0* describe what exists today.
Spec: `docs/SPEC.md`. Section references like "§7.5" point into the spec.

---

## 1. Design goals in one paragraph

RSM is a portable .NET 8 WPF tool that collects **read-only** evidence from every tier of a Rahkaran site
(SQL Server, databases, Windows, IIS/ARR, Redis, SSRS, CodeMeter, RabbitMQ), stores it locally, evaluates
data-driven rules, and presents live views, a guided slowness investigation, a unified error timeline and
exportable reports in Persian (RTL) and English. The overriding property is that **no code path can change
a monitored server** — enforced at compile time (banned APIs), at load time (query catalog + SqlGuard), at
run time (whitelisted clients, read-only handles) and in CI (unit, architecture and integration tests).

---

## 2. Solution layout

```
RSM.sln
├─ Directory.Build.props          net8 settings, Nullable, TreatWarningsAsErrors, central package versions
├─ src/Directory.Build.props      + BannedApiAnalyzers in every production project (RS0030 = error)
├─ src/Directory.Build.targets    + BannedSymbols.txt (all) and BannedSymbols.NonSafety.txt (all but RSM.Safety)
├─ BannedSymbols*.txt             single source of truth for forbidden APIs (§6.5)
├─ global.json                    .NET 8 SDK (Microsoft-built SDK required for WPF; see CLAUDE.md)
├─ src/
│  ├─ RSM.Core                    net8.0          models, ICollector, scheduler, fact store, rules engine,
│  │                                              expression evaluator, slowness engine, KB loader
│  ├─ RSM.Safety                  net8.0          SqlGuard (ScriptDom), QueryCatalog, SafeSqlExecutor,
│  │                                              RespWhitelistClient, RabbitGetClient, ReadOnlyFile,
│  │                                              Audited/* (the only allowed exceptions, §6.4)
│  ├─ RSM.Parsers                 net8.0          W3C/HTTPERR/errorlog/deadlock/showplan/RESP-INFO/
│  │                                              ExecutionLog/web.config parsers (pure, unit-tested on Linux)
│  ├─ RSM.Collectors.Sql          net8.0          §7 instance-level collectors
│  ├─ RSM.Collectors.Database     net8.0          §8 per-database collectors + query packs
│  ├─ RSM.Collectors.Windows      net8.0-windows  PDH/CIM/EventLog/ServiceController(read)/registry(read)
│  ├─ RSM.Collectors.Iis          net8.0-windows  Microsoft.Web.Administration (read-only) + CIM fallback
│  ├─ RSM.Collectors.Redis        net8.0          uses RSM.Safety.RespWhitelistClient
│  ├─ RSM.Collectors.Rahkaran     net8.0-windows  topology, version drift, SSRS, CodeMeter, RabbitMQ,
│  │                                              EventPipe runtime counters (optional, local only)
│  ├─ RSM.Storage                 net8.0          SQLite schema, migrations, downsampling, baselines
│  ├─ RSM.Reporting               net8.0          Scriban HTML, PDF, JSON bundle, masking, import/merge
│  ├─ RSM.Hosting                 net8.0-windows  DI composition root shared by App and Cli
│  ├─ RSM.App                     net8.0-windows  WPF shell (MVVM), views, localization, charts
│  └─ RSM.Cli                     net8.0-windows  --snapshot | --collect --duration | --export | --import
├─ rules/                         *.json rule definitions (copied next to exe; user-editable)
├─ kb/                            event-ID / error-number / wait-type knowledge base (fa/en JSON)
├─ known-builds.json              SQL Server build → CU map (shipped, editable, no internet)
├─ QueryPacks/                    empty by default; team drops *.sql + manifest.json here
└─ tests/
   ├─ RSM.Safety.Tests            SqlGuard corpus, catalog read-only, RESP whitelist, file guard
   ├─ RSM.ArchTests               NetArchTest dependency/usage rules
   ├─ RSM.Tests                   parsers, rules engine (JSON fixtures), delta math, masking,
   │                              report snapshots (Verify, fa+en)
   └─ RSM.Integration.Tests       skipped unless RSM_IT_SQL / RSM_IT_REDIS env vars are set
```

*P0:* created `RSM.Core`, `RSM.Safety`, `RSM.Collectors.Sql`, `RSM.Collectors.Redis`, `RSM.Storage`, `RSM.Hosting`,
`RSM.App` and the four test projects; the other projects are added in the phase that first needs them.

Why the extra `RSM.Parsers` and `RSM.Hosting` projects vs. the spec proposal: parsers have no Windows
dependency and are the most test-heavy code, so isolating them lets the bulk of tests run on Linux CI;
`RSM.Hosting` avoids duplicating DI wiring between the GUI and the CLI.

### 2.1 Package choices

| Concern | Package | Notes |
|---|---|---|
| MVVM | CommunityToolkit.Mvvm 8.x | source generators |
| Host/DI | Microsoft.Extensions.Hosting 8.x | |
| UI kit | plain styled WPF (*P0 decision*) | WPF-UI 3.x only after its RTL rendering is verified on Windows |
| Charts | ScottPlot.WPF 5.x | streaming `DataLogger`/`DataStreamer`; charts keep LTR axes inside RTL layout |
| Code view | AvalonEdit 6.3 | read-only, T-SQL/XML highlighting |
| SQL | Microsoft.Data.SqlClient 6.1, Dapper 2.1, Microsoft.SqlServer.TransactSql.ScriptDom 161.9142 | `TSql160Parser` |
| CIM | Microsoft.Management.Infrastructure 3.x | queries only (`QueryInstances`, `EnumerateInstances`, `GetInstance`) |
| Event logs | System.Diagnostics.EventLog 8.x (`Eventing.Reader`) | `EventLogSession` for remote |
| Services | System.ServiceProcess.ServiceController 8.x | read members only (banned mutators) |
| Perf counters | System.Diagnostics.PerformanceCounter 8.x | local PDH; remote via CIM `Win32_PerfFormattedData_*` |
| IIS | Microsoft.Web.Administration | **P0 spike**: confirm a .NET 8-compatible build and `ServerManager(readOnly: true, path)` |
| .NET counters | Microsoft.Diagnostics.NETCore.Client | optional, local, off by default |
| Storage | Microsoft.Data.Sqlite 8.x | WAL mode |
| Compression | System.IO.Compression (Brotli) | **instead of zstd** – zero extra dependency; see §10 |
| Templates | Scriban 5.x | sandboxed: no `import`, no file includes outside embedded resources |
| PDF | WebView2 `PrintToPdf` (decision Q1) | fallback: open HTML and print |
| Logging | Serilog + Serilog.Sinks.File | redaction enricher + destructuring policy |
| Tests | xUnit 2.9, FluentAssertions 6.12 (v7+ is commercial), NetArchTest.Rules, Microsoft.CodeAnalysis.CSharp (analyzer canary), Verify.Xunit (P1) | |
| Analyzers | Microsoft.CodeAnalysis.BannedApiAnalyzers 3.3.4, .NET SDK analyzers | warnings as errors |

No general-purpose Redis, HTTP, scripting or ORM library that exposes write APIs is referenced.
No Roslyn scripting / `DynamicExpresso` / `System.Linq.Dynamic` (rules must not be able to run code).

---

## 3. Runtime model and data flow

```
                       ┌──────────────────────────── RSM process ─────────────────────────────┐
 Site profile ───────► │ SiteSession (one per open site)                                      │
 (nodes, roles,        │   ├─ NodeContext × N  (host, credentials handle, capability matrix)  │
  credentials)         │   │     └─ Transports: SafeSqlExecutor │ CimQueryClient │ PdhReader  │
                       │   │                    EventLogReader │ IisReadOnlyConfig │ FileReader │
                       │   │                    RespWhitelistClient │ RabbitGetClient          │
                       │   ├─ CollectorScheduler ── per (node, collector) cadence, budgets,    │
                       │   │      │                 back-off, concurrency caps, cancellation   │
                       │   │      ▼                                                           │
                       │   │   ICollector.CollectAsync() ──► CollectorResult<T> + Status       │
                       │   │      │                                                           │
                       │   │      ├──► Live stores (ring buffers for charts, in memory)       │
                       │   │      ├──► FactStore  (flattened keys for rules)                  │
                       │   │      └──► Storage    (SQLite: samples, snapshots, events)        │
                       │   ├─ RulesEngine (on every fact update batch / on snapshot)          │
                       │   │      └──► Findings (deduplicated, with evidence + source ref)    │
                       │   ├─ SlownessInvestigation (synchronized capture → evidence scoring) │
                       │   └─ Reporting (HTML/PDF/JSON bundle, masking)                       │
                       └──────────────────────────────────────────────────────────────────────┘
                                  ▲ UI (WPF, MVVM) subscribes via IObservable / messenger
```

Principles:
- **Collectors are pure readers**: they receive transports from `NodeContext` and never construct
  connections, sockets or processes themselves (arch test, §6.6).
- **Transports are the only things that touch a server**, and each transport is implemented in
  `RSM.Safety` or in a collector project with banned-API coverage.
- **Everything is async + cancellable**; the UI never awaits a transport on the dispatcher thread.
- **Partial results are first-class**: every result carries `CollectorStatus` and a reason.

---

## 4. Core abstractions (sketch — final signatures may change)

```csharp
public enum CollectorStatusKind { Ok, Partial, Unavailable, Error, Skipped }
public sealed record CollectorStatus(CollectorStatusKind Kind, string? ReasonKey, string? Detail,
                                     Capability? MissingCapability = null);

public interface ICollector
{
    string Id { get; }                       // "sql.waits", "win.disk", "iis.pools"
    Layer Layer { get; }                     // Sql, Database, Os, Iis, Arr, Redis, Ssrs, Services, Events, Rahkaran
    CollectorCost Cost { get; }              // Cheap | Medium | Heavy (Heavy ⇒ on-demand only)
    IReadOnlySet<NodeRole> AppliesTo { get; }
    IReadOnlySet<Capability> Requires { get; }
    TimeSpan DefaultInterval { get; }
    Task<CollectorResult> CollectAsync(NodeContext node, CollectContext ctx, CancellationToken ct);
}

public sealed record CollectorResult(string CollectorId, string NodeId, DateTimeOffset CollectedAtUtc,
    TimeSpan Duration, CollectorStatus Status, object? Payload,           // typed DTO per collector
    IReadOnlyList<MetricSample> Samples, IReadOnlyList<Fact> Facts,
    IReadOnlyList<UnifiedEvent> Events);

public interface IMetricSource                 // same metric keys regardless of transport
{
    Task<IReadOnlyList<MetricSample>> ReadAsync(IReadOnlyList<MetricKey> keys, CancellationToken ct);
}
// LocalPdhSource (PerformanceCounter) | RemoteCimSource (Win32_PerfFormattedData_* / RawData + delta)

public sealed record Finding(string RuleId, Severity Severity, Layer Layer, string NodeId,
    LocalizedText Title, LocalizedText Explanation, IReadOnlyList<Evidence> Evidence,
    LocalizedText Recommendation, SourceRef Source, bool Verified, double Confidence,
    DateTimeOffset FirstSeenUtc, DateTimeOffset LastSeenUtc);

public sealed record SourceRef(SourceKind Kind, string Citation);  // StadFanni | KnowledgeBase | General
public sealed record Evidence(string FactKey, object? Value, string? Unit, DateTimeOffset AtUtc,
                              string? Threshold, string? ChartRef);
```

`Severity` = Critical / Warning / Info / OK. `Verified=false` for rules whose product-specific premise is
not in the Systemgroup docs (spec rule §0.4); the UI shows an "unverified" chip.

### 4.1 Facts

Collectors publish flattened **facts** (`key → value`, scoped by node and optional instance), e.g.
`sql.config.maxdop = 8`, `db[Rahkaran].recovery_model = "SIMPLE"`, `os.power.plan = "Balanced"`,
`iis.pool[AuxWebsite1].queue_length = 1000`. Rules only see facts, never DTOs — this keeps rules
data-driven, testable with JSON fixtures, and stable when collectors are refactored. The fact-key catalog
is generated into `docs/FACTS.md` by a test so it never drifts.

---

## 5. Scheduling and overhead control (§2.6)

| Tier | Default interval | Examples |
|---|---|---|
| Cheap counters | 5 s | PDH/CIM CPU, memory, disk latency, IIS queue, `dm_os_performance_counters` |
| DMV snapshots | 15–30 s | waits delta, requests, blocking, file stats, sessions summary |
| Heavy-ish views | 60 s+ | query stats top-N, memory clerks, tempdb usage, errorlog tail, Agent jobs |
| Inventory | on connect + 15 min | config, versions, services, IIS config, databases |
| Heavy (on-demand) | user click + warning + estimate | index physical stats (LIMITED, top-N), buffer descriptors, plan-cache XML scan, large IIS log parse |

Mechanics:
- **One in-flight run per (node, collector)**; a tick is skipped (and counted) if the previous run is
  still going.
- **Per-node SQL semaphore** (default 2) shared by all SQL collectors; **per-node CIM semaphore** (2).
- **Budgets**: each collector declares an expected duration. If it exceeds 2× budget for 3 consecutive
  runs, its interval doubles (max 8×); it returns to normal after 10 fast runs. Shown in the status bar.
- **Overhead estimate**: sum of SQL `cpu_time` of RSM's own sessions (from `dm_exec_sessions` filtered
  *to* our `program_name`, the only query that looks at our own sessions) / wall time / scheduler count,
  plus CIM/PDH call counts. Shown as "RSM overhead ≈ x %" per node; P2 acceptance gate is < 2 %.
- **Live mode pauses** when the window is minimized for > 5 min (configurable), except headless `--collect`.

---

## 6. Safety layer (defense in depth)

### 6.1 Layers

| # | Layer | Where | Catches |
|---|---|---|---|
| 1 | Banned API analyzer (compile time) | every project via `Directory.Build.props` | process start, service/IIS/WMI mutation, registry/eventlog writes, file delete/move, raw sockets/HTTP outside Safety |
| 2 | Query catalog (build time) | `RSM.Collectors.*/Queries/**/*.sql` embedded | string-built SQL (executor only accepts `QueryKey`) |
| 3 | SqlGuard (load + run time) | `RSM.Safety` | any non read-only T-SQL, even inside catalog/query packs |
| 4 | SafeSqlExecutor (run time) | `RSM.Safety` | timeouts, hardening prefix, concurrency, own-session exclusion, version gating |
| 5 | Whitelisted protocol clients | `RSM.Safety` | Redis/RabbitMQ commands outside whitelist |
| 6 | Read-only handles | IIS `ServerManager(readOnly)`, `RegistryKey.OpenSubKey(writable:false)`, `FileAccess.Read` | accidental writes through otherwise-allowed objects |
| 7 | Architecture tests | `RSM.ArchTests` | dependency direction, who may reference transports, audited exceptions pinned |
| 8 | Least-privilege accounts | `PERMISSIONS.md` (DBA runs scripts) | anything the first 7 layers miss is denied by the server |
| 9 | Read-only proof test | `RSM.Integration.Tests` | full snapshot under the least-privilege login: only "optional feature unavailable" errors allowed |

### 6.2 SqlGuard

Input: the full batch text **as it will be sent** (hardening prefix + catalog file). Parse with
`TSql160Parser(initialQuotedIdentifiers: true)`; any parse error ⇒ reject. Walk with a
`TSqlFragmentVisitor` that uses an **allow-list of statement types** and **deny-list of sub-fragments**:

Allowed top-level statements:

| Statement | Extra conditions |
|---|---|
| `SelectStatement` (incl. `WITH` CTE) | `Into == null`; no `ForClause` other than `FOR XML`/`FOR JSON`; no locking hints except `NOLOCK`/`READUNCOMMITTED`/`NOWAIT`; query hints limited to `OPTION (RECOMPILE)` and `OPTION (MAXDOP n)` — everything else (incl. `QUERYTRACEON`, `USE PLAN`) rejected |
| `DeclareVariableStatement` | scalar types only; initializers must themselves pass the expression rules |
| `DeclareTableVariableStatement` | — (registers the name as a declared table variable) |
| `SetVariableStatement` | `@local = expression` only (no `CURSOR`, no `.method()` on CLR UDTs) |
| `InsertStatement` | target is a `VariableTableReference` whose name was declared **earlier in the same batch**; source is a `SelectInsertSource` or an `ExecuteInsertSource` that passes the EXEC rule |
| `SetTransactionIsolationLevelStatement` | `READ UNCOMMITTED` only |
| `PredicateSetStatement` | `NOCOUNT ON` only |
| `SetCommandStatement` | `LOCK_TIMEOUT <int>`, `DEADLOCK_PRIORITY LOW` only |
| `ExecuteStatement` / `ExecuteInsertSource` | procedure name resolves exactly to `master.sys.xp_readerrorlog` (or `sys.xp_readerrorlog` / `xp_readerrorlog` while connected to master); all args are literals or `@variables`; never `EXEC(@string)` |
| `IfStatement` / `BeginEndBlockStatement` | allowed only as containers; their children are re-checked (needed for version-gated branches inside one file — prefer separate files) |

Rejected anywhere in the tree (even inside allowed statements): `UpdateStatement`, `DeleteStatement`,
`MergeStatement`, `TruncateTableStatement`, all `Create*/Alter*/Drop*`, `Grant/Deny/Revoke`,
`DbccStatement`, `KillStatement`, `ReconfigureStatement`, `BackupStatement`, `RestoreStatement`,
`CheckpointStatement`, `ShutdownStatement`, `UseStatement`, `WaitForStatement`, `BeginTransaction`/
`Commit`/`Rollback`/`SaveTransaction`, `RaiseErrorStatement`, `PrintStatement`, `ThrowStatement`,
`DeclareCursorStatement`/`Open`/`Fetch`, `OpenRowsetTableReference`, `OpenQueryTableReference`,
`AdHocTableReference` (OPENDATASOURCE), `BulkOpenRowset`, `NextValueForExpression`, any
`SchemaObjectName` with a server identifier (four-part name), any object name starting with `#`,
`ExecuteStatement` not matching the whitelist, `SetIdentityInsert`, `SetUserStatement`,
`ExecuteAsStatement`, `RevertStatement`, `GoToStatement`, `CreateEventSession` etc.

Function / object allow-list (catalog files): objects in `sys.*`, `INFORMATION_SCHEMA.*`,
`msdb.dbo.{backupset, backupmediafamily, backupfile, sysjobs, sysjobsteps, sysjobhistory, sysjobactivity,
sysschedules, sysjobschedules, syscategories, suspect_pages, sysmaintplan_plans, sysmaintplan_subplans,
log_shipping_*}`, `ReportServer*.dbo.{ExecutionLog3, ExecutionLogStorage, Catalog, Subscriptions}`,
and built-in functions. Query packs (user `.sql` files) may additionally reference **tables and views** in
the target DB, but **no user-defined function calls** and no cross-database names.

`SqlGuard` returns a structured verdict (`Allowed` | `Rejected{rule, line, column, fragment}`) so the
UI can show why a query-pack file was refused. It is **fail-closed** for statements and table sources: any
statement type or table-source type not on the allow-lists is rejected, so ScriptDom upgrades that introduce
new node types fail tests until reviewed. Expression-level dangers (`NEXT VALUE FOR`, XML `modify()`, user
UDFs, `OUTPUT … INTO`, `FOR UPDATE/BROWSE`, hints) are on an explicit deny-list.

*P0 rule ids:* SG001 parse error · SG002 >1 batch · SG010 statement not allowed · SG020 sequence · SG021 table
hint · SG022 query hint · SG023 OUTPUT · SG024 FOR clause · SG030 SELECT INTO · SG031 cursor · SG032 SET
@var.method · SG033 session SET · SG040/041 INSERT target/source · SG050/051 EXEC / EXEC args · SG060 undeclared
table variable · SG061 table source · SG062 TVF · SG063 object allow-list · SG064 four-part name · SG065 temp
table · SG066 XML modify · SG067 UDF.

Malicious corpus (`tests/RSM.Safety.Tests/Corpus/*.sql`, each with expected rejection rule): `; DROP`,
`EXEC('…')`, `EXEC sp_executesql`, `INSERT/UPDATE/DELETE/MERGE` into real tables, `SELECT INTO`,
`#temp`, `xp_cmdshell`, `OPENROWSET`, `OPENQUERY`, linked-server name, `WAITFOR` + DML,
`/* */`-comment-hidden statements, `--` line hiding, string-literal tricks, unicode homoglyph `ЕXEC`
(parse fails ⇒ reject), `NEXT VALUE FOR`, `RAISERROR … WITH LOG`, `UPDLOCK`/`TABLOCKX` hints,
`BEGIN TRAN`, `DBCC`, `SET CONTEXT_INFO`, `USE`, `EXECUTE AS`, cursor, `GO` separators, nested
`IF … BEGIN DELETE … END`, `INSERT @t EXEC master..xp_cmdshell`, `INSERT INTO @undeclared …`, etc.

### 6.3 Query catalog and executor

Catalog file format (`Queries/sql/waits/wait_stats.sql`):

```sql
-- key:         sql.waits.wait_stats
-- purpose:     Cumulative wait stats (deltas computed client-side), benign waits excluded
-- min_version: 13.0          -- SQL Server 2016
-- permission:  VIEW SERVER STATE (2022: VIEW SERVER PERFORMANCE STATE)
-- cost:        cheap
-- timeout_s:   10
-- database:    master          -- or: each-user-db | rahkaran-db | msdb | reportserver
SELECT ...
```

- Header is parsed and validated by a test (all fields present, `key` unique, `min_version` parseable).
- Version-specific variants use suffixes: `x.sql`, `x.v14.sql` (≥ 2017), `x.v16.sql` (≥ 2022); the
  executor picks the highest variant ≤ server major version.
- `SafeSqlExecutor.QueryAsync<T>(NodeContext, QueryKey, object? parameters, CancellationToken)`:
  1. Resolve file from catalog (embedded resources, loaded and **guarded once at startup**; a failing
     file is a startup error in Debug and a disabled collector + log in Release).
  2. Version gate → `Unavailable("requires SQL Server 2017+")`.
  3. Acquire node SQL semaphore.
  4. Open pooled connection with the hardened connection string (`Application Name`,
     `ApplicationIntent=ReadOnly`, `Encrypt`, `TrustServerCertificate`, `Connect Timeout=5`,
     `Initial Catalog` per the header's `database`).
  5. `CommandText = HardeningPrefix + file`; re-run SqlGuard on the exact text (cheap, cached by hash).
  6. `CommandTimeout` from header/settings; Dapper maps to DTO; permission errors (229/297/300/15247/
     916/4060) map to `Unavailable(capability)`; timeouts to `Partial`/back-off.
- Only scalar parameters are accepted: Dapper expands enumerable parameters by rewriting the SQL text, which
  would bypass SqlGuard (`SafeSqlExecutor.ValidateParameters`).
- Per-database queries never use `USE` or `sp_MSforeachdb`; the executor opens a connection with
  `Initial Catalog=<db>` for each target database (names come from `sys.databases`, passed as connection
  property, never concatenated into SQL).
- Note: `ApplicationIntent=ReadOnly` only influences AG read-only routing; it is **not** a security
  boundary on a standalone instance. The boundary is layers 1–4 + least privilege.

### 6.4 Audited exceptions (the only places allowed to touch "dangerous" APIs)

Located in `src/RSM.Safety/Audited/`, each class ≤ ~100 lines, reviewed line by line, pinned by an
arch test that fails if any other type uses the API or if the class grows a new public member.

| Class | API | Why | Constraint |
|---|---|---|---|
| `ProcessSnapshot` | `Process.GetProcesses()`, `GetProcessById()` (read members) | top processes, w3wp→pool mapping | no `Start`, `Kill`, `CloseMainWindow`, priority/affinity setters |
| `RemoteRegistryReader` *(P2, decision Q3)* | CIM `StdRegProv` — `GetStringValue`, `GetDWORDValue`, `GetMultiStringValue`, `EnumKey`, `EnumValues` only | power-plan fallback, pending reboot, `Win32PrioritySeparation`, Defender exclusions | hard-coded key/value whitelist; the only file allowed to contain `#pragma warning disable RS0030` (for `InvokeMethod`) |

Decision Q2: there is **no** `SelfElevation` or `ShellOpen` class — `Process.Start` has zero call sites. *P0:* the
`Audited/` folder contains only a README; `RSM.ArchTests` pins the (empty) type list.

### 6.5 Banned symbols (`BannedSymbols.txt` + `BannedSymbols.NonSafety.txt`)

*P0:* the full lists live in the two files at the repo root. `BannedSymbols.txt` applies to **every** production
project including `RSM.Safety` (process start/kill, PowerShell, service control, IIS mutation, CIM methods,
registry writes, classic `EventLog`, log clearing/export, legacy SQL clients, WebClient/WebRequest/SmtpClient).
`BannedSymbols.NonSafety.txt` applies to every project except `RSM.Safety` (the whole `Process` type,
`SqlConnection`/`SqlCommand`/Dapper, sockets, `HttpClient`, `File`/`Directory`/`FileStream`, mutating
`FileInfo`/`DirectoryInfo` members, `ZipFile`). The analyzer canary test also checks that every entry whose declaring
type is available resolves to a real API (a signature typo would otherwise ban nothing). Original sketch:

```
M:System.Diagnostics.Process.Start(System.String);Process start is forbidden (read-only tool)
M:System.Diagnostics.Process.Start(System.Diagnostics.ProcessStartInfo);...
M:System.Diagnostics.Process.Start(System.String,System.String);...
M:System.Diagnostics.Process.Start(System.String,System.Collections.Generic.IEnumerable{System.String});...
M:System.Diagnostics.Process.Start;...
M:System.Diagnostics.Process.Kill;... (+ Kill(bool), CloseMainWindow, set_PriorityClass, set_ProcessorAffinity)
T:System.Management.Automation.PowerShell;...
T:System.Management.Automation.Runspaces.RunspaceFactory;...
M:System.ServiceProcess.ServiceController.Start;... (+ Start(string[]), Stop, Stop(bool), Pause, Continue, ExecuteCommand(int))
M:Microsoft.Web.Administration.ServerManager.CommitChanges;...
M:Microsoft.Web.Administration.ApplicationPool.Start;... (+ Stop, Recycle)
M:Microsoft.Web.Administration.Site.Start;... (+ Stop)
M:Microsoft.Web.Administration.ConfigurationMethodInstance.Execute;...
M:Microsoft.Web.Administration.ConfigurationElement.SetAttributeValue(System.String,System.Object);...
M:Microsoft.Management.Infrastructure.CimSession.InvokeMethod(...);... (all overloads + Async)
M:Microsoft.Management.Infrastructure.CimSession.CreateInstance(...);... (+ ModifyInstance, DeleteInstance, *Async)
M:System.Management.ManagementObject.InvokeMethod(...);... (+ Put, Delete, ManagementClass.CreateInstance)
M:Microsoft.Win32.RegistryKey.SetValue(...);... (+ DeleteValue, CreateSubKey*, DeleteSubKey*, DeleteSubKeyTree, SetAccessControl)
M:Microsoft.Win32.RegistryKey.OpenSubKey(System.String,System.Boolean);use OpenSubKey(name) (read-only)
M:System.Diagnostics.EventLog.CreateEventSource(...);... (+ WriteEntry*, WriteEvent, Clear, Delete, DeleteEventSource)
M:System.Diagnostics.Eventing.Reader.EventLogSession.ClearLog(...);... (+ ExportLog*)
M:System.IO.File.Delete(System.String);... (+ Move, Replace, WriteAll*, AppendAll*, Create, Open w/ write modes via IDataFolder only)
M:System.IO.Directory.Delete(...);... (+ Move, CreateDirectory outside IDataFolder)
M:System.IO.FileInfo.Delete;... (+ MoveTo, Replace, Create, OpenWrite, AppendText, CreateText)
T:System.Net.Sockets.Socket;use RSM.Safety.RespWhitelistClient
T:System.Net.Sockets.TcpClient;use RSM.Safety.RespWhitelistClient
T:System.Net.Http.HttpClient;use RSM.Safety.RabbitGetClient
T:System.Net.WebClient;forbidden
T:System.Net.WebRequest;forbidden
M:Microsoft.Data.SqlClient.SqlCommand.#ctor(System.String,...);use SafeSqlExecutor
M:Microsoft.Data.SqlClient.SqlCommand.set_CommandText(System.String);use SafeSqlExecutor
M:Dapper.SqlMapper.Execute(...);use SafeSqlExecutor (+ ExecuteAsync, ExecuteScalar*, Query* on raw strings)
```

Only `RSM.Safety` (the single safety boundary, `RsmSafetyBoundary=true`) is exempt from the NonSafety list,
because it owns sockets/HTTP/SQL commands and the data-folder writer; Storage and Reporting write only through
`IDataFolder`. An arch test asserts no other project sets that property. **P0 acceptance:** adding
`Process.Start("cmd")` to a collector fails the build (`build/analyzer-canary.sh`).

### 6.6 Architecture tests (NetArchTest)

- `RSM.Collectors.*` must not depend on `Microsoft.Data.SqlClient.SqlConnection/SqlCommand`,
  `System.Net.Sockets`, `System.Net.Http`, `System.Diagnostics.Process` (except via `ProcessSnapshot`),
  `System.Management.Automation`, `Microsoft.Win32.Registry` (except via `RemoteRegistryReader`).
- Only `RSM.Safety` may reference `Microsoft.SqlServer.TransactSql.ScriptDom` and own socket/HTTP types.
- `RSM.Core` depends on nothing but BCL + `Microsoft.Extensions.*.Abstractions`.
- `RSM.App` may not reference collectors directly (only via `RSM.Hosting` interfaces).
- *P0 additions:* no RS0030 suppression outside `Audited/` (source scan) or in any build file; every `src` project
  inherits the analyzer; `.sql` files only under `Queries/`; no C# string literal shaped like a T-SQL statement;
  no XAML command whose name suggests a server action (kill/recycle/restart/stop/start/shrink/rebuild/execute…).
- Types in `RSM.Safety.Audited` are exactly the documented list; public surface snapshot-tested.
- No type outside `RSM.Storage`/`RSM.Reporting` implements or consumes `IDataFolderWriter`.

### 6.7 Read-only proof (integration)

`RSM.Integration.Tests.ReadOnlyProof`: creates nothing on the server itself — the CI job's setup script
(run as the DBA role, outside RSM) creates `rsm_monitor` from `PERMISSIONS.md`. The test runs a full
snapshot with **every** collector + every heavy on-demand operation, then asserts:
(a) the only SQL errors are in the "optional feature unavailable" allow-list (e.g. `xp_readerrorlog`
denied); (b) `sys.dm_exec_sessions`/XE `sql_batch_completed` captured by the *harness* (not RSM) shows only
statements that SqlGuard accepts; (c) `msdb`/`ReportServer` row counts and `sys.objects.modify_date` max
are unchanged before/after.

---

## 7. Transports and remote access

| Data | Local (RSM runs on the server) | Remote (RSM on a workstation) | Typical requirement |
|---|---|---|---|
| SQL | SqlClient | SqlClient | `rsm_monitor` login (§2.4) |
| Perf counters | `PerformanceCounter` (PDH) | CIM `Win32_PerfRawData_*` (delta math in RSM) / `Win32_PerfFormattedData_*` | Performance Monitor Users; WinRM (5985/5986) or DCOM |
| Inventory | CIM local | CIM over WinRM (fallback DCOM) | Remote WMI read on `root\cimv2` |
| Power plan | CIM `root\cimv2\power` | same | may be access-denied remotely → registry fallback (Q3) |
| Event logs | `EventLogReader` | `EventLogSession(host, domain, user, pwd)` | Event Log Readers + "Remote Event Log Management" firewall rule |
| Services | `ServiceController` (read) | `ServiceController(name, machine)` (read) / CIM `Win32_Service` | SCM connect |
| IIS config | `ServerManager(readOnly:true, applicationHost.config path)` | `ServerManager.OpenRemote` (admin, DCOM) **or** CIM `root\WebAdministration` | local admin for applicationHost.config |
| IIS live requests | `WorkerProcess.GetRequests` | not supported remotely → "run RSM locally" hint | admin + Request Monitor feature |
| IIS/HTTPERR/ANCM logs | file read | admin share `\\host\c$\inetpub\logs\...` | admin share access |
| SQL ERRORLOG fallback | file read | admin share | file ACL on `MSSQL\Log` |
| Redis | `RespWhitelistClient` | same | network + AUTH password |
| RabbitMQ | `RabbitGetClient` | same | management plugin user (monitoring tag) |
| .NET runtime counters | `DiagnosticsClient` (EventPipe, same machine only) | ✗ | same user as w3wp or admin |

**Remote mode vs Local-per-server mode** (§6): in local-per-server mode the user runs RSM on each server,
clicks *Export bundle*, and later *Import & merge* bundles into one site in another RSM. Bundles carry the
node's capability matrix so the merged report explains gaps per node.

---

## 8. Capability Matrix

`CapabilityProbe` runs on *Test connection* and every reconnect. Each capability has: id, layer, probe
(a cheap read), result (Available / Denied / NotInstalled / Unreachable / NeedsElevation / NotApplicable),
reason (fa/en) and remediation text (e.g. "add the login to … — see PERMISSIONS.md §3"). Examples:

| Capability | Probe |
|---|---|
| `sql.connect` | open connection, `SELECT @@VERSION` |
| `sql.view_server_state` | `HAS_PERMS_BY_NAME(NULL, NULL, 'VIEW SERVER STATE')` (+ 2022 split perms) |
| `sql.view_any_definition` | `HAS_PERMS_BY_NAME(NULL,NULL,'VIEW ANY DEFINITION')` |
| `sql.connect_any_database` | `HAS_PERMS_BY_NAME(NULL,NULL,'CONNECT ANY DATABASE')`, fallback per-DB `HAS_DBACCESS` |
| `sql.msdb_read` | `HAS_PERMS_BY_NAME('msdb.dbo.backupset','OBJECT','SELECT')` |
| `sql.agent_read` | `IS_ROLEMEMBER('SQLAgentReaderRole')` in msdb or sysadmin |
| `sql.errorlog` | `HAS_PERMS_BY_NAME('master.sys.xp_readerrorlog','OBJECT','EXECUTE')` + fallback file probe |
| `sql.ssrs_log` | `HAS_PERMS_BY_NAME('ReportServer.dbo.ExecutionLog3','OBJECT','SELECT')` |
| `sql.sysadmin` | `IS_SRVROLEMEMBER('sysadmin')` → info banner, not a blocker |
| `win.cim`, `win.perf`, `win.eventlog`, `win.services`, `win.registry` | one trivial read each |
| `iis.config`, `iis.requests`, `iis.logs`, `iis.httperr` | config open / `GetRequests(0)` / dir listing |
| `redis.ping`, `redis.info`, `redis.slowlog`, `redis.clientlist` | each whitelisted command |
| `rabbit.api` | `GET /api/overview` |
| `dotnet.eventpipe` | `DiagnosticsClient.GetPublishedProcesses()` contains target PID |
| `rsm.elevated` | current token elevated? |

---

## 9. Rules engine

### 9.1 Rule format (`rules/*.json`)

```json
{
  "id": "SQL-CFG-003",
  "layer": "Sql",
  "appliesTo": ["Data", "Report"],
  "when": "sql.config.maxdop != 4",
  "severity": "Warning",
  "escalate": [ { "when": "sql.config.maxdop == 0 && sql.cpu.logical > 8", "severity": "Warning" } ],
  "title":       { "fa": "MAXDOP مطابق استاندارد نیست", "en": "MAXDOP differs from baseline" },
  "explanation": { "fa": "…", "en": "…" },
  "recommendation": { "fa": "…", "en": "…" },
  "evidence": ["sql.config.maxdop", "sql.cpu.logical", "sql.numa.nodes"],
  "source": { "kind": "StadFanni", "citation": "SQL Server Tuning V.3 — Instance settings" },
  "verified": true,
  "confidence": 0.9,
  "params": { "expected": 4 }
}
```

### 9.2 Expression language

A tiny **in-house** evaluator (hand-written Pratt parser, ~500 LOC, 100 % unit-tested):
literals, `+ - * / %`, comparisons, `&& || !`, `in [..]`, `?:`, null-safe navigation, and a fixed
function set: `pct(a,b)`, `coalesce`, `abs`, `min/max`, `any(coll, expr)`, `all`, `count`, `sum`,
`avg`, `p95`, `exists(key)`, `matches(str, regex)` (regex with timeout), `daysSince(ts)`,
`hoursSince(ts)`, `version_ge(v, "13.0.5026")`. Variables resolve only against the fact store and the
rule's `params`. No reflection, no method calls, no I/O — rules cannot execute anything.

Missing facts make a condition **indeterminate** (not false): the finding is suppressed and the rule is
listed under "could not evaluate (missing: x)" in the report appendix.

### 9.3 Lifecycle

Findings are keyed by `(ruleId, nodeId, instance)`; repeated evaluations update `LastSeen`; a finding
clears after its condition is false for N evaluations (hysteresis, default 2). History is persisted in
`findings_history`.

---

## 10. Storage (SQLite, `data/rsm.db`, WAL)

```sql
sites(id, name, created_utc, profile_json)                     -- secrets NOT here (DPAPI blobs in profile store)
nodes(id, site_id, name, host, roles, settings_json)
metric_keys(id, key, unit, kind)                               -- interned keys
metric_samples_raw(ts, node_id, key_id, inst_id, value)        -- 24 h
metric_samples_1m (ts, node_id, key_id, inst_id, avg, min, max, n)   -- 7 d
metric_samples_15m(ts, node_id, key_id, inst_id, avg, min, max, n)   -- 90 d
snapshots(id, ts, node_id, kind, schema_ver, payload_br)        -- Brotli JSON (waits, top queries, requests, IIS summary…)
findings_history(id, rule_id, node_id, instance, severity, first_ts, last_ts, evidence_json, cleared_ts)
events_cache(id, ts, node_id, layer, source, code, severity, message_hash, message, count, first_ts, last_ts)
baselines(node_id, key_id, inst_id, how /*hour-of-week 0..167*/, median, p95, n, weeks, computed_utc)
investigations(id, ts, site_id, duration_s, result_json)
```

- Downsampling job runs every 5 min (raw→1m) and hourly (1m→15m); retention configurable.
- Baseline job daily: median & p95 per hour-of-week over the last N weeks (default 4); anomaly =
  value > p95 × 1.5 for ≥ 3 consecutive samples (configurable).
- Week definition uses the **Iranian week (Saturday-first)** for display; hour-of-week math is
  calendar-neutral (local time).
- zstd → Brotli: the spec suggests zstd; Brotli from the BCL gives comparable ratios for JSON with no
  native dependency inside a single-file exe. Change back if you prefer (trivial).

Data folder: portable mode if `./data` exists next to the exe or `--portable`; else `%LOCALAPPDATA%\RSM`.
`IDataFolder` is the only component allowed to create/write/delete files.

---

## 11. Slowness investigation (§12)

1. **Capture** (default 120 s): the scheduler switches every applicable collector on every node to a
   synchronized "investigation cadence" (5 s cheap, 15 s DMV) with a shared `CaptureId`, plus one-shot
   IIS log tail (last 15 min) and `dm_exec_query_stats` before/after.
2. **Evidence extraction**: a set of `IEvidenceDetector`s turn samples into signals with strength 0..1,
   e.g. `HostCpuSaturated(non-sql)`, `SqlCpuHighSignalWait`, `PowerThrottling`, `MemoryPressureOs`,
   `MemoryGrantsPending`, `PleDrop`, `GcPressure`, `DiskLatency(volume, role)`, `WritelogSlowLog`,
   `BlockingChain(head)`, `ExpensiveRunningQuery`, `PlanRegression(QS)`, `AsyncNetworkIo`,
   `IisQueue`, `ThreadPoolStarvation`, `RecentRecycleOrRapidFail`, `Http503Burst`, `ArrImbalance`,
   `ArrNodeUnhealthy`, `RedisEvictionsOrLatency`, `RedisRestart`, `NetworkSaturation`, `SqlRttHigh`.
3. **Cause scoring**: causes (JSON, `rules/slowness/*.json`) combine detectors with weights and
   exclusions (e.g. `AsyncNetworkIo` + high w3wp CPU/GC ⇒ "app tier slow consuming results", boosted;
   `AsyncNetworkIo` alone ⇒ "client/network" lower confidence). Confidence = calibrated weighted sum,
   capped; each cause lists its evidence with timestamps and chart refs.
4. **Output**: ranked causes, shared-timeline charts (all nodes, same X axis), fa/en explanation,
   **text-only** next steps (e.g. ProcDump guidance per KB — never executed by RSM). Saved as an
   `investigations` row and exportable as a report section.

Lab validation (P5): scripted scenarios (CPU burn, blocking pair, disk latency via DiskSpd on a lab
volume, memory pressure, IIS queue via slow endpoint + low queue length) — the scripts live in
`tests/lab/` and are run by a human on a lab box, **never by RSM**.

---

## 12. Unified error timeline (§13)

All sources emit `UnifiedEvent { ts, nodeId, layer, source, code, severity, message, kbKey, count }`.
Grouping key = (node, source, code, normalized message hash) in a time bucket. KB lookup by
`source+code` (`kb/events.json`, `kb/sql-errors.json`, `kb/waits.json`, `kb/http.json`). Correlation view:
pick a spike on any chart ⇒ events ±5 min across all nodes.

---

## 13. Reporting

- **HTML**: Scriban templates embedded as resources; one template set with `dir="rtl|ltr"`; inline CSS,
  inline SVG charts rendered server-side by our own small SVG chart builder (no JS needed for viewing;
  optional tiny inline JS for collapsible sections). Vazirmatn embedded as base64 `@font-face`.
- **PDF** (decision Q1): WebView2 `PrintToPdfAsync` of the same HTML (identical look). The WebView2 Runtime is
  often absent on Server 2016/2019 → fallback "open the HTML and print to PDF".
- **JSON bundle** (`RSM_<site>_<yyyyMMdd-HHmm>.zip`): `manifest.json` (RSM version, schema version, nodes,
  capability matrices, masking flag), `results/<node>/<collector>.json`, `findings.json`, `events.json`,
  optional `samples.jsonl`. Importable/mergeable.
- **Masking** (`IMasker`): deterministic per-export pseudonyms (`SRV-1`, `DB-2`, `10.x.x.7`, `LOGIN-3`),
  query literals replaced via ScriptDom token stream (strings/numbers → `?`), applied to *all* outputs.
- Snapshot tests (Verify) for fa and en HTML with a fixed fixture bundle.

---

## 14. UI

- Shell: left nav (§14 list), top bar (site picker, node filter, live/pause, interval, language, theme),
  status bar (per-node connection dots, collector errors, overhead estimate).
- MVVM with CommunityToolkit; each page VM subscribes to live stores via `IObservable` throttled to the
  dispatcher at ≤ 4 Hz.
- RTL: `FlowDirection` bound at the window root; charts, code views, IPs, SQL text and numbers in grids
  explicitly `LeftToRight`. Jalali via `PersianCalendar`; optional Persian digits via a value converter
  (display only — copy/CSV export use Latin digits).
- Every metric/finding has a tooltip from `kb/metrics.json`: what / normal range / why it matters for
  Rahkaran / source.
- **No action buttons on servers** (kill/recycle/restart/shrink/rebuild), not even disabled.

---

## 15. Credentials, secrets, logging

- Site profiles in `data/sites/<id>.json` (human-readable); secrets in `data/secrets/<id>.bin`
  (DPAPI CurrentUser, entropy per site) or not stored ("don't remember").
- Serilog file sink in `data/logs`, rolling 10 × 10 MB; `RedactionEnricher` + destructuring policy
  scrub `Password=`, `Pwd=`, `User ID=` in connection strings, `AUTH` args, `Authorization` headers, and
  any property named like `password|pwd|secret|token|key`. A unit test logs a fake secret and asserts it
  never reaches the sink.
- SQL encryption options per connection: `Encrypt=Mandatory|Optional|Strict`,
  `TrustServerCertificate` with a visible warning.

---

## 16. CI / build

- GitHub Actions (`.github/workflows/ci.yml`, P0): `windows-latest` job: build all, run all tests, publish single-file exe as an
  artifact. `ubuntu-latest` job (fast): `RSM.Core`, `RSM.Safety`, `RSM.Parsers`, `RSM.Storage` tests.
  Optional integration job with a SQL Server container (Linux) for the SQL collectors + read-only proof.
- Every PR runs the safety tests; the analyzer runs in every build.

---

## 17. Risks and early spikes (P0)

| Risk | Spike / mitigation |
|---|---|
| WPF-UI RTL rendering issues | *P0:* shell built with plain styled WPF; WPF-UI only after a Windows RTL check |
| Microsoft.Web.Administration on .NET 8 + read-only ctor | spike moved to P3 (needs Windows + IIS); fallback to parsing `applicationHost.config` XML read-only + CIM `root\WebAdministration` |
| ARR runtime counters not exposed read-only | config-only + distribution from IIS logs (spec fallback) |
| Remote registry reads require WMI method calls | decision Q3: one audited `StdRegProv` reader (P2) |
| `xp_readerrorlog` permission varies by version | capability probe + file fallback + documented in PERMISSIONS.md as "verify" |
| ScriptDom fail-closed rejects legitimate DMV queries | catalog test catches it on every build |
| Overhead on busy 64-core servers | budgets, back-off, investigation-only fast cadence, measured in P2 gate |
| PDF licensing | decision Q1: WebView2 print |
