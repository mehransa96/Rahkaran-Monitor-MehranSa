# Slowness lab scenarios (P5 acceptance)

These scripts **create load on a lab machine** so you can check that RSM's "Why is Rahkaran slow?" wizard
ranks the injected cause first. Every scenario also has a synthetic twin in `tests/RSM.Tests/SlownessTests.cs`,
which runs in CI.

> **Lab only.** Never run these scripts on a customer or production server. **RSM never runs them**: they are
> not shipped with the exe, and RSM cannot start processes (banned API, see `docs/SPEC.md` §2). A person runs them
> by hand on a lab box, then watches RSM.

## Procedure (every scenario)

1. Open RSM, select the lab site, start the **Live** view and wait about one minute.
2. Start the scenario script (see the table) and wait until it reports *load running*.
3. In RSM, open **Why is it slow?**, keep the default capture of 120 s and start the capture.
4. Compare RSM's top cause with the expected one, then let the script finish (or press Ctrl+C). Each script
   cleans up after itself.

| Scenario | Script | Run on | Expected top cause |
|---|---|---|---|
| CPU burn (non-SQL process) | `cpu-burn.ps1 -Minutes 5` | Data server | `SLOW-CPU-NONSQL` |
| Blocking pair | `blocking-setup.sql`, then `blocking-session-a.sql` and `blocking-session-b.sql` in two SSMS windows | Data server (lab instance) | `SLOW-BLOCKING` (head blocker = session A) |
| Disk latency | `disk-latency.ps1 -DiskSpd C:\tools\diskspd.exe -Target D:\rsm-lab` | Data server, volume of the lab database | `SLOW-DISK-DATA` |
| Memory pressure | `memory-pressure.ps1 -LeaveMb 300 -Minutes 5` | App server | `SLOW-MEM-OS` |
| IIS queue | `iis-queue.ps1 -Url http://app01/slow?ms=20000 -Concurrency 200` (the lab pool's *Queue Length* set to 50 by hand first) | any client | `SLOW-IIS-QUEUE` |

## Notes

- `blocking-*.sql` work only in the database `RsmLab`, which `blocking-setup.sql` creates and `blocking-cleanup.sql`
  drops. Run them as a lab administrator, not as the `rsm_monitor` login (which cannot write anything).
- DiskSpd is the free Microsoft tool (https://github.com/microsoft/diskspd); download it yourself.
- The IIS scenario needs a slow endpoint. The CI test app `tests/integration/RsmItCoreApp` has one (`/slow?ms=`).

---

# سناریوهای آزمایشگاهی کندی (پذیرش فاز ۵)

این اسکریپت‌ها **روی ماشین آزمایشگاهی بار ایجاد می‌کنند** تا بررسی کنید دستیار «چرا راهکاران کند است؟» علت
تزریق‌شده را در رتبهٔ اول نشان می‌دهد. هر سناریو یک نسخهٔ مصنوعی در `SlownessTests.cs` دارد که در CI اجرا می‌شود.

> **فقط آزمایشگاه.** هرگز روی سرور مشتری یا عملیاتی اجرا نکنید. **RSM هرگز این اسکریپت‌ها را اجرا نمی‌کند**؛ همراه
> برنامه منتشر نمی‌شوند و RSM اجازهٔ اجرای فرایند ندارد. یک نفر آن‌ها را دستی روی ماشین آزمایشگاهی اجرا می‌کند.

روش: نمای زنده را باز کنید، اسکریپت را اجرا کنید، در RSM «چرا کند است؟» را با ضبط ۱۲۰ ثانیه‌ای اجرا کنید و علت
اول را با جدول بالا مقایسه کنید. هر اسکریپت در پایان خودش را پاک‌سازی می‌کند.
