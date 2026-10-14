# PLAN — phased delivery

Status: **draft for approval**. Spec: `docs/SPEC.md` §18. Each phase ends with: green build
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
8. Spike: Microsoft.Web.Administration on .NET 8 with read-only `ServerManager` (decision recorded).
9. Tests: `RSM.Safety.Tests` (malicious corpus ≥ 60 cases, catalog read-only, RESP whitelist, redaction),
   `RSM.ArchTests` (rules in `ARCHITECTURE.md` §6.6), analyzer canary.
10. `PERMISSIONS.md` (DBA script + Windows groups), `README.en.md`, `README.fa.md` (short).
11. CI workflow (Linux fast job + Windows full job) — pending Q9.

**Acceptance**
- [ ] Every malicious corpus sample rejected with the expected rule id; every catalog file accepted.
- [ ] **Analyzer canary**: a test project / CI step that adds `Process.Start("cmd")` to a collector fails
      the build with RS0030 (verified in CI, file not committed to `src/`).
- [ ] Arch tests green; audited-exception list matches `ARCHITECTURE.md` §6.4.
- [ ] Redis client refuses `SET`, `FLUSHALL`, `CONFIG SET`, `EVAL`, pipelined injection (`\r\n` in args).
- [ ] Logging a connection string with a password never puts the password in the log file.
- [ ] *Test connection* against a SQL Server with the `rsm_monitor` login shows a correct Capability
      Matrix (and a sysadmin banner when using sysadmin).
- [ ] App starts in fa (RTL) and en (LTR) and switches at runtime.

## Phase 1 — SQL & DB health + report v1

**Scope** — §7 items 1–2, 12–17; §8 database module (incl. backups, VLFs, CDC, integrity, top tables,
stats; index physical stats on demand); rules from `RULES.md` §2 (config parts), §3.1–3.4, §4; Rahkaran
DB auto-detect + override; snapshot runner; HTML report v1 (Scriban, fa + en, masking); JSON bundle v1;
`known-builds.json` initial content; SQLite storage for snapshots & findings.

**Acceptance**
- [ ] Snapshot against a test instance seeded with deliberate deviations (MAXDOP 0, max memory default,
      ad hoc off, percent growth, SIMPLE recovery on "Rahkaran" DB, no log backups, auto_shrink on,
      page_verify NONE, shrink job step) produces exactly the expected findings — fixture-verified.
- [ ] Compliance table vs ستاد فنی baseline renders with expected/actual/status/source per row.
- [ ] Least-privilege login: all P1 collectors OK or `Unavailable` with a correct reason (no crash).
- [ ] HTML report renders in fa (RTL, Jalali + Gregorian) and en; Verify snapshot tests pass; masked
      export contains no server/DB/login/host names or query literals.
- [ ] Errorlog parser unit tests cover every classified error number in §7.15.

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

## Defaults I will use unless you say otherwise

| Topic | Default |
|---|---|
| Snapshot compression | Brotli (BCL) instead of zstd — no native dependency in the single-file exe |
| Rules expression language | small in-house evaluator (no scripting engines that can run code) |
| Extra projects | `RSM.Parsers` (Linux-testable) and `RSM.Hosting` (shared DI for App + CLI) |
| Working hours (2 h log-backup check) | Sat–Wed 08:00–17:00, Thu 08:00–13:00 local time (setting) |
| "Large" DB (log 10 GB guideline, diff + weekly full allowed) | ≥ 200 GB data (setting) |
| Rahkaran DB detection | largest online user DB with the most user sessions; user can override |
| Persian digits | off by default; Jalali + Gregorian dates both shown |

## Open questions (need your decision)

**Q1 — PDF engine.** QuestPDF's free Community license applies only to organisations under USD 1M annual
revenue; a company of Systemgroup's/your employer's size likely needs a paid license. Options:
(a) QuestPDF with a license you confirm; (b) WebView2 `PrintToPdf` of the same HTML (identical look, but
the WebView2 Runtime is often missing on Server 2016/2019 → fallback to "open HTML and print");
(c) HTML only in v1. *My recommendation: (b) with fallback.*

**Q2 — `Process.Start` exceptions.** The spec bans `Process.Start` but also asks for "relaunch RSM
elevated" (needs `Process.Start(self, runas)`) and an "open exported report" button implies a shell open.
Allow two tiny audited classes (`SelfElevation`, `ShellOpen`, restricted to RSM's own exe and files in its
data folder), or keep **zero** `Process.Start` and just show "right-click → Run as administrator" + a
copyable report path? *My recommendation: zero — simplest proof story.*

**Q3 — Remote registry reads.** Some checks (power-plan fallback, pending reboot, processor scheduling,
Defender exclusions) read the registry. Remotely that needs either WMI `StdRegProv.GetStringValue/…`
(technically a WMI **method call**, which §2.1 bans) or the Remote Registry service (`OpenRemoteBaseKey`,
often disabled). Options: (a) allow only `StdRegProv` *Get/Enum* methods in one audited class with a
hard-coded key whitelist; (b) Remote Registry service only; (c) registry checks local-only, remote shows
"run RSM locally for this check". *My recommendation: (a), or (c) if you want the ban absolute.*

**Q4 — Thresholds not covered by the docs.** (i) Max server memory on a **dedicated** DB server (TV3 gives
65 % only for shared App+SQL boxes) — use GEN "leave max(4 GB, 10 %) to OS"? (ii) What size makes a
customer "large" (log 10 240 MB, daily diff + weekly full)? (iii) Working days/hours for the 2-hour
log-backup check. Defaults are in the table above.

**Q5 — Citations.** Can you share the ستاد فنی documents (SQL Server Tuning V.3, log-growth KB, ARR KB,
GC/StackOverflow KB, backup KBs, HPE prep) or at least their section numbers/IDs? Without them I cite by
document name + section title only.

**Q6 — Product-specific names.** Job-name patterns of the «ابزار پایگاه داده» (ستاد فنی DB tool), the
attendance service name, SGProactiveMonitor, and where the Rahkaran build/version can be read. Until you
confirm, these stay as empty, *unverified* settings and the dependent rules (marked ⚠U) don't fire.

**Q7 — Narrower msdb permissions.** `db_datareader` on msdb (spec §2.4) also exposes everything in msdb
(e.g. job-step commands that may contain credentials, SSIS packages, mail profiles). I propose
`GRANT SELECT` on the specific msdb tables listed in `SQL_CATALOG.md` instead, keeping
`SQLAgentReaderRole`. OK?

**Q8 — EventPipe on the Rahkaran process.** Attaching a diagnostics session to a production `w3wp`
enables event providers inside that process (small overhead, no state change, but it is an action on the
process). Keep it in v1 (off by default, local only, explicit warning) or postpone?

**Q9 — CI.** OK to add GitHub Actions workflows (Linux fast tests + `windows-latest` build/test/publish,
optional SQL Server container for integration tests)?

**Q10 — Repository language for docs.** Planning docs are in English with Persian terms; `README.fa.md`
and all UI/report texts will be Persian + English. Fine?
