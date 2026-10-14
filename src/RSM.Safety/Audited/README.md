# Audited exceptions

The only place in `src/` where RS0030 (banned API) may be suppressed. Every type here is listed in
`ARCHITECTURE.md` §6.4 and pinned by `RSM.ArchTests` (type list + public surface).

Phase 0: **empty**. Planned:
- `RemoteRegistryReader` (P2) — WMI `StdRegProv` *Get/Enum* read methods only, hard-coded key whitelist (decision Q3).
- `ProcessSnapshot` (P2) — `Process.GetProcesses()` read members only.
