# PLAN — phased delivery

Status: **approved** (all open questions answered with the recommendations, see *Decisions*). Phase 0 delivered — see `docs/phase-notes/P0.md`. Spec: `docs/SPEC.md` §18. Each phase ends with: green build
(`-warnaserror`), all tests green, demo notes in `docs/phase-notes/P<n>.md`, one commit/PR. **The next phase
starts only after explicit approval.**

## Verification environment (important)

- This planning session runs in a Linux container. From Phase 0 on, the .NET 8 SDK can be installed here
  and WPF projects compile with `EnableWindowsTargeting=true`; **all pure-library tests** (SqlGuard,
  catalog, parsers, rules engine, storage, reporting) run on Linux.
- Anything that needs Windows at **run time** (WPF UI, PDH, CIM, Event Log, IIS, single-file exe smoke
  test) is verified by a `windows-latest` GitHub Actions job (Q9) and by you on a lab server. Demo notes
  will say exactly which checks were run where.
- SQL collectors are verified against SQL Server 2019/2022 Linux containers in CI (DMVs used are the same;
  Windows-only DMV columns are flagged).

---

## Phase 0 — Foundation & safety

**Scope**
1. Solution skeleton (`ARCHITECTURE.md` §2), `Directory.Build.props`, central package management
   (`Directory.Packages.props`), `.editorconfig`, `BannedSymbols.txt`, analyzers, warnings-as-errors.
2. `RSM.Safety`: `SqlGuard` (ScriptDom `TSql160Parser`, fail-closed visitor), `QueryCatalog` (embedded
   resources + header parser + version variants), `SafeSqlExecutor` (hardening prefix, timeouts,
   semaphore, permission-error mapping), `RespWhitelistClient` (RESP2 encoder/decoder + whitelist),
   `RabbitGetClient` (GET-only, 3 paths), `ReadOnlyFile` helpers, `IDataFolder`.
3. `RSM.Core`: models (`ICollector`, `CollectorResult`, `Finding`, `Fact`, `Capability`), scheduler
   skeleton, fact store, rules-engine skeleton + expression evaluator.
4. Site/node profiles (JSON) + DPAPI secret store + "don't remember".
5. Serilog with redaction enricher.
6. `CapabilityProbe` for SQL (all `cap.*` queries) + Windows/IIS/Redis probe stubs returning
   `NotImplemented` reasons.
7. WPF shell: nav, language switch (fa/en, RTL/LTR, Vazirmatn), theme switch, site editor, *Test
   connection* → Capability Matrix page. **RTL spike** for WPF-UI controls (decision recorded).
8. Spike: Microsoft.Web.Administration on .NET 8 with read-only `ServerManager` — **moved to P3** (needs Windows + IIS).
9. Tests: `RSM.Safety.Tests` (malicious corpus ≥ 60 cases, catalog read-only, RESP whitelist, redaction),
   `RSM.ArchTests` (rules in `ARCHITECTURE.md` §6.6), analyzer canary.
10. `PERMISSIONS.md` (DBA script + Windows groups), `README.en.md`, `README.fa.md` (short).
11. CI workflow (Linux job, SQL Server/Redis integration job, Windows build + publish job).

**Acceptance** (status after P0 — evidence in `docs/phase-notes/P0.md`)
- [x] Every malicious corpus sample (90 cases) rejected with the expected rule id; every catalog file accepted.
- [x] **Analyzer canary**: `build/analyzer-canary.sh` adds `Process.Start` to a collector and the build fails with RS0030;
      `RSM.ArchTests.AnalyzerCanaryTests` runs the real analyzer in-process on 21 banned snippets.
- [x] Arch tests green; audited-exception list matches `ARCHITECTURE.md` §6.4 (empty in P0).
- [x] Redis client refuses `SET`, `FLUSHALL`, `CONFIG SET`, `EVAL`, CRLF injection … (28 cases) — and a live Redis
      confirmed via `INFO commandstats` that the probe sent only whitelisted commands.
- [x] Logging a connection string / password / AUTH never puts the secret in the log file.
- [x] *Test connection* against SQL Server with the `rsm_monitor` login → CI `integration` job green on `dcda00e`
      (SQL Server 2022 container; the first run caught the msdb-database bug fixed in that commit).
- [ ] App starts in fa (RTL) / en (LTR) and switches at runtime → compiles and publishes here; **needs a run on Windows**.

## Phase 1 — SQL & DB health + report v1

**Scope** — §7 items 1–2, 12–17; §8 database module (incl. backups, VLFs, CDC, integrity, top tables,
stats; index physical stats on demand); rules from `RULES.md` §2 (config parts), §3.1–3.4, §4; Rahkaran
DB auto-detect + override; snapshot runner; HTML report v1 (Scriban, fa + en, masking); JSON bundle v1;
`known-builds.json` initial content; ~~SQLite~~ file storage for snapshots (see ARCHITECTURE §10 — SQLite is an
open owner decision before P5).

**Acceptance** (status after P1 — evidence in `docs/phase-notes/P1.md`)
- [x] Seeded deviations produce exactly the expected findings — fixture-verified (`ReportTests` pins the exact 15
      findings of the lab fact set, no indeterminate rule); the CI integration job seeds a real SQL Server 2022
      the same way and asserts the findings with the least-privilege login (result: see P1 notes / CI run).
- [x] Compliance table vs ستاد فنی baseline renders with item/expected/actual/status/rule/source per row
      (33 baseline rules; Deviation / Compliant / Unknown / Not applicable tested).
- [ ] Least-privilege login: all P1 collectors OK or `Unavailable` with a correct reason → asserted by the CI
      integration test (no collector `Error`), plus a before/after server fingerprint diff as `sa`.
- [x] HTML report renders in fa (RTL, Jalali + Gregorian) and en; Verify snapshot tests pass; masked HTML and
      bundle contain none of 16 sensitive lab tokens (names, hosts, logins, IPs, job names, payload-only data).
      (Query literals: P1 reports contain no query text; literal masking arrives with query text in P2.)
- [x] Errorlog parser unit tests cover every classified error number in §7.15.

## Phase 2 — Live SQL + Windows

**Scope** — §7 items 3–11 (counters with delta math, CPU ring buffer, waits, requests, blocking,
deadlocks, transactions, top queries, plan warnings, missing/unused indexes, Query Store); §9 Windows
module (local PDH + remote CIM, same metric keys); live dashboard (overview, SQL, queries & blocking,
Windows pages), ScottPlot charts (15/30/60 min, pause, interval selector, threshold bands), overhead
estimate, adaptive back-off; rules `RULES.md` §1, §2 live, §3.5.

**Acceptance**
- [ ] Live view stable for 1 h against a loaded lab server; RSM overhead < 2 % CPU (measured by
      `cap.self_overhead` + server-side perf counters) and RSM process < 500 MB.
- [ ] Blocking demo (two sessions) shows correct head blocker, chain, statement, open-tran age.
- [ ] Deadlock demo is read from `system_health` and parsed (victim, statements, objects, modes).
- [ ] Showplan parser flags each warning type on fixture plans.
- [ ] Remote CIM and local PDH return the same metric keys for the same counters (contract test).

## Phase 3 — IIS / ARR / services / events

**Scope** — §10 complete (sites, pools, worker processes & live requests, counters, W3C/HTTPERR/ANCM
logs, FREB listing, web.config with secret masking, ARR farms, URL Rewrite, certificates, drift across
nodes); services panel (display only); event-log collection + KB; unified Errors & Events timeline with
±5 min correlation; rules `RULES.md` §5–7.

**Acceptance**
- [ ] Live requests visible for a slow test endpoint (local, admin, Request Monitor installed).
- [ ] IIS W3C percentile computation matches a hand-verified log sample (p50/p95/p99 per URL stem).
- [ ] ARR farm + rewrite checks verified on a lab ARR (incl. "app path segment changed" case).
- [ ] web.config viewer never shows a password/secret value (fixture with every secret key pattern).
- [ ] Event KB covers every event id listed in spec §9.8.

## Phase 4 — Redis, SSRS, CodeMeter, RabbitMQ, topology, .NET counters

**Scope** — §11; topology discovery (IIS/ARR config + SQL sessions + Redis CLIENT LIST) and map;
EventPipe runtime counters (optional, local, off by default — Q8); query packs with SqlGuard query-pack
profile; rules `RULES.md` §8–10.

**Acceptance**
- [ ] Topology map correct for a 3-node lab (ARR → 2 App → Data/Redis).
- [ ] GC / threadpool counters shown for an ASP.NET Core test app under IIS (in-process and
      out-of-process).
- [ ] RESP/INFO parser and ExecutionLog aggregation unit-tested; a query pack with `UPDATE` is refused
      with a visible reason.

## Phase 5 — History, baseline, slowness wizard, PDF, bundles, polish

**Scope** — §12 slowness investigation (synchronized capture, detectors, cause scoring, shared timeline);
§15 history (downsampling, retention, baselines, anomaly flags, compare periods); PDF (Q1); bundle
import/merge; `RSM.Cli` (`--snapshot`, `--collect --duration`, `--export`, `--import`); docs (user guide
with screenshots); single-file publish.

**Acceptance**
- [ ] Wizard ranks the injected cause first in lab scenarios: CPU burn, blocking, disk latency, memory
      pressure, IIS queue (scripts in `tests/lab/`, run by a human — never by RSM).
- [ ] Baseline anomaly flags appear for a replayed metric series with a known anomaly.
- [ ] Two bundles from two nodes import & merge into one site report with per-node capability gaps.
- [ ] Release exe runs on a clean Windows Server 2019 and 2022 (no installer, no extra runtime).
- [ ] Definition-of-done checklist (spec §19) all ticked.

---

## Decisions (approved)

| # | Topic | Decision |
|---|---|---|
| Q1 | PDF engine | **WebView2 `PrintToPdf`** of the same HTML report (P5); where the WebView2 Runtime is missing (often Server 2016/2019) RSM offers "open HTML and print". No QuestPDF (licence). |
| Q2 | `Process.Start` | **Zero call sites.** No "relaunch elevated" and no "open report" shell call; the UI tells the user to start RSM with *Run as administrator* and shows a copyable report path. |
| Q3 | Remote registry | **One audited class** `RemoteRegistryReader` (P2) using WMI `StdRegProv` *Get\*/Enum\** read methods only, with a hard-coded key whitelist. Local reads use `RegistryKey.OpenSubKey(name)` (read-only). |
| Q4 | Thresholds not in the docs | Dedicated DB server: leave max(4 GB, 10 % RAM) to the OS (GEN). "Large" DB ≥ 200 GB data. Working hours Sat–Wed 08:00–17:00, Thu 08:00–13:00. All are site settings. |
| Q5 | Citations | Cite by document name + section title until the documents/section numbers are provided. |
| Q6 | Product-specific names | Empty, *unverified* settings; dependent rules (⚠U) don't fire until filled. |
| Q7 | msdb permissions | `SQLAgentReaderRole` + `GRANT SELECT` on the listed msdb tables only — **no** `db_datareader` (see `PERMISSIONS.md`). |
| Q8 | EventPipe on w3wp | Kept for v1: off by default, local only, explicit warning before attaching (P4). |
| Q9 | CI | GitHub Actions: Linux (build + all tests + analyzer canary), SQL Server 2022 + Redis integration (read-only proof with the least-privilege login), Windows (build, test, single-file publish artifact). |
| Q10 | Doc language | Planning docs in English; `README.fa.md`, UI and reports bilingual. |

Other defaults: Brotli (BCL) instead of zstd; in-house rule expression language; extra projects `RSM.Parsers` (P1) and
`RSM.Hosting`; plain styled WPF instead of WPF-UI until its RTL rendering is verified on Windows; FluentAssertions pinned to
6.12 (v7+ is commercial).
