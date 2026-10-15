using RSM.Core.Model;

namespace RSM.Reporting;

/// <summary>Fixed report labels (domain text, fa/en). The report never contains server-acting controls.</summary>
internal static class ReportText
{
    public static readonly LocalizedText Title = new("گزارش سلامت سرورهای راهکاران", "Rahkaran Server Health Report");
    public static readonly LocalizedText Site = new("سایت", "Site");
    public static readonly LocalizedText Nodes = new("سرورها", "Servers");
    public static readonly LocalizedText Generated = new("تاریخ تهیه", "Generated");
    public static readonly LocalizedText SnapshotWindow = new("بازهٔ جمع‌آوری", "Collection window");
    public static readonly LocalizedText RsmVersion = new("نسخهٔ RSM", "RSM version");
    public static readonly LocalizedText Rules = new("قواعد ارزیابی‌شده", "Rules evaluated");
    public static readonly LocalizedText HeavyIncluded = new("بررسی‌های سنگین (Fragmentation) اجرا شد", "Heavy checks (fragmentation) were included");
    public static readonly LocalizedText HeavyExcluded = new("بررسی‌های سنگین اجرا نشد (فقط به درخواست)", "Heavy checks were not run (on demand only)");
    public static readonly LocalizedText Masked = new("نام‌ها و نشانی‌ها در این گزارش پوشانده شده‌اند.", "Names and addresses in this report are masked.");
    public static readonly LocalizedText ReadOnlyNote = new(
        "RSM فقط‌خواندنی است: هیچ تغییری روی سرورها ایجاد نکرده است. توصیه‌ها فقط متن هستند؛ هر تغییر را با ستاد فنی هماهنگ کنید.",
        "RSM is read-only: it made no change to any server. Recommendations are text only; coordinate every change with ستاد فنی.");

    public static readonly LocalizedText Summary = new("خلاصهٔ مدیریتی", "Executive summary");
    public static readonly LocalizedText Score = new("امتیاز کلی", "Overall score");
    public static readonly LocalizedText ScoreNote = new(
        "امتیاز تقریبی: ۱۰۰ منهای ۱۵ برای هر مورد بحرانی، ۵ برای هر هشدار و ۱ برای هر اطلاع.",
        "Indicative score: 100 minus 15 per critical, 5 per warning and 1 per info finding.");
    public static readonly LocalizedText Critical = new("بحرانی", "Critical");
    public static readonly LocalizedText Warning = new("هشدار", "Warning");
    public static readonly LocalizedText Info = new("اطلاع", "Info");
    public static readonly LocalizedText TopIssues = new("مهم‌ترین موارد", "Top issues");
    public static readonly LocalizedText NoFindings = new("موردی یافت نشد.", "No findings.");

    public static readonly LocalizedText Inventory = new("مشخصات", "Inventory");
    public static readonly LocalizedText Compliance = new("انطباق با توصیه‌های ستاد فنی", "Compliance with the ستاد فنی baseline");
    public static readonly LocalizedText Item = new("مورد", "Item");
    public static readonly LocalizedText Expected = new("مقدار مورد انتظار", "Expected");
    public static readonly LocalizedText Actual = new("مقدار فعلی", "Actual");
    public static readonly LocalizedText Status = new("وضعیت", "Status");
    public static readonly LocalizedText Rule = new("قاعده", "Rule");
    public static readonly LocalizedText Compliant = new("منطبق", "Compliant");
    public static readonly LocalizedText Deviation = new("مغایر", "Deviation");
    public static readonly LocalizedText Unknown = new("نامعلوم", "Unknown");
    public static readonly LocalizedText NotApplicable = new("غیرمرتبط", "Not applicable");

    public static readonly LocalizedText Findings = new("یافته‌ها", "Findings");
    public static readonly LocalizedText Evidence = new("شواهد", "Evidence");
    public static readonly LocalizedText Recommendation = new("توصیه", "Recommendation");
    public static readonly LocalizedText Source = new("منبع", "Source");
    public static readonly LocalizedText GeneralGuidance = new("راهنمای عمومی — با ستاد فنی تأیید کنید", "general guidance — confirm with ستاد فنی");
    public static readonly LocalizedText Unverified = new("تأییدنشده", "unverified");
    public static readonly LocalizedText IndexCaution = new(
        "ساختار پایگاه راهکاران متعلق به سازنده است؛ بدون ستاد فنی ایندکسی ایجاد یا حذف نکنید.",
        "The Rahkaran schema is vendor-owned; do not create or drop indexes without ستاد فنی.");

    public static readonly LocalizedText Databases = new("پایگاه‌های داده", "Databases");
    public static readonly LocalizedText Database = new("پایگاه داده", "Database");
    public static readonly LocalizedText State = new("وضعیت", "State");
    public static readonly LocalizedText Recovery = new("Recovery", "Recovery");
    public static readonly LocalizedText SizeMb = new("حجم داده (MB)", "Data (MB)");
    public static readonly LocalizedText LastFull = new("آخرین Full (ساعت پیش)", "Last full (h ago)");
    public static readonly LocalizedText LastLog = new("آخرین Log (ساعت پیش)", "Last log (h ago)");
    public static readonly LocalizedText LastCheckdb = new("آخرین CHECKDB (روز پیش)", "Last CHECKDB (days ago)");
    public static readonly LocalizedText Never = new("هرگز", "never");
    public static readonly LocalizedText RahkaranDb = new("راهکاران", "Rahkaran");

    public static readonly LocalizedText Appendix = new("پیوست", "Appendix");
    public static readonly LocalizedText Capabilities = new("ماتریس قابلیت‌ها", "Capability Matrix");
    public static readonly LocalizedText Capability = new("قابلیت", "Capability");
    public static readonly LocalizedText Detail = new("توضیح", "Detail");
    public static readonly LocalizedText Collectors = new("وضعیت جمع‌آورها", "Collector status");
    public static readonly LocalizedText Collector = new("جمع‌آور", "Collector");
    public static readonly LocalizedText Duration = new("مدت (ثانیه)", "Duration (s)");
    public static readonly LocalizedText Indeterminate = new("قواعد نامعلوم (داده در دسترس نبود)", "Indeterminate rules (data was not available)");
    public static readonly LocalizedText MissingFact = new("دادهٔ ناموجود", "Missing data");
    public static readonly LocalizedText RuleErrors = new("خطای قواعد", "Rule errors");
    public static readonly LocalizedText Glossary = new("واژه‌نامه", "Glossary");

    public static readonly IReadOnlyList<(string Term, LocalizedText Meaning)> GlossaryTerms =
    [
        ("ستاد فنی", new("واحد پشتیبانی فنی سیستم (سازندهٔ راهکاران)؛ مرجع توصیه‌های پیکربندی.", "Systemgroup's technical support unit; the authority for configuration guidance.")),
        ("CU / GDR", new("به‌روزرسانی تجمعی / به‌روزرسانی امنیتی SQL Server.", "SQL Server cumulative update / security update.")),
        ("MAXDOP", new("حداکثر درجهٔ موازی‌سازی پرس‌وجو.", "Maximum degree of parallelism for a query.")),
        ("VLF", new("فایل مجازی Log؛ تعداد زیاد آن بازیابی و پشتیبان‌گیری را کند می‌کند.", "Virtual log file; too many slow down recovery and backups.")),
        ("CHECKDB", new("بررسی سازگاری پایگاه داده (DBCC CHECKDB).", "Database consistency check (DBCC CHECKDB).")),
    ];

    public static LocalizedText Severity(Severity severity) => severity switch
    {
        Core.Model.Severity.Critical => Critical,
        Core.Model.Severity.Warning => Warning,
        Core.Model.Severity.Info => Info,
        _ => new("سالم", "OK"),
    };

    public static LocalizedText Layer(Layer layer) => layer switch
    {
        Core.Model.Layer.Sql => new("SQL Server", "SQL Server"),
        Core.Model.Layer.Database => new("پایگاه‌های داده", "Databases"),
        Core.Model.Layer.Os => new("سیستم‌عامل", "Operating system"),
        Core.Model.Layer.Storage => new("دیسک و ذخیره‌سازی", "Disks & storage"),
        Core.Model.Layer.Iis => new("IIS", "IIS"),
        Core.Model.Layer.Arr => new("ARR", "ARR"),
        Core.Model.Layer.Redis => new("Redis", "Redis"),
        Core.Model.Layer.Ssrs => new("SSRS", "SSRS"),
        Core.Model.Layer.Services => new("سرویس‌ها", "Services"),
        Core.Model.Layer.Events => new("رویدادها", "Events"),
        Core.Model.Layer.RabbitMq => new("RabbitMQ", "RabbitMQ"),
        Core.Model.Layer.DotNet => new(".NET", ".NET"),
        Core.Model.Layer.Rahkaran => new("راهکاران", "Rahkaran"),
        _ => new("RSM", "RSM"),
    };

    public static LocalizedText CollectorState(CollectorStatusKind kind) => kind switch
    {
        CollectorStatusKind.Ok => new("کامل", "OK"),
        CollectorStatusKind.Partial => new("ناقص", "Partial"),
        CollectorStatusKind.Unavailable => new("در دسترس نیست", "Unavailable"),
        CollectorStatusKind.Error => new("خطا", "Error"),
        _ => new("اجرا نشد", "Skipped"),
    };

    /// <summary>Inventory rows shown per node: label + fact key.</summary>
    public static readonly IReadOnlyList<(LocalizedText Label, string Fact)> InventoryFacts =
    [
        (new("نسخه", "Product"), "sql.product_name"),
        (new("شمارهٔ نسخه", "Version"), "sql.version"),
        (new("ویرایش", "Edition"), "sql.edition"),
        (new("آخرین CU شناخته‌شده", "Latest known CU"), "sql.build.latest"),
        (new("پایان پشتیبانی (روز مانده)", "Support ends in (days)"), "sql.support.days_left"),
        (new("نام سرور", "Server name"), "sql.server_name"),
        (new("سیستم‌عامل", "Host OS"), "sql.host.distribution"),
        (new("مجازی‌سازی", "Virtualization"), "sql.vm_type"),
        (new("هستهٔ منطقی", "Logical CPUs"), "sql.cpu.logical"),
        (new("NUMA", "NUMA nodes"), "sql.numa.nodes"),
        (new("حافظهٔ فیزیکی (MB)", "Physical memory (MB)"), "sql.memory.physical_mb"),
        (new("max server memory (MB)", "max server memory (MB)"), "sql.config.max_server_memory_mb"),
        (new("Collation", "Collation"), "sql.collation"),
        (new("زمان کارکرد (ساعت)", "Uptime (h)"), "sql.uptime_hours"),
        (new("SQL Agent", "SQL Agent"), "sql.service.agent.status"),
        (new("پایگاه‌های راهکاران", "Rahkaran databases"), "rk.databases"),
        (new("روش تشخیص", "Detected by"), "rk.detected_by"),
    ];
}
