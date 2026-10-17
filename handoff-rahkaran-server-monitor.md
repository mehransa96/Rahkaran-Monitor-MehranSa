# active-memory handoff: Rahkaran Server Monitor (RSM)

**Handoff #1** · 2026-10-17 · Lineage: #1 (2026-10-17): فازهای ۰ تا ۵ ساخته و پوش شد، CI سبز، به‌علاوهٔ مقایسهٔ دو عیب‌یابی؛ منتظر بررسی‌های انسانی

## 0. Instructions for Claude (read first)

You are continuing work from a previous chat. That chat is gone; this file is the complete context and the source of truth.

1. Read this whole file before replying.
2. Follow sections 3 (Style), 4 (Hard rules) and 5 (Corrections) in every reply, for the rest of this chat. They override your defaults.
3. Use the values in section 8 exactly. Never round, re-estimate, or "correct" them.
4. Do not suggest anything listed in section 7 (Changed / rejected) again unless the user brings it up.
5. Code word: None active. If the user types /amcodeword, start every reply with the phrase they choose (default "Yes Boss!").
6. Your first reply: at most 5 lines covering the goal, the current state, and the next step (section 11). Mention any files from section 13 that were not attached. Ask the questions in section 12 if there are any. End with "Ready to continue with <next step>?" Then wait for the user's go.

## 1. Mission
- **Goal:** ساخت **Rahkaran Server Monitor (RSM)**، ابزار دسکتاپ قابل‌حمل، تک‌فایل و **کاملاً فقط‌خواندنی** (.NET 8 WPF) برای پایش و عیب‌یابی سرورهای راهکاران (Systemgroup ERP) روی Windows Server + IIS + SQL Server، دوزبانه فارسی (RTL) و انگلیسی، طبق `docs/SPEC.md` (مرجع اصلی) و `PLAN.md`.
- **Done looks like:** همهٔ فازها با ساخت سبز، همهٔ آزمون‌ها سبز، یادداشت فاز در `docs/phase-notes/`، کامیت، پوش، CI سبز، گزارش، و **تأیید صریح کاربر**؛ چک‌لیست §19 مشخصات کامل.
- **Why it matters / context:** ابزار کارشناس پشتیبانی برای دیدن سلامت سرور مشتری و یافتن علت کندی، بدون هیچ تغییری در سرور.

## 2. About the user (as relevant to this work)
- مالک مخزن `mehransa96/Rahkaran-Monitor-MehranSa`.
- با راهکاران و ستاد فنی سیستم‌گروه آشناست؛ تصمیم‌های محصول با اوست.
- روند گفتگو: کارهای فنی را می‌سپارد، با پیام کوتاه («Continue»، «Ok»، «Continue best») فاز بعدی را تأیید می‌کند و توضیح را به فارسی ساده می‌خواهد.

## 3. Style & communication
- **Language:** برای کاربر فارسی (کاربر خواست: «به زبان فارسی و خلاصه و ساده»). کامیت‌ها، کد، کامنت‌ها و مستندات فنی به انگلیسی. متن‌های رابط کاربری همیشه فارسی و انگلیسی.
- **Tone:** مستقیم و صادق. شکست‌ها و کارهای انجام‌نشده را صریح بگو.
- **Reply length:** کوتاه؛ جزئیات فقط وقتی لازم است.
- **Formatting:** فهرست کوتاه، جدول برای داده؛ ایموجی فقط در قالب checkpoint/handoff.
- **Working style:** کار را فاز به فاز پیش ببر. پیش از هر کامیت همهٔ آزمون‌ها را اجرا کن. پس از پوش، CI را بررسی کن (با یادآور `send_later`، نه sleep). تصمیم‌های ضروری را با AskUserQuestion بپرس.
- **Avoid:** حدس زدن نام‌ها و مسیرهای داخلی راهکاران؛ آوردن مدل یا شناسهٔ مدل در کامیت و مستندات؛ پاسخ طولانی.

## 4. Hard rules (word for word)
1. "Work **phase by phase** per `PLAN.md`. A phase ends with: build green, all tests green, short demo notes in `docs/phase-notes/`, one commit. **Do not start the next phase without the user's explicit OK.**"
2. "**Never invent Rahkaran internals** (table names, service names, DLL names, app paths). Anything product-specific not stated in `docs/SPEC.md` §3 must be a setting or a user-supplied query pack, and must be labelled *unverified* in code, docs and UI."
3. "Every threshold carries a source: `stad-fanni:<doc>§<section>` (Systemgroup ستاد فنی docs) or `general` (industry guidance). `general` items are labelled "general guidance — confirm with ستاد فنی" in the UI and reports."
4. "Prefer correctness and safety over feature count. Missing permission / feature / elevation ⇒ degrade gracefully and **say why** (Capability Matrix). Never crash, never "fix" the server."
5. NON-NEGOTIABLE safety rules (کامل در `CLAUDE.md`؛ فقط مالک مخزن، با تغییر صریح در `docs/SPEC.md` §2، می‌تواند آن‌ها را عوض کند). خلاصهٔ دقیق:
   - T-SQL فقط این‌ها:
     - `SELECT` فقط‌خواندنی و CTE؛
     - `DECLARE`/`SET` متغیر محلی؛
     - `INSERT` در table variable محلی؛
     - پیشوند ثابت `SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED; SET LOCK_TIMEOUT 2000; SET DEADLOCK_PRIORITY LOW; SET NOCOUNT ON;`؛
     - تنها EXEC مجاز: `EXEC master.sys.xp_readerrorlog`.
   - SQL فقط در فایل‌های `Queries/**/*.sql`، اجرا فقط با کلید کاتالوگ، پارامتر فقط با `SqlParameter`، و همه از `SqlGuard` عبور کنند.
   - هیچ اجرای فرایند، هیچ کنترل سرویس، هیچ تغییر IIS، WMI فقط query، هیچ نوشتن در رجیستری یا Event Log.
   - نوشتن فایل فقط داخل پوشهٔ دادهٔ RSM و فقط با `IDataFolder`.
   - Redis فقط فرمان‌های whitelist و فقط با `RespWhitelistClient`؛ RabbitMQ فقط GET به `/api/overview`، `/api/queues`، `/api/nodes`.
   - هیچ کنترل UI که روی سرور عمل کند؛ توصیه‌ها فقط متنی؛ هیچ ویرایشگر SQL؛ بدون اینترنت، تله‌متری یا auto-update.
   - همیشه: BannedApiAnalyzers در همهٔ پروژه‌ها، `TreatWarningsAsErrors`، هیچ suppress برای RS0030 بیرون از `src/RSM.Safety/Audited/`.
   - اتصال SQL با `Application Name=RSM-ReadOnlyMonitor` و `ApplicationIntent=ReadOnly`؛ رمزها فقط DPAPI یا ذخیره‌نشده.
   - فایل SQL جدید ⇒ header و یک ردیف در `SQL_CATALOG.md`.
   - قاعدهٔ جدید ⇒ JSON در `rules/`، ردیف در `RULES.md`، متن fa و en، منبع، آزمون.
6. "UI strings only via `src/RSM.App/Resources/Strings.resx` (English, neutral) and `Strings.fa.resx`; no literals in XAML (checked by `RSM.Tests`)."
7. "Commit messages: imperative, prefixed with phase, e.g. `P0: add SqlGuard AST walker`." پایان هر پیام کامیت دقیقاً:
   ```
   Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>
   Claude-Session: https://claude.ai/code/session_018LZShtGBw2ymn2LPtrQGRU
   ```
8. فقط روی شاخهٔ `claude/new-session-z9e5f7` کار و پوش کن. این شاخه شاخهٔ پیش‌فرض است و PR ندارد. هیچ شناسهٔ مدلی در کامیت، کد یا مستندات نیاید.
9. نام دستورهای UI نباید با `Kill|Recycle|Restart|Stop|Start|Shrink|Rebuild|Reorganize|Execute|Run|Fix|Apply` مطابق شود. کلیدهای resx که در XAML به کار می‌روند باید با `[A-Z][A-Za-z]+_[A-Za-z_]+` مطابق باشند (بدون رقم).

## 5. Corrections log
| # | Claude did | The user wanted |
|---|---|---|
| 1 | گزارش و پرسش را به انگلیسی و طولانی داد | «به زبان فارسی و خلاصه و ساده بگو» |

## 6. Decisions
| Decision | Why |
|---|---|
| ذخیرهٔ تاریخچه به‌صورت فایل، بدون SQL («Non-SQL file store (Recommended)») | انتخاب کاربر؛ RSM هیچ SQL از خودش ندارد |
| PDF با WebView2 `PrintToPdf`، آفلاین؛ اگر نشد HTML و «چاپ از مرورگر» | تصمیم Q1 در PLAN |
| صفر فراخوانی `Process.Start`؛ RSM هیچ فایلی را خودش باز نمی‌کند | تصمیم Q2 در PLAN |
| کاربر مسیر «فایل نسخه راهکاران» (RK-001) را خودش در تنظیمات سایت وارد می‌کند | گفتهٔ کاربر: «خودم در تنظیمات سایت واردش می‌کنم» |
| فاز ۵ آخرین فاز برنامه است | PLAN.md |

## 7. Changed / rejected
- طرح SQLite برای تاریخچه (`data/rsm.db`) → ذخیرهٔ فایلی (تصمیم کاربر در فاز ۵؛ ARCHITECTURE §10 به‌روز شد)
- zstd → Brotli (بدون وابستگی native در exe تک‌فایل)
- ❌ QuestPDF (به‌خاطر مجوز)
- ❌ آپلود artifact حجیم `RSM-win-x64` با نگهداری ۳ روز → ۱ روز (فضای artifact حساب پر شده بود)

## 8. Data & facts (exact)
| Item | Value |
|---|---|
| Repo / branch | `mehransa96/Rahkaran-Monitor-MehranSa` / `claude/new-session-z9e5f7` |
| Last commit | `07a6a54` P5: compare two diagnoses (before vs after) and test host wiring |
| CI green | runs 37–45 (all P5 commits; run 45 = `07a6a54`) |
| Scheduled check | `trig_018taM2HnWRdYnSA7LADGFwm`, fires 2026-10-17T04:10:00Z (check run 44) |
| Tests (local) | Safety 520, RSM.Tests 1 003, Arch 142, Integration 1 passed + 13 skipped locally |
| Rules | 195 rules (OS-019 added 2026-10-17) + 22 slowness causes + 37 detectors |
| Target OS | **Windows Server 2022 Standard** on every Rahkaran server (owner, 2026-10-17); build 20348, SKU 7; CI Windows jobs pinned to `windows-2022`; other versions → OS-019 *Info* |
| History retention | raw 24 h, 1-min 7 days, 15-min 90 days |
| Baseline | median/p95 per hour-of-week, 4 weeks of 15-min averages, ≥ 8 values per slot, Saturday = slot 0 |
| Anomaly | > p95 × 1.5 for ≥ 3 consecutive samples (`general`) |
| Slowness capture | default 120 s, interval scale 1/3, confidence cap 0.95, listed from 0.15 |
| Snapshot compare | numbers moved ≥ 10 % |
| Bundle | format `rsm-bundle` v1; entry cap 256 MB; file cap 128 MB |
| WebView2 | `Microsoft.Web.WebView2` 1.0.2849.39; virtual host `rsm-report.invalid`; profile `<data>/webview2` |
| CI UI tour (run 42) | 40 screenshots; PDF smoke `ok 29697` |
| Overhead | ≤ 0.8 % of one core (P2 soak) |
| SDK | .NET SDK 8.0.426; Linux build with `-p:EnableWindowsTargeting=true` |

## 9. People, terms & names
- **People:** کاربر = مالک مخزن.
- **Terms & nicknames:** ستاد فنی = مستندات فنی سیستم‌گروه (منبع `stad-fanni:`)؛ GEN = راهنمای عمومی؛ ⚠U = تأییدنشده (unverified).
- **Names in use:**
  - پروژه‌ها: `RSM.Core`، `RSM.Safety`، `RSM.Parsers`، `RSM.Collectors.*`، `RSM.Storage`، `RSM.Reporting`، `RSM.Hosting`، `RSM.App`، `RSM.Cli`.
  - فرمان‌های CLI: `--snapshot`، `--collect --duration`، `--investigate`، `--export`، `--import`، `--compare`، `--list`، `--portable`.
  - حالت تور تصاویر: `RSM.exe --portable --ui-snapshots`.
  - تنظیم فایل نسخه: `RahkaranVersionFile`، برچسب UI «فایل نسخه راهکاران (تأییدنشده)».
  - اسکریپت‌های تولید قواعد در scratchpad: `gen_p5_slowness.py`، `ruleslib.py`.

## 10. Work state
| Item | Status | Version / location | Notes |
|---|---|---|---|
| Phases 0–4 | final | — | P4 report delivered; user replied «Ok» |
| Phase 5 features | final, pushed | `07a6a54` | history, baselines, slowness wizard, import/merge, PDF, CLI, UI pages, snapshot compare |
| User guide | final (text) | `docs/user-guide/README.md`, `README.fa.md` | images missing (`docs/user-guide/images/`) |
| Phase notes P5 | final | `docs/phase-notes/P5.md` | includes §19 checklist |
| Lab scripts | ready | `tests/lab/` | to be run by a human, never by RSM |
| CI run 45 (`07a6a54`) | green | — | — |
| Windows Server 2022 Standard target | PR (draft) | branch `claude/target-windows-server-2022-rh6v6v` | CI pinned to `windows-2022`, OS-019, facts `os.build`/`os.sku`; notes `docs/phase-notes/WS2022.md` |

## 11. Next steps
1. **Next action:** آخرین اجرای CI روی شاخه را بررسی کن (باید سبز باشد) و منتظر درخواست کاربر بمان.
2. منتظر کاربر بمان. فاز دیگری در PLAN نیست؛ کار تازه فقط با درخواست صریح او.
3. وقتی فضای artifact آزاد شد، تصاویر `ui-snapshots` را در `docs/user-guide/images/` قرار بده (نیاز به دانلود artifact توسط کاربر).

## 12. Open questions ⚠️
- آیا کاربر سناریوهای `tests/lab/` را روی ماشین آزمایشگاهی اجرا کرده و علت اول درست بوده است؟
- آیا RSM روی یک VM تمیز Windows Server 2022 Standard آزموده شده است؟ (۲۰۱۹ دیگر هدف نیست؛ همهٔ سرورهای راهکاران ۲۰۲۲ استاندارد هستند.)
- تصاویر راهنما: فضای artifact حساب GitHub پر بود («Artifact storage quota has been hit»). کاربر artifact را دانلود می‌کند یا فضای حساب را آزاد می‌کند؟
- تأیید نقشهٔ توپولوژی روی محیط واقعی (از فاز ۴ باز مانده است).

## 13. Re-attach checklist
- Nothing to attach. (همه‌چیز در مخزن `mehransa96/Rahkaran-Monitor-MehranSa`، شاخهٔ `claude/new-session-z9e5f7` است. گفتگوی تازه باید در همین مخزن باشد؛ `CLAUDE.md` و `docs/SPEC.md` را خودش می‌خواند.)

---
<sub>Audit: 13/13 sections · 9 rules · 1 corrections · 15 data points · secrets removed: none found · generated by active-memory</sub>
