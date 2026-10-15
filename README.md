# Rahkaran Server Monitor (RSM) — پایشگر سرور راهکاران

A **strictly read-only** diagnostic and monitoring desktop tool for Rahkaran (Systemgroup ERP) servers:
SQL Server, databases, Windows, IIS/ARR, Redis, SSRS, services and event logs, with a guided
"why is Rahkaran slow?" investigation and bilingual (Persian/English) reports.

ابزاری **فقط‌خواندنی** برای پایش و عیب‌یابی سرورهای راهکاران؛ هیچ تغییری روی سرور ایجاد نمی‌کند.

**Status: Phase 2 (live SQL Server + Windows monitoring) delivered — awaiting review.** → [`README.en.md`](README.en.md) · [`README.fa.md`](README.fa.md)

| Document | Content |
|---|---|
| [`docs/SPEC.md`](docs/SPEC.md) | Original build prompt (authoritative spec) |
| [`CLAUDE.md`](CLAUDE.md) | Non-negotiable safety rules for every session |
| [`ARCHITECTURE.md`](ARCHITECTURE.md) | Solution layout, data flow, collectors, safety layer |
| [`SQL_CATALOG.md`](SQL_CATALOG.md) | Every T-SQL query with DMV, min version, permission, cost |
| [`RULES.md`](RULES.md) | Initial health-check rules with source references |
| [`PLAN.md`](PLAN.md) | Phases, acceptance criteria, decisions |
| [`PERMISSIONS.md`](PERMISSIONS.md) | Least-privilege SQL/Windows accounts (run by the DBA) |
| [`docs/phase-notes/`](docs/phase-notes/) | Demo notes per phase |
