using System.IO.Enumeration;
using RSM.Core.Model;
using RSM.Core.Profiles;

namespace RSM.Collectors.Windows;

/// <summary>Category of a watched service (spec §9.7).</summary>
public enum ServiceCategory
{
    SqlServer,
    SqlAgent,
    SqlBrowser,
    Ssrs,
    Iis,
    Redis,
    CodeMeter,
    RabbitMq,
    Attendance,
    ProactiveMonitor,
    WindowsUpdate,
    Antivirus,
}

/// <param name="Unverified">True when the pattern is a site setting whose product name is not confirmed (shown with ⚠).</param>
public sealed record WatchedService(ServiceCategory Category, string Pattern, bool Unverified = false);

/// <summary>One service as displayed. RSM shows services; it has no control that starts, stops or reconfigures them.</summary>
public sealed record ServiceInfo(
    string Name,
    string? DisplayName,
    ServiceCategory Category,
    string? State,
    string? StartMode,
    string? Account,
    int? ProcessId,
    DateTimeOffset? StartedUtc,
    bool Unverified)
{
    public bool IsRunning => string.Equals(State, "Running", StringComparison.OrdinalIgnoreCase);

    public bool IsAutomatic => string.Equals(StartMode, "Auto", StringComparison.OrdinalIgnoreCase);

    public bool AutomaticButStopped => IsAutomatic && string.Equals(State, "Stopped", StringComparison.OrdinalIgnoreCase);
}

public sealed class NoServiceState;

/// <summary>
/// Watched Windows services through CIM <c>Win32_Service</c> (queries only): state, start mode, account, PID and the
/// process start time (uptime → recent restarts). Display only (spec §9.7).
/// </summary>
public sealed class WindowsServicesCollector : WindowsCollectorBase<NoServiceState>
{
    public override string Id => "win.services";

    public override Layer Layer => Layer.Services;

    public override TimeSpan DefaultInterval => TimeSpan.FromMinutes(2);

    private static readonly CimQuery ServiceQuery = new("Win32_Service", ["Name", "DisplayName", "State", "StartMode", "StartName", "ProcessId"]);
    private static readonly CimQuery ProcessQuery = new("Win32_Process", ["ProcessId", "CreationDate"]);

    /// <summary>Built-in watch list (product service names stated in spec §9.7 / §11); site settings add the rest.</summary>
    public static IReadOnlyList<WatchedService> BuiltIn { get; } =
    [
        new(ServiceCategory.SqlServer, "MSSQLSERVER"),
        new(ServiceCategory.SqlServer, "MSSQL$*"),
        new(ServiceCategory.SqlAgent, "SQLSERVERAGENT"),
        new(ServiceCategory.SqlAgent, "SQLAgent$*"),
        new(ServiceCategory.SqlBrowser, "SQLBrowser"),
        new(ServiceCategory.Ssrs, "SQLServerReportingServices"),
        new(ServiceCategory.Ssrs, "ReportServer*"),
        new(ServiceCategory.Iis, "W3SVC"),
        new(ServiceCategory.Iis, "WAS"),
        new(ServiceCategory.Redis, "Redis*"),
        new(ServiceCategory.CodeMeter, "CodeMeter*"),
        new(ServiceCategory.RabbitMq, "RabbitMQ*"),
        new(ServiceCategory.WindowsUpdate, "wuauserv"),
    ];

    public static IReadOnlyList<WatchedService> WatchList(SiteSettings settings)
    {
        var list = BuiltIn.ToList();
        if (!string.IsNullOrWhiteSpace(settings.AttendanceServicePattern))
        {
            list.Add(new WatchedService(ServiceCategory.Attendance, settings.AttendanceServicePattern.Trim(), Unverified: true));
        }

        if (!string.IsNullOrWhiteSpace(settings.ProactiveMonitorServicePattern))
        {
            list.Add(new WatchedService(ServiceCategory.ProactiveMonitor, settings.ProactiveMonitorServicePattern.Trim(), Unverified: true));
        }

        list.AddRange(settings.AntivirusServicePatterns.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => new WatchedService(ServiceCategory.Antivirus, p.Trim())));
        return list;
    }

    protected override async Task CollectCoreAsync(INodeContext node, WindowsNodeTransport transport, NoServiceState state, LiveOutput output, List<string> problems, CancellationToken cancellationToken)
    {
        var rows = await TryQueryAsync(transport.Cim, ServiceQuery, problems, cancellationToken).ConfigureAwait(false);
        if (rows is null)
        {
            return;
        }

        var watched = Match(rows, WatchList(node.Site.Settings));
        var pids = watched.Where(s => s.ProcessId is > 0).Select(s => s.ProcessId!.Value).ToHashSet();
        IReadOnlyDictionary<int, DateTimeOffset> started = new Dictionary<int, DateTimeOffset>();
        if (pids.Count > 0 && await TryQueryAsync(transport.Cim, ProcessQuery, problems, cancellationToken).ConfigureAwait(false) is { } processes)
        {
            started = processes
                .Select(p => (Pid: (int?)p.Number("ProcessId"), At: p.Time("CreationDate")))
                .Where(p => p.Pid is { } id && pids.Contains(id) && p.At is not null)
                .GroupBy(p => p.Pid!.Value)
                .ToDictionary(g => g.Key, g => g.First().At!.Value);
        }

        Apply(watched.Select(s => s with { StartedUtc = s.ProcessId is { } pid && started.TryGetValue(pid, out var at) ? at : null }).ToList(), Clock.GetUtcNow(), output);
    }

    /// <summary>Watched services among all <c>Win32_Service</c> rows; a pattern matches the service name or display name.</summary>
    internal static List<ServiceInfo> Match(IReadOnlyList<CimRow> rows, IReadOnlyList<WatchedService> watchList)
    {
        var result = new List<ServiceInfo>();
        foreach (var row in rows)
        {
            var name = row.String("Name");
            if (name is null)
            {
                continue;
            }

            var display = row.String("DisplayName");
            var hit = watchList.FirstOrDefault(w => Matches(w.Pattern, name) || (display is not null && Matches(w.Pattern, display)));
            if (hit is null)
            {
                continue;
            }

            var pid = (int?)row.Number("ProcessId");
            result.Add(new ServiceInfo(name, display, hit.Category, row.String("State"), row.String("StartMode"), row.String("StartName"), pid is > 0 ? pid : null, null, hit.Unverified));
        }

        return result.OrderBy(s => s.Category).ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static bool Matches(string pattern, string value) => FileSystemName.MatchesSimpleExpression(pattern, value, ignoreCase: true);

    internal static void Apply(IReadOnlyList<ServiceInfo> services, DateTimeOffset now, LiveOutput output)
    {
        output.Payload["services"] = services;
        foreach (var s in services)
        {
            var key = $"svc[{s.Name}]";
            output.Fact($"{key}.state", s.State);
            output.Fact($"{key}.start_mode", s.StartMode);
            output.Fact($"{key}.category", s.Category.ToString());
            output.Fact($"{key}.auto_stopped", s.AutomaticButStopped);
            output.Fact($"{key}.unverified", s.Unverified);
            if (s.StartedUtc is { } at)
            {
                output.Fact($"{key}.uptime_s", Math.Max(0, (now - at).TotalSeconds));
            }
        }

        var autoStopped = services.Where(s => s.AutomaticButStopped && s.Category is not ServiceCategory.WindowsUpdate and not ServiceCategory.Attendance).ToList();
        output.Fact("svc.watched_count", services.Count);
        output.Fact("svc.auto_stopped_count", autoStopped.Count);
        output.Fact("svc.auto_stopped_names", string.Join(", ", autoStopped.Select(s => s.Name)));

        bool? Running(ServiceCategory c) => services.Any(s => s.Category == c) ? services.Any(s => s.Category == c && s.IsRunning) : null;
        output.Fact("svc.codemeter_present", services.Any(s => s.Category == ServiceCategory.CodeMeter));
        output.Fact("svc.codemeter_running", Running(ServiceCategory.CodeMeter) ?? false);
        output.Fact("svc.rabbitmq_present", services.Any(s => s.Category == ServiceCategory.RabbitMq));
        output.Fact("svc.rabbitmq_running", Running(ServiceCategory.RabbitMq) ?? false);
        output.Fact("svc.attendance_present", services.Any(s => s.Category == ServiceCategory.Attendance));
        output.Fact("svc.attendance_stopped", services.Any(s => s.Category == ServiceCategory.Attendance && !s.IsRunning));
        output.Fact("svc.sqlbrowser_running", Running(ServiceCategory.SqlBrowser) ?? false);
        output.Fact("svc.sql_instance_count", services.Count(s => s.Category == ServiceCategory.SqlServer));
        output.Fact("svc.sql_named_instance_count", services.Count(s => s.Category == ServiceCategory.SqlServer && s.Name.Contains('$', StringComparison.Ordinal)));
    }
}
