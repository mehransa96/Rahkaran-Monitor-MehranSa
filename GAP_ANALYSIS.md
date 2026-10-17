# GAP_ANALYSIS — RSM against PROMPT v1 and PROMPT v2

Phase A deliverable 2 of PROMPT v2. Baseline: commit `47b1767` (phases 0–5 of PROMPT v1 + the Windows Server 2022
Standard alignment of PR #4). Status values: **Implemented** · **Partial** · **Missing** · **Buggy**. Evidence is a
file path, a test, or a phase note. The read-only compliance part is in `READONLY_AUDIT.md`.

## 1. PROMPT v1 (`docs/SPEC.md`) — what is left

Phases 0–5 are delivered and CI-green (`docs/phase-notes/P0.md` … `P5.md`, `PLAN.md`). Everything below is either
an acceptance item that needs a real server, or a v1 detail that v2 now builds on.

| SPEC § | Requirement | Status | Evidence / gap |
|---|---|---|---|
| §2 | Read-only by construction | Implemented | `READONLY_AUDIT.md` (no violation; R1–R5 to confirm) |
| §6 | Sites, nodes, roles, Capability Matrix, two modes (remote / local + bundle merge) | Implemented | `src/RSM.Core/Profiles/SiteProfile.cs`, `src/RSM.Hosting/BundleImportService.cs`, P5 notes |
| §6 | Topology map | Partial | modelled in `tests/RSM.Tests/TopologyTests.cs`; **3-node lab confirmation open** (PLAN P4) |
| §7–§8 | SQL instance and database modules | Implemented | 84 catalog queries (`SQL_CATALOG.md`), 195 rules (`RULES.md`) |
| §8.7 | Query packs (Rahkaran version, user count) | Partial | mechanism done (`src/RSM.Safety/Sql/QueryPacks.cs`); only `docs/query-packs/example-table-sizes` ships — Rahkaran-specific packs must come from the team (no guessed tables) |
| §9 | Windows module (PDH local / CIM remote) | Implemented | `src/RSM.Collectors.Windows/` |
| §10 | IIS / ARR / URL Rewrite / NUMA | Partial | fixture-verified (`IisConfigParserTests`, `IisCollectorTests`); **lab ARR incl. "app path segment changed" open** (PLAN P3) |
| §11 | Redis, SSRS, CodeMeter, RabbitMQ, .NET counters | Implemented | `src/RSM.Collectors.Redis/`, `SsrsCollector.cs`, `RabbitMqCollector.cs`, `DotNetRuntimeCollector.cs` |
| §11 | CodeMeter, attendance service names | Partial (by design) | name patterns are site settings, *unverified* (`SiteProfile.cs:27-28`) |
| §12 | "Why is Rahkaran slow?" wizard | Partial | 22 causes + 37 detectors (`rules/slowness/`), fixture-verified (`SlownessTests`); **lab run of `tests/lab` open** |
| §13 | Unified error timeline | Implemented | event KB (`src/RSM.Parsers/Events/EventKnowledgeBase.cs`), errorlog parser, deadlocks |
| §14 | Live dashboard, fa/en, RTL | Partial | CI UI tour on `windows-2022` (40 screenshots); **1-hour live soak on a loaded lab server open** (PLAN P2) |
| §15 | History, baseline, compare | Implemented | file store instead of SQLite (owner decision, ARCHITECTURE §10) |
| §16 | Snapshot report HTML/PDF/JSON, masking | Implemented | `src/RSM.Reporting/`, Verify snapshots fa/en |
| §17 | Overhead < 2 % CPU | Partial | ≤ 0.8 % of one core in the P2 CI soak; loaded-lab measurement open |
| §17 | Version gating SQL 2016–2022, IIS 8.5–10, Windows Server 2016–2022 | Partial | SQL: per-file `min_version` + `.v13sp2/.v14/.v16` variants, CI runs SQL 2022 only (2019 container not in CI). Windows: target is 2022 Standard (owner), others → OS-019 *Info*. No 2016/2017/2019 SQL **fixtures** yet (PROMPT v2 §9.3 asks for them) |

Known defects: none open in the issue tracker or the phase notes. No crash or slow screen is recorded; the lab runs
above are where such defects would show up.

## 2. PROMPT v2 — requirement by requirement

### Phase A — compliance and transparency

| Requirement | Status | Evidence / gap |
|---|---|---|
| `READONLY_AUDIT.md` | Implemented | this PR |
| `GAP_ANALYSIS.md` | Implemented | this file |
| Fix every §2 non-compliance | Implemented | none found; R1–R5 need the owner's confirmation |
| **Operations Audit log** («شفافیت عملیات»): every read recorded with time, target, duration, result; UI panel; report appendix | **Missing** | nothing records reads today. Natural hook points: `SafeSqlExecutor.QueryAsync`, `CimSessionQuery`, `PdhCounterSource`, `ReadOnlyFile`, `LocalIisSources`, `RespWhitelistClient`, `RabbitGetClient`, `RuntimeCounterSource`, `RegistryValues`, `EventLogReaderSource` |
| **Read-only self-test screen** («خودآزمایی فقط‌خواندنی») | **Missing** | building blocks exist: SqlGuard over catalog + packs, `cap.server_perms`/`cap.object_perms`, `rsm.login_is_sysadmin` |
| Lab snapshot with least-privilege login shows only reads | Partial | proven in CI; real lab server = owner task |

### Phase B — Rahkaran Knowledge Pack

| Requirement | Status | Evidence / gap |
|---|---|---|
| Rules as data with `responsibleRole`, `source`, `verified`, fa/en texts | Partial | 195 JSON rules with fa/en text and `source` (`rules/*.json`); **no `responsibleRole` field**, `verified` exists only for site settings; not grouped under `rules/rahkaran/` |
| Rules for every §1 item (Tuning V.3 baseline, log-growth KB incl. CDC, memory-dump KB, NUMA/ARR reset KB, v20 upgrade KB) | Partial | Tuning V.3 baseline mostly covered (33 baseline rules, SQL-CFG-*, DB-*, OS-*); log_reuse_wait and CDC covered (DB-LOG-*, DB-CDC-001); memory-dump, reset and upgrade KB texts not yet rules |
| Rahkaran health index 0–100 per site and layer, weights in `RULES.md` | **Missing** | — |
| Daily check screen («چک روزانه ۵ دقیقه‌ای») | **Missing** | most inputs exist as facts (backups, Agent jobs, CHECKDB age, disk/log space, errorlog, IIS 5xx, pool recycles, ARR, Redis, services, pending reboot, certificates) |
| SGProactiveMonitor field coverage | Partial | last backup, DB size, hardware, data-drive free space, OS/SQL version, fragmentation (on demand) — done; Rahkaran version + user count need team query packs / the version file (RK-001, *unverified*) |

### Phase C — slowness forensics and playbooks

| Requirement | Status | Evidence / gap |
|---|---|---|
| Flight recorder (last 30 min in memory) + «ثبت لحظه کندی» saving −15/+5 min with a note | **Missing** | raw history (24 h) exists in the file store; no in-memory ring of requests/blocking/IIS live requests, no mark |
| Blocking and long-request history (sampled every 10 s) | **Missing** | live blocking chains exist (`block/chains.sql`), not persisted as a timeline |
| Query regression detection (hash stats over time, plan change, parameter sensitivity, Query Store) | Partial | Query Store regressed queries (`qds/regressed.sql`); no own time series of `query_hash` stats |
| Module attribution (schema/object prefix → module, session pattern → app server, URL pattern → form) with a discovery screen | **Missing** | — (must be configurable, *unverified* until filled) |
| Slow IIS request ↔ SQL request correlation | **Missing** | — |
| USE/RED per resource / site / farm | **Missing** | inputs exist (counters, IIS logs) |
| Capacity and time-to-full (files, volumes, TempDB, backups, log growth vs log backup interval) | **Missing** | sizes and free space exist; no projection |
| Jalali business calendar for baselines; update events | **Missing** | baselines are hour-of-week only |
| Root-cause engine v2 (evidence-weighted, cross-layer) | Partial | slowness wizard ranks 22 causes with evidence and confidence |
| 12 symptom playbooks (P1–P12) with ticket-ready text | **Missing** | P1 ≈ today's slowness wizard; others not built |

### Phase D — error intelligence

| Requirement | Status | Evidence / gap |
|---|---|---|
| Error signatures (normalize, dedupe, first/last seen, trend, new / spiking) | **Missing** | error timeline lists events, no signatures |
| Rahkaran app-log tailing (configured folders, patterns, regex rules) | **Missing** | `ReadOnlyFile` offset reads already used for IIS log tails |
| Error KB (editable JSON, fa/en, Rahkaran KB items) | Partial | `EventKnowledgeBase.cs` (compiled, Windows/IIS/WAS/ANCM/SQL ids); not editable JSON, no Rahkaran KB items |
| Update/restart markers on every chart and on the error timeline | **Missing** | SQL start time, pool recycles, reboots are known facts; not drawn as markers |

### Phase E — reports and alerts

| Requirement | Status | Evidence / gap |
|---|---|---|
| Templates: periodic health, incident/slowness, before/after update, ستاد فنی bundle | Partial | one snapshot report + compare of two diagnoses + `rsm-bundle`; not the four templates |
| Ticket-ready Persian summary, one-click copy | **Missing** | — |
| In-app alerts (thresholds, snooze, history) | **Missing** | — |

### Phase F — quality and release

| Requirement | Status | Evidence / gap |
|---|---|---|
| Overhead proof in the status bar and `PERFORMANCE.md` | Partial | `cap.self_overhead` query exists; no status-bar figure, no `PERFORMANCE.md` |
| Lab scenarios in a separate `lab/` folder and solution, `LAB_RESULTS.md` | Partial | 5 scenarios in `tests/lab/` (blocking, CPU burn, disk latency, memory pressure, IIS queue); log growth and app-pool crash scenarios missing; no `LAB_RESULTS.md` |
| Tests: playbook fixtures, report snapshots fa/en, masking, audit log, version-gating fixtures 2016/2017/2019/2022 | Partial | report, masking and safety tests exist; playbook, audit-log and version fixtures missing |
| Persian docs: `docs/fa/راهنمای-کاربر.md`, `docs/fa/سوالات-متداول.md` | Partial | guide exists as `docs/user-guide/README.fa.md` with screenshots; no FAQ |
| Release: versioned exe, About with version/build/commit + read-only statement, SHA-256 | Partial | CI on the default branch publishes the exe as a 1-day artifact only; the rolling `rsm-latest` pre-release workflow is on branch `claude/project-thread-uevw4p` (`fc74d54`) and is **not merged**; About shows the version only; no checksum |

## 3. Definition of Done (PROMPT v2 §10) — today

| # | Item | Status |
|---|---|---|
| 1 | `READONLY_AUDIT.md` confirms §2; §2.2 tests green | Done with this PR (pending R1–R5 confirmation) |
| 2 | Operations Audit panel and Self-Test screen | Missing |
| 3 | Knowledge Pack, health index, daily check, 12 playbooks | Partial (rules) / Missing |
| 4 | Flight recorder … update/restart markers | Missing (regression: Partial) |
| 5 | Error signatures, app-log tailing, error KB, alerts | Missing (KB: Partial) |
| 6 | Report templates, ticket summary | Partial / Missing |
| 7 | Overhead, lab, Persian guide, release + checksum | Partial |
| 8 | Nothing Rahkaran-specific guessed | Holds today (every product-specific value is a setting marked *unverified*) |

## 4. Plan for Phases A–F

One phase at a time, each ending with build + all tests green, notes in `docs/phase-notes/V2-<phase>.md`, docs
updated, CI green; the next phase starts only after the owner's OK. Size: S ≈ 2–4 commits, M ≈ 5–10, L ≈ 10+.

| Phase | Scope (in order) | Size |
|---|---|---|
| **A** (rest) | 1. `IOperationAudit` in `RSM.Core` + recorder in the data folder (ring in memory, daily JSONL file); one call per transport listed in §2 (no new server operation). 2. Operations Audit panel (filter by node/kind, export) and report appendix. 3. Self-Test screen: SqlGuard over catalog + packs, effective permissions, sysadmin banner, audit summary. 4. Tests: every transport records exactly one entry per read; audit file stays in the data folder. | M |
| **B** | `responsibleRole` + `verified` in the rule schema (all 195 rules), KB-derived rules under `rules/rahkaran/`, health index with weights in `RULES.md`, daily-check page, SGProactiveMonitor coverage table. | L |
| **C** | Flight recorder + slowness mark, blocking / long-request history, query-hash time series + regression, module mapping + discovery screen, IIS↔SQL correlation, USE/RED, time-to-full, Jalali business calendar, root-cause v2, playbooks P1–P12. Split into C1 (recorder, history, regression, time-to-full, calendar) and C2 (mapping, correlation, USE/RED, playbooks). | L + L |
| **D** | Error signatures, app-log tailing, error KB as editable JSON, update/restart markers. | M |
| **E** | Four report templates, ticket-ready summary, in-app alerts. | M |
| **F** | `PERFORMANCE.md` + status-bar overhead, `lab/` solution + 2 new scenarios + `LAB_RESULTS.md`, version fixtures, `docs/fa/` guide + FAQ, About with commit + read-only statement, SHA-256 next to the release assets. | M |

Owner-only tasks stay with the owner: lab runs (`/mnt/project-files/lab/lab-run-guide-fa.md`), the Windows Server 2022
Standard test (`/mnt/project-files/windows-2022-test/steps.fa.md`), filling the *unverified* settings (version file
path, service patterns, module mappings).

## 5. Questions for the owner (each has a default; work continues on the default)

1. **R1–R5** in `READONLY_AUDIT.md` §1: confirm as compliant? *Default: yes, no code change.*
2. **Storage:** PROMPT v2 says SQLite for slowness marks and blocking history; you chose the file store in P5.
   *Default: keep the file store (no SQLite).*
3. **Rule layout:** add `responsibleRole` and `verified` to every existing rule and put only new KB rules in
   `rules/rahkaran/`? *Default: yes (no renaming of the 195 rule ids).*
4. **Lab folder:** move `tests/lab/` to a top-level `lab/` with its own solution, as PROMPT v2 §9.2 asks?
   *Default: yes, in Phase F.*
5. **Persian guide path:** move `docs/user-guide/README.fa.md` to `docs/fa/راهنمای-کاربر.md`? *Default: yes in
   Phase F, leaving a link at the old path.*
