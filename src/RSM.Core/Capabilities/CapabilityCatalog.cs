using RSM.Core.Model;

namespace RSM.Core.Capabilities;

/// <summary>Ids and fa/en descriptions of every capability in the Capability Matrix (ARCHITECTURE.md §8).</summary>
public static class CapabilityIds
{
    public const string SqlConnect = "sql.connect";
    public const string SqlViewServerState = "sql.view_server_state";
    public const string SqlViewAnyDefinition = "sql.view_any_definition";
    public const string SqlConnectAnyDatabase = "sql.connect_any_database";
    public const string SqlMsdbRead = "sql.msdb_read";
    public const string SqlAgentRead = "sql.agent_read";
    public const string SqlErrorLog = "sql.errorlog";
    public const string SqlSsrsLog = "sql.ssrs_log";
    public const string SqlSysadmin = "sql.sysadmin";
    public const string WinCim = "win.cim";
    public const string WinPerf = "win.perf";
    public const string WinEventLog = "win.eventlog";
    public const string WinServices = "win.services";
    public const string WinRegistry = "win.registry";
    public const string IisConfig = "iis.config";
    public const string IisRequests = "iis.requests";
    public const string IisLogs = "iis.logs";
    public const string RedisPing = "redis.ping";
    public const string RedisInfo = "redis.info";
    public const string RedisSlowlog = "redis.slowlog";
    public const string RedisClientList = "redis.clientlist";
    public const string RabbitApi = "rabbit.api";
    public const string DotNetEventPipe = "dotnet.eventpipe";
    public const string RsmElevated = "rsm.elevated";
}

public static class CapabilityCatalog
{
    private const string PermissionsDoc = "PERMISSIONS.md";

    public static IReadOnlyList<CapabilityDefinition> All { get; } =
    [
        Def(CapabilityIds.SqlConnect, Layer.Sql,
            "اتصال به SQL Server", "Connect to SQL Server",
            "همه بررسی‌های SQL Server و پایگاه داده", "All SQL Server and database checks",
            "نام سرور، پورت، فایروال و نام کاربری/رمز را بررسی کنید.", "Check server name, port, firewall and login/password."),
        Def(CapabilityIds.SqlViewServerState, Layer.Sql,
            "مجوز VIEW SERVER STATE", "VIEW SERVER STATE permission",
            "Waitها، درخواست‌های در حال اجرا، Blocking، حافظه، I/O و شمارنده‌ها", "Waits, running requests, blocking, memory, I/O and counters",
            $"DBA باید GRANT VIEW SERVER STATE را اجرا کند ({PermissionsDoc} §1).", $"The DBA grants VIEW SERVER STATE ({PermissionsDoc} §1)."),
        Def(CapabilityIds.SqlViewAnyDefinition, Layer.Sql,
            "مجوز VIEW ANY DEFINITION", "VIEW ANY DEFINITION permission",
            "بررسی امنیت (Loginها، اعضای sysadmin، Linked Server)", "Security posture (logins, sysadmin members, linked servers)",
            $"DBA باید GRANT VIEW ANY DEFINITION را اجرا کند ({PermissionsDoc} §1).", $"The DBA grants VIEW ANY DEFINITION ({PermissionsDoc} §1)."),
        Def(CapabilityIds.SqlConnectAnyDatabase, Layer.Database,
            "دسترسی به همه پایگاه‌های داده", "Access to all databases",
            "فایل‌ها، VLFها، آمار و جداول بزرگ هر پایگاه داده", "Per-database files, VLFs, statistics and largest tables",
            $"DBA باید GRANT CONNECT ANY DATABASE را اجرا کند ({PermissionsDoc} §1).", $"The DBA grants CONNECT ANY DATABASE ({PermissionsDoc} §1)."),
        Def(CapabilityIds.SqlMsdbRead, Layer.Database,
            "خواندن تاریخچه پشتیبان‌گیری در msdb", "Read backup history in msdb",
            "آخرین پشتیبان‌ها، زنجیره LSN، صفحات مشکوک", "Last backups, LSN chain, suspect pages",
            $"دسترسی SELECT روی جداول msdb طبق {PermissionsDoc} §2.", $"SELECT on the msdb tables listed in {PermissionsDoc} §2."),
        Def(CapabilityIds.SqlAgentRead, Layer.Sql,
            "خواندن Jobهای SQL Agent", "Read SQL Agent jobs",
            "وضعیت Jobها، خطاها، Jobهای CDC و Shrink", "Job status, failures, CDC and shrink jobs",
            $"عضویت در نقش SQLAgentReaderRole در msdb ({PermissionsDoc} §2).", $"Membership in msdb SQLAgentReaderRole ({PermissionsDoc} §2)."),
        Def(CapabilityIds.SqlErrorLog, Layer.Sql,
            "خواندن ErrorLog سرور SQL", "Read the SQL Server error log",
            "خطاهای 823/824/825، 833، 17883، 9002، 18456 و …", "Errors 823/824/825, 833, 17883, 9002, 18456 …",
            $"مجوز اجرای xp_readerrorlog (بسته به نسخه) یا دسترسی خواندن به پوشه Log ({PermissionsDoc} §3).",
            $"EXECUTE on xp_readerrorlog (version-dependent) or read access to the Log folder ({PermissionsDoc} §3)."),
        Def(CapabilityIds.SqlSsrsLog, Layer.Ssrs,
            "خواندن ExecutionLog3 گزارش‌ساز", "Read SSRS ExecutionLog3",
            "گزارش‌های کند و خطادار SSRS", "Slow and failing SSRS reports",
            $"دسترسی SELECT روی ReportServer.dbo.ExecutionLog3 ({PermissionsDoc} §4).", $"SELECT on ReportServer.dbo.ExecutionLog3 ({PermissionsDoc} §4)."),
        Def(CapabilityIds.SqlSysadmin, Layer.Sql,
            "حساب با دسترسی sysadmin", "sysadmin account in use",
            "اطلاع: RSM همچنان فقط‌خواندنی است", "Info: RSM stays read-only",
            "برای پایش، حساب کم‌دسترسی rsm_monitor توصیه می‌شود.", "A least-privilege rsm_monitor login is recommended for monitoring."),
        Def(CapabilityIds.WinCim, Layer.Os,
            "WMI/CIM (فقط Query)", "WMI/CIM (queries only)",
            "سخت‌افزار، سیستم‌عامل، Power Plan، دیسک‌ها", "Hardware, OS, power plan, disks",
            "WinRM یا DCOM و دسترسی خواندن WMI روی root\\cimv2.", "WinRM or DCOM and WMI read access on root\\cimv2."),
        Def(CapabilityIds.WinPerf, Layer.Os,
            "شمارنده‌های کارایی ویندوز", "Windows performance counters",
            "CPU، حافظه، دیسک، شبکه به‌صورت زنده", "Live CPU, memory, disk and network",
            "عضویت در گروه Performance Monitor Users.", "Membership in Performance Monitor Users."),
        Def(CapabilityIds.WinEventLog, Layer.Events,
            "خواندن Event Log", "Read event logs",
            "رخدادهای System و Application", "System and Application events",
            "عضویت در گروه Event Log Readers و قانون فایروال Remote Event Log Management.", "Membership in Event Log Readers and the Remote Event Log Management firewall rule."),
        Def(CapabilityIds.WinServices, Layer.Services,
            "مشاهده سرویس‌ها", "View services",
            "وضعیت سرویس‌های SQL، IIS، Redis، CodeMeter و …", "State of SQL, IIS, Redis, CodeMeter … services",
            "دسترسی اتصال به Service Control Manager.", "Service Control Manager connect access."),
        Def(CapabilityIds.WinRegistry, Layer.Os,
            "خواندن رجیستری", "Read registry",
            "Pending reboot، زمان‌بندی پردازنده، استثناهای آنتی‌ویروس", "Pending reboot, processor scheduling, antivirus exclusions",
            "محلی: خودکار. راه دور: دسترسی WMI StdRegProv (فقط متدهای خواندن).", "Local: automatic. Remote: WMI StdRegProv access (read methods only)."),
        Def(CapabilityIds.IisConfig, Layer.Iis,
            "خواندن پیکربندی IIS", "Read IIS configuration",
            "سایت‌ها، Application Poolها، ARR و URL Rewrite", "Sites, application pools, ARR and URL Rewrite",
            "عضویت در Administrators محلی (برای applicationHost.config).", "Local Administrators membership (for applicationHost.config)."),
        Def(CapabilityIds.IisRequests, Layer.Iis,
            "درخواست‌های در حال اجرای IIS", "Live IIS requests",
            "صفحات کند در همین لحظه", "Pages that are slow right now",
            "اجرای RSM به‌صورت Administrator روی همان سرور و نصب ویژگی Request Monitor.", "Run RSM as Administrator on the server and install the Request Monitor feature."),
        Def(CapabilityIds.IisLogs, Layer.Iis,
            "خواندن لاگ‌های IIS", "Read IIS logs",
            "صدک‌های زمان پاسخ و خطاهای 5xx", "Latency percentiles and 5xx errors",
            "دسترسی خواندن به پوشه لاگ IIS (محلی یا Admin share).", "Read access to the IIS log folder (local or admin share)."),
        Def(CapabilityIds.RedisPing, Layer.Redis,
            "اتصال به Redis", "Connect to Redis",
            "همه بررسی‌های Redis", "All Redis checks",
            "آدرس، پورت (پیش‌فرض 6379)، فایروال و رمز Redis را بررسی کنید.", "Check host, port (default 6379), firewall and password."),
        Def(CapabilityIds.RedisInfo, Layer.Redis,
            "دستور INFO در Redis", "Redis INFO command",
            "حافظه، Evictionها، Hit ratio، Persistence", "Memory, evictions, hit ratio, persistence",
            "ممکن است دستور INFO تغییر نام داده یا غیرفعال شده باشد.", "The INFO command may be renamed or disabled."),
        Def(CapabilityIds.RedisSlowlog, Layer.Redis,
            "SLOWLOG در Redis", "Redis SLOWLOG",
            "دستورات کند Redis", "Slow Redis commands",
            "ممکن است دستور SLOWLOG تغییر نام داده یا غیرفعال شده باشد.", "SLOWLOG may be renamed or disabled."),
        Def(CapabilityIds.RedisClientList, Layer.Redis,
            "CLIENT LIST در Redis", "Redis CLIENT LIST",
            "کدام سرورهای برنامه از Redis استفاده می‌کنند", "Which app servers use Redis",
            "ممکن است دستور CLIENT تغییر نام داده یا غیرفعال شده باشد.", "CLIENT may be renamed or disabled."),
        Def(CapabilityIds.RabbitApi, Layer.RabbitMq,
            "API مدیریتی RabbitMQ (فقط GET)", "RabbitMQ management API (GET only)",
            "صف‌ها و هشدارهای گردش کار", "Workflow queues and alarms",
            "افزونه management و کاربری با تگ monitoring.", "Management plugin and a user with the monitoring tag."),
        Def(CapabilityIds.DotNetEventPipe, Layer.DotNet,
            "شمارنده‌های .NET فرایند راهکاران", ".NET counters of the Rahkaran process",
            "GC، ThreadPool، Exceptionها (اختیاری، پیش‌فرض خاموش)", "GC, thread pool, exceptions (optional, off by default)",
            "فقط محلی؛ RSM با همان کاربر w3wp یا Administrator اجرا شود.", "Local only; run RSM as the w3wp identity or Administrator."),
        Def(CapabilityIds.RsmElevated, Layer.Rsm,
            "اجرای RSM با دسترسی Administrator", "RSM running elevated",
            "پیکربندی IIS، درخواست‌های زنده و برخی لاگ‌ها", "IIS configuration, live requests and some logs",
            "RSM را با «Run as administrator» اجرا کنید (این کار فقط خود RSM را مجدداً اجرا می‌کند).", "Start RSM with \"Run as administrator\" (affects only RSM itself)."),
    ];

    private static readonly Dictionary<string, CapabilityDefinition> ById = All.ToDictionary(d => d.Id, StringComparer.Ordinal);

    public static CapabilityDefinition Get(string id) =>
        ById.TryGetValue(id, out var def) ? def : throw new KeyNotFoundException($"Unknown capability '{id}'.");

    public static LocalizedText DescribeState(CapabilityState state) => state switch
    {
        CapabilityState.Available => new("در دسترس", "Available"),
        CapabilityState.Denied => new("دسترسی ندارد", "Permission denied"),
        CapabilityState.NotInstalled => new("نصب نیست", "Not installed"),
        CapabilityState.Unreachable => new("در دسترس نیست (شبکه)", "Unreachable"),
        CapabilityState.NeedsElevation => new("نیاز به اجرای Administrator", "Needs elevation"),
        CapabilityState.NotApplicable => new("کاربرد ندارد", "Not applicable"),
        CapabilityState.NotConfigured => new("پیکربندی نشده", "Not configured"),
        CapabilityState.NotImplemented => new("در فازهای بعدی", "Arrives in a later phase"),
        CapabilityState.Error => new("خطا", "Error"),
        _ => new(state.ToString(), state.ToString()),
    };

    private static CapabilityDefinition Def(string id, Layer layer, string titleFa, string titleEn, string enablesFa, string enablesEn, string fixFa, string fixEn) =>
        new(id, layer, new(titleFa, titleEn), new(enablesFa, enablesEn), new(fixFa, fixEn));
}
