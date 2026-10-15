# Rahkaran Server Monitor (RSM)

A portable, **strictly read-only** Windows tool that support engineers run on (or against) a customer's Rahkaran
servers to see the health of SQL Server, databases, Windows, IIS/ARR, Redis, SSRS and services, find the cause of
slowness, and export a diagnostic report — in Persian or English.

**Status: Phase 1 (SQL Server & database health + report v1).** Define sites and servers, run *Test connection* to
get the Capability Matrix, then *Reports → Diagnose now* for a read-only health check of SQL Server and its databases
against the ستاد فنی baseline. Save it as a Persian or English HTML report or a JSON bundle, optionally with names and
addresses masked. Live monitoring pages arrive in Phases 2–5 (`PLAN.md`).

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
