# V2 Phase A — compliance check and gap analysis (first part)

PROMPT v2 is stored verbatim as `docs/SPEC-v2.md`. Its §2 is PROMPT v1 §2 (`docs/SPEC.md`) unchanged; the owner
re-uploaded the same v2 file on 2026-10-17 (byte-identical to the earlier copy).

## Delivered (docs only, no code change — PROMPT v2 §0: "Do not change code before I approve")

- `READONLY_AUDIT.md` — inventory of every outbound operation (84 SQL catalog keys, 36 counters, CIM classes,
  8 registry values, 2 event-log channels, server files, IIS APIs, 9 Redis commands, 3 HTTP GET paths, EventPipe,
  local writes), each mapped to its §2 rule with `file:line`. **No violation found.** Five interpretations (R1–R5)
  are listed for the owner to confirm.
- `GAP_ANALYSIS.md` — PROMPT v1 leftovers (all of them need a real lab server), PROMPT v2 requirement by
  requirement, the §10 Definition of Done today, the Phase A–F plan, and five owner questions with defaults.

## Checks run

- Build and tests unchanged by this part (Markdown only); CI on the PR is the gate.

## Next (after the owner's OK)

Rest of Phase A: Operations Audit log + panel + report appendix, Read-only Self-Test screen, tests
(`GAP_ANALYSIS.md` §4).

## Owner tasks (unchanged)

- Lab scenarios: `/mnt/project-files/lab/lab-run-guide-fa.md`.
- Windows Server 2022 Standard test: `/mnt/project-files/windows-2022-test/steps.fa.md`.
