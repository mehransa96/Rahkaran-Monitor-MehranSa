# Rahkaran Server Monitor (RSM)

A portable, **strictly read-only** Windows tool that support engineers run on (or against) a customer's Rahkaran
servers to see the health of SQL Server, databases, Windows, IIS/ARR, Redis, SSRS and services, find the cause of
slowness, and export a diagnostic report — in Persian or English.

**Status: Phase 3 (live SQL Server, Windows, IIS/ARR, services, event logs).** Define sites and servers, run *Test connection* to get the Capability
Matrix, then *Reports → Diagnose now* for a read-only health check of SQL Server, its databases and Windows against the
ستاد فنی baseline (Persian or English HTML report, JSON bundle, optional masking). *Connect live view* on the Overview,
SQL Server, Queries & Blocking or Windows page watches the servers in real time: CPU (SQL vs other processes), waits,
counters, file and disk latency, blocking chains with the head blocker, open transactions, deadlocks from
`system_health`, top queries with plan warnings, Query Store regressions, memory, disks, network and top processes —
15/30/60-minute charts with threshold lines, pause (automatic while minimised) and RSM's own overhead. The IIS page
shows sites, pools, worker processes, the requests executing right now, latency percentiles per URL from the W3C
logs, HTTP.sys errors, masked web.config and certificates; the ARR page shows farms and URL Rewrite rules (including
"application path changed after an update"); Services lists the watched services (display only); Errors & Events puts
SQL, Windows and IIS events on one timeline with ±5-minute correlation and a Persian/English knowledge base. Redis,
SSRS, history and the slowness wizard arrive in Phases 4–5 (`PLAN.md`).

## Read-only by construction

RSM cannot change, restart or execute anything on a server:

- every T-SQL statement is an embedded catalog file that passes **SqlGuard** (ScriptDom AST allow-list) before it
  can run; there is no SQL text in C# and no SQL editor;
- dangerous .NET APIs (process start, service control, IIS commit/recycle, WMI methods, registry/event-log writes,
  file deletes, raw sockets/HTTP …) are **compile errors** (banned-API analyzer, warnings as errors);
- Redis and RabbitMQ are reached only through clients that can send a fixed whitelist of read commands;
- the UI has no kill / recycle / restart / shrink / rebuild buttons — recommendations are text only;
- no internet access, telemetry or auto-update.

Use the least-privilege accounts in [`PERMISSIONS.md`](PERMISSIONS.md).

## Run

1. Download `RSM-win-x64` from the latest CI run (or build it, below) and copy `RSM.exe` anywhere — no installer.
2. Optional portable mode: create a `data` folder next to `RSM.exe` (or start with `--portable`); otherwise data
   lives in `%LOCALAPPDATA%\RSM`.
3. *Sites & Connections* → *New site* → add servers → *Test connection*.
4. For IIS features, start RSM with *Run as administrator* on that server (RSM never elevates itself).

Passwords are stored only with Windows DPAPI for the current user, or not at all ("remember" unchecked).

## Build

Requires the Microsoft-built .NET 8 SDK (8.0.1xx+). Linux can compile everything, including WPF, but the app only
runs on Windows.

```bash
dotnet build RSM.sln -c Release
dotnet test RSM.sln -c Release
./build/analyzer-canary.sh                     # proves Process.Start in a collector breaks the build
dotnet publish src/RSM.App -c Release -r win-x64 -o publish   # single self-contained RSM.exe
```

## Documentation

[`docs/SPEC.md`](docs/SPEC.md) (spec) · [`ARCHITECTURE.md`](ARCHITECTURE.md) · [`SQL_CATALOG.md`](SQL_CATALOG.md) ·
[`RULES.md`](RULES.md) · [`PLAN.md`](PLAN.md) · [`PERMISSIONS.md`](PERMISSIONS.md) · [`docs/phase-notes/`](docs/phase-notes/)

Font: [Vazirmatn](https://github.com/rastikerdar/vazirmatn), SIL Open Font License 1.1 (`src/RSM.App/Fonts/OFL-Vazirmatn.txt`).
