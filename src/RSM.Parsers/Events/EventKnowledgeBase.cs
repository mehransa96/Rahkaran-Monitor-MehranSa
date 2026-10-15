using RSM.Core.Model;

namespace RSM.Parsers.Events;

/// <summary>One Windows event as RSM keeps it: never the raw XML, the message already masked and truncated.</summary>
/// <param name="Level">1 critical, 2 error, 3 warning, 4 information, 0 "log always" (shown as information).</param>
public sealed record EventRecordData(string Log, string Provider, int Id, int Level, DateTimeOffset AtUtc, string? Message);

/// <summary>
/// A known event (spec §9.8): what it means and what to check next, in fa and en. <see cref="Providers"/> entries
/// ending in <c>*</c> match by prefix (e.g. <c>MSSQL$*</c> for named instances). An empty <see cref="Ids"/> list
/// matches every error/warning of the provider.
/// </summary>
public sealed record EventKbEntry(
    string Key,
    IReadOnlyList<string> Providers,
    IReadOnlyList<int> Ids,
    Severity Severity,
    LocalizedText Title,
    LocalizedText Explanation,
    LocalizedText NextStep,
    string? RuleId = null,
    bool ProblemsOnly = false)
{
    public bool MatchesProvider(string provider) =>
        Providers.Any(p => p.EndsWith('*')
            ? provider.StartsWith(p[..^1], StringComparison.OrdinalIgnoreCase)
            : provider.Equals(p, StringComparison.OrdinalIgnoreCase));

    public bool Matches(string provider, int id, int level) =>
        MatchesProvider(provider) && (Ids.Count == 0 || Ids.Contains(id)) && (!(ProblemsOnly || Ids.Count == 0) || level is >= 1 and <= 3);
}

/// <summary>Built-in event knowledge base. Every event id named in spec §9.8 has an entry (checked by a test).</summary>
public static class EventKnowledgeBase
{
    private static readonly string[] Was = ["Microsoft-Windows-WAS", "WAS"];
    private static readonly string[] Scm = ["Service Control Manager"];
    private static readonly string[] Disk = ["disk"];
    private static readonly string[] Ntfs = ["Ntfs", "Microsoft-Windows-Ntfs"];
    private static readonly string[] Mssql = ["MSSQLSERVER", "MSSQL$*"];
    private static readonly string[] Schannel = ["Schannel"];

    public static IReadOnlyList<EventKbEntry> All { get; } =
    [
        new("kernel-power-41", ["Microsoft-Windows-Kernel-Power"], [41], Severity.Critical,
            new("خاموشی ناگهانی سرور (Kernel-Power 41)", "Unexpected restart (Kernel-Power 41)"),
            new("سیستم بدون خاموش شدن عادی دوباره راه‌اندازی شده است: قطع برق، Crash (صفحه آبی)، ریست سخت‌افزاری یا Hang.",
                "The system rebooted without cleanly shutting down first: power loss, a crash (bugcheck), a hardware reset or a hang."),
            new("رخداد BugCheck (1001) در همان زمان، لاگ iLO/IPMI و منبع تغذیه را بررسی کنید.", "Check for a BugCheck (1001) at the same time, the iLO/IPMI log and the power supply."),
            "OS-010"),
        new("eventlog-6008", ["EventLog"], [6008], Severity.Warning,
            new("خاموشی قبلی غیرمنتظره بود (EventLog 6008)", "Previous shutdown was unexpected (EventLog 6008)"),
            new("ویندوز در راه‌اندازی بعدی تشخیص داده که خاموشی قبلی عادی نبوده است.", "On the next start Windows found that the previous shutdown was not clean."),
            new("با Kernel-Power 41 و User32 1074 در همان بازه مقایسه کنید.", "Correlate with Kernel-Power 41 and User32 1074 around the same time."),
            "OS-010"),
        new("user32-1074", ["User32"], [1074], Severity.Info,
            new("راه‌اندازی مجدد یا خاموشی برنامه‌ریزی‌شده (User32 1074)", "Planned restart or shutdown (User32 1074)"),
            new("یک فرایند یا کاربر سرور را Restart/Shutdown کرده است؛ متن پیام نام فرایند، کاربر و دلیل را دارد.",
                "A process or user restarted or shut down the server; the message names the process, the user and the reason."),
            new("بررسی کنید Restart در ساعت کاری و با هماهنگی انجام شده است (مثلاً به‌روزرسانی ویندوز).", "Check that the restart was planned and outside business hours (e.g. Windows Update).")),
        new("resource-exhaustion-2004", ["Microsoft-Windows-Resource-Exhaustion-Detector"], [2004], Severity.Critical,
            new("کمبود حافظه مجازی (Resource-Exhaustion 2004)", "Low virtual memory (Resource-Exhaustion 2004)"),
            new("Commit حافظه به سقف نزدیک شده است؛ پیام، فرایندهای پرمصرف را نام می‌برد. ممکن است فرایندها Crash کنند.",
                "Committed memory is close to the commit limit; the message names the largest consumers. Processes may fail."),
            new("مصرف حافظه w3wp و sqlservr، تنظیم max server memory و اندازه Pagefile را بررسی کنید.", "Check w3wp and sqlservr memory, max server memory and the pagefile size."),
            "EVT-001"),
        new("srv-2013", ["srv"], [2013], Severity.Warning,
            new("دیسک نزدیک به پر شدن (srv 2013)", "Disk at or near capacity (srv 2013)"),
            new("یکی از درایوها تقریباً پر است.", "A drive is at or near capacity."),
            new("فضای آزاد درایوها را در صفحه Storage ببینید؛ لاگ‌های IIS و پشتیبان‌ها معمولاً عامل رشدند.", "See free space on the Storage page; IIS logs and backups are the usual growth."),
            "EVT-006"),
        new("scm-unexpected-stop", Scm, [7031, 7034], Severity.Warning,
            new("توقف غیرمنتظره سرویس (SCM 7031/7034)", "Service terminated unexpectedly (SCM 7031/7034)"),
            new("یک سرویس ویندوز بدون درخواست متوقف شده است (7031: اقدام بازیابی انجام می‌شود، 7034: بدون بازیابی).",
                "A Windows service stopped without being asked to (7031: recovery action follows, 7034: no recovery)."),
            new("Application Error 1000 و WER 1001 همان فرایند را در همان زمان ببینید.", "Look for Application Error 1000 and WER 1001 for the same process at the same time."),
            "SVC-002"),
        new("scm-start-failed", Scm, [7000, 7009], Severity.Warning,
            new("سرویس اجرا نشد (SCM 7000/7009)", "Service failed to start (SCM 7000/7009)"),
            new("سرویس هنگام شروع خطا داده (7000) یا در مهلت مقرر پاسخ نداده است (7009).", "The service failed to start (7000) or did not respond in time (7009)."),
            new("حساب سرویس، رمز آن، وابستگی‌ها و لاگ خود سرویس را بررسی کنید.", "Check the service account and its password, dependencies and the service's own log.")),
        new("scm-7023", Scm, [7023], Severity.Warning,
            new("سرویس با خطا متوقف شد (SCM 7023)", "Service terminated with an error (SCM 7023)"),
            new("سرویس متوقف شده و کد خطا گزارش کرده است.", "The service stopped and reported an error code."),
            new("کد خطا در متن پیام و لاگ خود سرویس را بررسی کنید.", "Check the error code in the message and the service's own log.")),
        new("app-error-1000", ["Application Error"], [1000], Severity.Warning,
            new("Crash برنامه (Application Error 1000)", "Application crash (Application Error 1000)"),
            new("یک فرایند با خطا بسته شده است؛ پیام نام فرایند و ماژول خطادار را دارد. برای w3wp یعنی قطع همه درخواست‌های آن Pool.",
                "A process crashed; the message names the process and the faulting module. For w3wp every request of that pool was lost."),
            new("ماژول خطادار را ببینید و با .NET Runtime 1026 و WAS 5009 در همان زمان مقایسه کنید.", "Note the faulting module and correlate with .NET Runtime 1026 and WAS 5009 at the same time."),
            "EVT-002"),
        new("dotnet-1026", [".NET Runtime"], [1026], Severity.Warning,
            new("Exception مدیریت‌نشده در .NET (1026)", "Unhandled .NET exception (.NET Runtime 1026)"),
            new("یک برنامه .NET به دلیل Exception مدیریت‌نشده بسته شده است؛ پیام Stack trace را دارد.", "A .NET process ended on an unhandled exception; the message holds the stack trace."),
            new("اگر فرایند w3wp یا dotnet است، Stack trace را برای ستاد فنی ارسال کنید.", "If it is w3wp or dotnet, send the stack trace to ستاد فنی."),
            "EVT-002"),
        new("wer-1001", ["Windows Error Reporting"], [1001], Severity.Info,
            new("گزارش خطای ویندوز (WER 1001)", "Windows Error Reporting (WER 1001)"),
            new("ویندوز برای یک Crash یا Hang گزارش ساخته است (P1 نام فرایند است).", "Windows recorded a crash or hang report (P1 is the process name)."),
            new("برای w3wp با Application Error 1000 و WAS 5009/5010 مقایسه کنید.", "For w3wp correlate with Application Error 1000 and WAS 5009/5010."),
            "EVT-003"),
        new("was-5002", Was, [5002], Severity.Critical,
            new("Application Pool به دلیل خطاهای پیاپی غیرفعال شد (WAS 5002)", "Application pool disabled by rapid-fail protection (WAS 5002)"),
            new("Worker process این Pool چند بار پشت سر هم Crash کرده و IIS آن را متوقف کرده است؛ کاربران خطای 503 می‌بینند.",
                "The pool's worker process crashed repeatedly and IIS stopped the pool; users get HTTP 503."),
            new("علت Crash را در Application Error 1000 / .NET Runtime 1026 پیدا کنید. RSM هیچ Poolی را راه‌اندازی نمی‌کند.", "Find the crash cause in Application Error 1000 / .NET Runtime 1026. RSM never starts a pool."),
            "IIS-POOL-002"),
        new("was-5009", Was, [5009, 5011], Severity.Critical,
            new("Worker process به‌طور غیرمنتظره بسته شد (WAS 5009/5011)", "Worker process terminated unexpectedly (WAS 5009/5011)"),
            new("w3wp این Pool ناگهان بسته شده (5009) یا ارتباطش با WAS قطع شده است (5011)؛ درخواست‌های در حال اجرا از دست رفته‌اند.",
                "The pool's w3wp ended unexpectedly (5009) or lost its channel to WAS (5011); in-flight requests were lost."),
            new("Application Error 1000 و .NET Runtime 1026 در همان زمان را ببینید.", "Look at Application Error 1000 and .NET Runtime 1026 at the same time."),
            "IIS-POOL-002"),
        new("was-5010", Was, [5010], Severity.Critical,
            new("Worker process به Ping پاسخ نداد (WAS 5010)", "Worker process failed to respond to a ping (WAS 5010)"),
            new("w3wp قفل شده یا به‌شدت کند بوده است (مثلاً کمبود Thread یا GC طولانی) و IIS آن را جایگزین کرده است.",
                "w3wp was hung or extremely slow (e.g. thread starvation or long GC) and IIS replaced it."),
            new("CPU، Blocking در SQL و شمارنده‌های .NET را در همان بازه بررسی کنید.", "Check CPU, SQL blocking and .NET counters in the same window."),
            "IIS-POOL-002"),
        new("was-5013", Was, [5013], Severity.Warning,
            new("خاموش شدن Worker process بیش از حد طول کشید (WAS 5013)", "Worker process exceeded its shutdown time limit (WAS 5013)"),
            new("هنگام Recycle، درخواست‌های طولانی تا پایان مهلت shutdownTimeLimit تمام نشده‌اند.", "During a recycle, long requests did not finish within shutdownTimeLimit."),
            new("درخواست‌های طولانی (صفحه IIS) و زمان Recycle را بررسی کنید.", "Check long-running requests (IIS page) and the recycle time."),
            "IIS-POOL-002"),
        new("was-5117", Was, [5117], Severity.Info,
            new("Recycle به دلیل سقف حافظه (WAS 5117)", "Recycle on private-bytes limit (WAS 5117)"),
            new("Worker process به سقف حافظه تعریف‌شده برای Pool رسیده و Recycle شده است.", "The worker process reached the pool's private memory limit and was recycled."),
            new("رشد حافظه w3wp را در صفحه Windows ببینید.", "See w3wp memory growth on the Windows page.")),
        new("aspnet-1309", ["ASP.NET*"], [1309], Severity.Warning,
            new("Exception مدیریت‌نشده در ASP.NET (1309)", "Unhandled ASP.NET exception (1309)"),
            new("یک درخواست در ASP.NET (.NET Framework) با Exception مدیریت‌نشده تمام شده است؛ پیام آدرس و نوع خطا را دارد.",
                "A request in ASP.NET (.NET Framework) ended with an unhandled exception; the message names the URL and the exception."),
            new("تعداد را با خطاهای 500 در لاگ IIS مقایسه کنید.", "Compare the count with HTTP 500s in the IIS logs."),
            "EVT-004"),
        new("ancm", ["IIS AspNetCore Module*"], [], Severity.Critical,
            new("خطای ASP.NET Core Module (ANCM)", "ASP.NET Core Module (ANCM) error"),
            new("ماژول ASP.NET Core نتوانسته برنامه را اجرا یا به آن وصل شود (معادل خطاهای 502.5 و 500.30–500.37).",
                "The ASP.NET Core Module could not start or reach the application (the 502.5 / 500.30–500.37 errors)."),
            new("لاگ stdout برنامه، نسخه Hosting Bundle و processPath در web.config را بررسی کنید.", "Check the app's stdout log, the Hosting Bundle version and processPath in web.config."),
            "IIS-ERR-003"),
        new("disk-7", Disk, [7], Severity.Critical,
            new("Bad block روی دیسک (Disk 7)", "Bad block on a disk (Disk 7)"),
            new("دیسک یک بلوک خراب گزارش کرده است؛ خطر از دست رفتن داده.", "A disk reported a bad block; data is at risk."),
            new("سلامت دیسک/RAID را در ابزار سازنده (مثلاً iLO/SSA) بررسی کنید و CHECKDB پایگاه داده‌ها را ببینید.", "Check disk/RAID health in the vendor tool (e.g. iLO/SSA) and the databases' CHECKDB.")),
        new("disk-11", Disk, [11], Severity.Critical,
            new("خطای کنترلر دیسک (Disk 11)", "Disk controller error (Disk 11)"),
            new("درایور خطای کنترلر روی یک دستگاه ذخیره‌سازی گزارش کرده است.", "The driver detected a controller error on a storage device."),
            new("Firmware و درایور کنترلر و مسیرهای SAN/Multipath را بررسی کنید.", "Check controller firmware/driver and SAN/multipath paths.")),
        new("disk-51", Disk, [51], Severity.Warning,
            new("خطا هنگام Paging (Disk 51)", "Error during a paging operation (Disk 51)"),
            new("یک عملیات I/O هنگام Paging خطا داده است؛ معمولاً نشانه مشکل دیسک یا مسیر ذخیره‌سازی.", "An I/O during paging failed; usually a disk or storage-path problem."),
            new("با Disk 153 و تأخیر دیسک (Avg. Disk sec/Read) در همان زمان مقایسه کنید.", "Correlate with Disk 153 and disk latency at the same time.")),
        new("disk-153", Disk, [153], Severity.Warning,
            new("تکرار عملیات I/O (Disk 153)", "I/O operation retried (Disk 153)"),
            new("دیسک یک I/O را دوباره امتحان کرده است؛ در SQL Server همراه با هشدار 833 (I/O طولانی) دیده می‌شود.",
                "The disk retried an I/O; on SQL Server it often appears with error 833 (long I/O)."),
            new("تأخیر دیسک، Multipath و Firmware را بررسی کنید.", "Check disk latency, multipath and firmware.")),
        new("ntfs-55", Ntfs, [55], Severity.Critical,
            new("ساختار فایل‌سیستم خراب است (Ntfs 55)", "File system structure is corrupt (Ntfs 55)"),
            new("NTFS خرابی ساختار فایل‌سیستم را روی یک Volume گزارش کرده است.", "NTFS found corruption on a volume."),
            new("با هماهنگی و در زمان مناسب chkdsk اجرا شود (RSM آن را اجرا نمی‌کند) و CHECKDB پایگاه داده‌های آن Volume را ببینید.", "Plan a chkdsk in a maintenance window (RSM never runs it) and check CHECKDB of databases on that volume.")),
        new("ntfs-98", Ntfs, [98], Severity.Warning,
            new("Volume نیاز به بررسی دارد (Ntfs 98)", "Volume needs a check (Ntfs 98)"),
            new("NTFS اعلام کرده است که Volume برای رفع خطا باید بررسی (chkdsk) شود.", "NTFS reported that the volume needs a check (chkdsk) to repair errors."),
            new("در پنجره نگهداری برنامه‌ریزی کنید.", "Plan it in a maintenance window."),
            ProblemsOnly: true),
        new("mssql-823", Mssql, [823], Severity.Critical,
            new("خطای I/O در SQL Server (823)", "SQL Server I/O error (823)"),
            new("سیستم‌عامل خواندن/نوشتن صفحه را ناموفق گزارش کرده است؛ نشانه مشکل جدی ذخیره‌سازی.", "The OS failed a page read/write; a serious storage problem."),
            new("فوراً CHECKDB و سلامت دیسک را بررسی کنید و از سالم بودن پشتیبان مطمئن شوید.", "Check CHECKDB and disk health at once and make sure backups are good.")),
        new("mssql-824", Mssql, [824], Severity.Critical,
            new("خطای سازگاری منطقی صفحه (824)", "Logical consistency error (824)"),
            new("صفحه خوانده شده ولی Checksum یا شناسه آن نادرست است (خرابی داده).", "A page was read but its checksum or id is wrong (corruption)."),
            new("CHECKDB و جدول suspect_pages را ببینید؛ با ستاد فنی هماهنگ کنید.", "Check CHECKDB and suspect_pages; involve ستاد فنی.")),
        new("mssql-833", Mssql, [833], Severity.Warning,
            new("I/O طولانی‌تر از 15 ثانیه (833)", "I/O longer than 15 seconds (833)"),
            new("SQL Server درخواست‌های I/O با زمان بیش از 15 ثانیه دیده است.", "SQL Server saw I/O requests taking longer than 15 seconds."),
            new("تأخیر فایل‌ها (صفحه SQL) و Disk 153 را بررسی کنید.", "Check file latency (SQL page) and Disk 153.")),
        new("mssql-17883", Mssql, [17883], Severity.Critical,
            new("Scheduler غیرپاسخگو (17883)", "Non-yielding scheduler (17883)"),
            new("یک Thread در SQL Server مدت طولانی CPU را رها نکرده است؛ سرور ممکن است موقتاً قفل شود.", "A SQL Server thread did not yield for a long time; the server may freeze."),
            new("Dump تولیدشده و نسخه CU را بررسی کنید.", "Check the generated dump and the CU level.")),
        new("mssql-701", Mssql, [701], Severity.Critical,
            new("کمبود حافظه در SQL Server (701)", "Insufficient memory in SQL Server (701)"),
            new("SQL Server برای اجرای یک درخواست حافظه کافی نداشته است.", "SQL Server lacked memory to run a query."),
            new("max server memory، Memory grants و مصرف حافظه سایر فرایندها را بررسی کنید.", "Check max server memory, memory grants and other processes' memory.")),
        new("mssql-9002", Mssql, [9002], Severity.Critical,
            new("Transaction log پر است (9002)", "Transaction log full (9002)"),
            new("لاگ تراکنش یک پایگاه داده پر شده و نوشتن در آن متوقف می‌شود.", "A database's transaction log is full; writes stop."),
            new("log_reuse_wait_desc، پشتیبان لاگ و فضای دیسک را بررسی کنید (صفحه Databases).", "Check log_reuse_wait_desc, log backups and disk space (Databases page).")),
        new("mssql-18456", Mssql, [18456], Severity.Info,
            new("ورود ناموفق به SQL Server (18456)", "SQL Server login failed (18456)"),
            new("یک Login با نام کاربری یا رمز نادرست یا بدون دسترسی تلاش کرده است.", "A login attempt failed (wrong name/password or no access)."),
            new("تعداد زیاد از یک سرور برنامه یعنی رمز تغییر کرده یا Connection string نادرست است.", "Many from one app server mean a changed password or a wrong connection string.")),
        new("schannel-36887", Schannel, [36887], Severity.Info,
            new("هشدار TLS از طرف مقابل (Schannel 36887)", "TLS fatal alert received (Schannel 36887)"),
            new("طرف مقابل اتصال TLS را با هشدار بسته است (اغلب ناسازگاری نسخه یا Cipher).", "The remote side closed a TLS connection with an alert (often a version or cipher mismatch)."),
            new("فقط اگر کاربران خطای اتصال دارند پیگیری شود.", "Follow up only if users see connection errors."),
            "EVT-005"),
        new("schannel-36874", Schannel, [36874], Severity.Info,
            new("Cipher مشترکی برای TLS پیدا نشد (Schannel 36874)", "No common TLS cipher suite (Schannel 36874)"),
            new("کلاینت با نسخه/Cipherی وصل شده که سرور پشتیبانی نمی‌کند.", "A client offered TLS versions/ciphers the server does not support."),
            new("کلاینت‌های قدیمی و تنظیمات TLS سرور را بررسی کنید.", "Check old clients and the server's TLS settings."),
            "EVT-005"),
        new("time-service", ["Microsoft-Windows-Time-Service"], [], Severity.Info,
            new("خطای همگام‌سازی زمان (Time-Service)", "Time synchronisation problem (Time-Service)"),
            new("سرویس زمان ویندوز نتوانسته با منبع زمان همگام شود؛ اختلاف ساعت روی Kerberos، لاگ‌ها و همبستگی رخدادها اثر دارد.",
                "Windows Time could not sync with its source; clock skew affects Kerberos, logs and event correlation."),
            new("منبع زمان (Domain controller / NTP) و دسترسی شبکه را بررسی کنید.", "Check the time source (domain controller / NTP) and network access."),
            "EVT-007"),
    ];

    /// <summary>Every event id the knowledge base asks the reader to fetch even at information level (e.g. User32 1074).</summary>
    public static IReadOnlyList<int> InformationalIds { get; } = [1074, 1001, 5117, 18456];

    public static EventKbEntry? Find(string provider, int id, int level) => All.FirstOrDefault(e => e.Matches(provider, id, level));

    public static EventKbEntry? ByKey(string key) => All.FirstOrDefault(e => e.Key == key);

    /// <summary>True when the message names an IIS worker process (w3wp) or an out-of-process ASP.NET Core host (dotnet).</summary>
    public static bool NamesWebWorker(string? message) =>
        message is not null && (message.Contains("w3wp", StringComparison.OrdinalIgnoreCase) || System.Text.RegularExpressions.Regex.IsMatch(message, @"\bdotnet(\.exe)?\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase));
}
