using System.Globalization;
using System.Xml.Linq;
using RSM.Parsers.Xml;

namespace RSM.Parsers.Iis;

public sealed record IisBinding(string Protocol, string BindingInformation, string? IpAddress, int? Port, string? Host, string? CertificateHash, string? CertificateStore);

public sealed record IisApplication(string Path, string? AppPool, string? PhysicalPath);

public sealed record IisSite(
    long Id,
    string Name,
    bool AutoStart,
    IReadOnlyList<IisBinding> Bindings,
    IReadOnlyList<IisApplication> Applications,
    bool LogEnabled,
    string? LogFormat,
    string? LogDirectory,
    IReadOnlyList<string> LogFields,
    string? RahkaranRole)
{
    public string? RootPhysicalPath => Applications.FirstOrDefault(a => a.Path == "/")?.PhysicalPath;

    public IEnumerable<string> AppPools => Applications.Select(a => a.AppPool).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase);

    /// <summary>W3C logs must contain <c>TimeTaken</c> for latency analysis (RSM never changes it).</summary>
    public bool LogsTimeTaken => LogFields.Contains("TimeTaken", StringComparer.OrdinalIgnoreCase);
}

/// <summary>An application pool with IIS defaults applied (pool attribute → applicationPoolDefaults → IIS schema default).</summary>
public sealed record IisAppPool(
    string Name,
    string ManagedRuntimeVersion,
    string PipelineMode,
    bool AutoStart,
    string StartMode,
    string IdentityType,
    string? IdentityUserName,
    TimeSpan IdleTimeout,
    TimeSpan PeriodicRestart,
    long RestartRequests,
    long PrivateMemoryLimitKb,
    long VirtualMemoryLimitKb,
    IReadOnlyList<TimeSpan> RestartSchedule,
    bool RapidFailProtection,
    int QueueLength,
    int MaxProcesses,
    int CpuLimit,
    string CpuAction,
    bool Enable32Bit,
    bool SmpAffinitized,
    string? ProcessorAffinityMask,
    string NumaNodeAssignment,
    string NumaNodeAffinityMode)
{
    /// <summary>"No Managed Code" (empty runtime) is expected for ASP.NET Core pools.</summary>
    public bool NoManagedCode => string.IsNullOrEmpty(ManagedRuntimeVersion);

    /// <summary>The settings compared across App nodes of a farm (config drift).</summary>
    public IReadOnlyDictionary<string, string> DriftSignature => new SortedDictionary<string, string>(StringComparer.Ordinal)
    {
        ["managedRuntimeVersion"] = ManagedRuntimeVersion,
        ["managedPipelineMode"] = PipelineMode,
        ["startMode"] = StartMode,
        ["identityType"] = IdentityType,
        ["idleTimeout"] = IdleTimeout.ToString("c", CultureInfo.InvariantCulture),
        ["periodicRestart"] = PeriodicRestart.ToString("c", CultureInfo.InvariantCulture),
        ["restartSchedule"] = string.Join(",", RestartSchedule.Select(t => t.ToString("c", CultureInfo.InvariantCulture))),
        ["privateMemoryLimitKb"] = PrivateMemoryLimitKb.ToString(CultureInfo.InvariantCulture),
        ["rapidFailProtection"] = RapidFailProtection ? "true" : "false",
        ["queueLength"] = QueueLength.ToString(CultureInfo.InvariantCulture),
        ["maxProcesses"] = MaxProcesses.ToString(CultureInfo.InvariantCulture),
        ["cpuLimit"] = CpuLimit.ToString(CultureInfo.InvariantCulture),
        ["enable32BitAppOnWin64"] = Enable32Bit ? "true" : "false",
        ["numaNodeAssignment"] = NumaNodeAssignment,
        ["numaNodeAffinityMode"] = NumaNodeAffinityMode,
    };
}

public sealed record FarmServer(string Address, bool Enabled, double Weight, int? HttpPort, int? HttpsPort);

public sealed record WebFarm(string Name, bool Enabled, string LoadBalancing, string? HealthCheckUrl, string? HealthCheckInterval, IReadOnlyList<FarmServer> Servers);

/// <summary>A URL Rewrite rule (global, site-level or from a web.config). <see cref="TargetFarm"/> is the farm a
/// rewrite to <c>http://&lt;farm&gt;/…</c> routes to, when it names one.</summary>
public sealed record RewriteRule(string Scope, string Name, bool Enabled, string? MatchUrl, IReadOnlyList<string> Conditions, string? ActionType, string? ActionUrl, bool StopProcessing)
{
    public string? TargetHost
    {
        get
        {
            if (ActionUrl is null || !ActionUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var start = ActionUrl.IndexOf("://", StringComparison.Ordinal) + 3;
            var end = ActionUrl.IndexOfAny(['/', ':', '{'], start);
            return end < 0 ? ActionUrl[start..] : ActionUrl[start..end];
        }
    }

    /// <summary>The first literal path segment after the host in the action URL ("/Rahkaran73/{R:1}" → "Rahkaran73").</summary>
    public string? TargetPathSegment
    {
        get
        {
            if (ActionUrl is null)
            {
                return null;
            }

            var path = ActionUrl;
            if (path.Contains("://", StringComparison.Ordinal))
            {
                var hostStart = path.IndexOf("://", StringComparison.Ordinal) + 3;
                var slash = path.IndexOf('/', hostStart);
                path = slash < 0 ? string.Empty : path[slash..];
            }

            var segment = path.TrimStart('/').Split('/', '?')[0];
            return segment.Length == 0 || segment.Contains('{', StringComparison.Ordinal) ? null : segment;
        }
    }
}

public sealed record ApplicationHostConfig(
    IReadOnlyList<IisSite> Sites,
    IReadOnlyList<IisAppPool> AppPools,
    IReadOnlyList<WebFarm> WebFarms,
    IReadOnlyList<RewriteRule> RewriteRules,
    bool ArrProxyEnabled);

/// <summary>
/// Reads <c>%windir%\system32\inetsrv\config\applicationHost.config</c> (read-only; untrusted XML through
/// <see cref="SafeXml"/>). Encrypted secrets in the file (pool identity passwords) are never read into the model.
/// IIS schema defaults are applied where the file does not set a value (IIS 10).
/// </summary>
public static class ApplicationHostConfigParser
{
    /// <summary>Default W3C fields of IIS 10 when a site does not set <c>logExtFileFlags</c>.</summary>
    public static readonly IReadOnlyList<string> DefaultLogFields =
        ["Date", "Time", "ClientIP", "UserName", "ServerIP", "Method", "UriStem", "UriQuery", "HttpStatus", "Win32Status", "TimeTaken", "ServerPort", "UserAgent", "Referer", "HttpSubStatus"];

    public static ApplicationHostConfig Parse(string xml, IReadOnlyList<RolePattern>? rolePatterns = null)
    {
        var doc = SafeXml.Parse(xml);
        var root = doc.Root ?? throw new FormatException("applicationHost.config has no root element");
        var appHost = root.Element("system.applicationHost");
        var pools = ParsePools(appHost?.Element("applicationPools"));
        var sites = ParseSites(appHost?.Element("sites"), rolePatterns ?? RolePattern.Defaults);
        var farms = root.Element("webFarms")?.Elements("webFarm").Select(ParseFarm).ToList() ?? [];

        var rules = new List<RewriteRule>();
        rules.AddRange(ParseRules(root.Element("system.webServer")?.Element("rewrite")?.Element("globalRules"), "global"));
        foreach (var location in root.Elements("location"))
        {
            var scope = (string?)location.Attribute("path") is { Length: > 0 } path ? path : "global";
            rules.AddRange(ParseRules(location.Element("system.webServer")?.Element("rewrite")?.Element("globalRules"), scope));
            rules.AddRange(ParseRules(location.Element("system.webServer")?.Element("rewrite")?.Element("rules"), scope));
        }

        var proxy = root.Element("system.webServer")?.Element("proxy");
        return new ApplicationHostConfig(sites, pools, farms, rules, Bool(proxy?.Attribute("enabled"), false));
    }

    private static List<IisAppPool> ParsePools(XElement? pools)
    {
        var defaults = pools?.Element("applicationPoolDefaults");
        return pools?.Elements("add").Select(p => ParsePool(p, defaults)).ToList() ?? [];
    }

    private static IisAppPool ParsePool(XElement pool, XElement? defaults)
    {
        string Attr(string name, string fallback) => (string?)pool.Attribute(name) ?? (string?)defaults?.Attribute(name) ?? fallback;
        string Child(string element, string name, string fallback) =>
            (string?)pool.Element(element)?.Attribute(name) ?? (string?)defaults?.Element(element)?.Attribute(name) ?? fallback;
        string Nested(string element, string inner, string name, string fallback) =>
            (string?)pool.Element(element)?.Element(inner)?.Attribute(name) ?? (string?)defaults?.Element(element)?.Element(inner)?.Attribute(name) ?? fallback;

        var schedule = (pool.Element("recycling")?.Element("periodicRestart")?.Element("schedule")
                        ?? defaults?.Element("recycling")?.Element("periodicRestart")?.Element("schedule"))?
            .Elements("add").Select(a => Time((string?)a.Attribute("value"), TimeSpan.Zero)).ToList() ?? [];

        return new IisAppPool(
            (string?)pool.Attribute("name") ?? "?",
            Attr("managedRuntimeVersion", "v4.0"),
            Attr("managedPipelineMode", "Integrated"),
            Bool(Attr("autoStart", "true")),
            Attr("startMode", "OnDemand"),
            Child("processModel", "identityType", "ApplicationPoolIdentity"),
            (string?)pool.Element("processModel")?.Attribute("userName"),
            Time(Child("processModel", "idleTimeout", "00:20:00"), TimeSpan.FromMinutes(20)),
            Time(Nested("recycling", "periodicRestart", "time", "1.05:00:00"), TimeSpan.FromMinutes(1740)),
            Long(Nested("recycling", "periodicRestart", "requests", "0")),
            Long(Nested("recycling", "periodicRestart", "privateMemory", "0")),
            Long(Nested("recycling", "periodicRestart", "memory", "0")),
            schedule,
            Bool(Child("failure", "rapidFailProtection", "true")),
            (int)Long(Attr("queueLength", "1000")),
            (int)Long(Child("processModel", "maxProcesses", "1")),
            (int)Long(Child("cpu", "limit", "0")),
            Child("cpu", "action", "NoAction"),
            Bool(Attr("enable32BitAppOnWin64", "false")),
            Bool(Child("cpu", "smpAffinitized", "false")),
            (string?)pool.Element("cpu")?.Attribute("smpProcessorAffinityMask") ?? (string?)defaults?.Element("cpu")?.Attribute("smpProcessorAffinityMask"),
            Child("cpu", "numaNodeAssignment", "MostAvailableMemory"),
            Child("cpu", "numaNodeAffinityMode", "Soft"));
    }

    private static List<IisSite> ParseSites(XElement? sites, IReadOnlyList<RolePattern> patterns)
    {
        if (sites is null)
        {
            return [];
        }

        var defaults = sites.Element("siteDefaults")?.Element("logFile");
        var appDefaults = sites.Element("applicationDefaults");
        return sites.Elements("site").Select(site =>
        {
            var name = (string?)site.Attribute("name") ?? "?";
            var log = site.Element("logFile");
            var fields = ((string?)log?.Attribute("logExtFileFlags") ?? (string?)defaults?.Attribute("logExtFileFlags"))?
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList() ?? DefaultLogFields.ToList();
            var apps = site.Elements("application").Select(app => new IisApplication(
                (string?)app.Attribute("path") ?? "/",
                (string?)app.Attribute("applicationPool") ?? (string?)site.Element("applicationDefaults")?.Attribute("applicationPool")
                    ?? (string?)appDefaults?.Attribute("applicationPool") ?? "DefaultAppPool",
                (string?)app.Elements("virtualDirectory").FirstOrDefault(v => (string?)v.Attribute("path") == "/")?.Attribute("physicalPath"))).ToList();
            return new IisSite(
                Long((string?)site.Attribute("id") ?? "0"),
                name,
                Bool((string?)site.Attribute("serverAutoStart") ?? "true"),
                site.Element("bindings")?.Elements("binding").Select(ParseBinding).ToList() ?? [],
                apps,
                Bool((string?)log?.Attribute("enabled") ?? (string?)defaults?.Attribute("enabled") ?? "true"),
                (string?)log?.Attribute("logFormat") ?? (string?)defaults?.Attribute("logFormat") ?? "W3C",
                (string?)log?.Attribute("directory") ?? (string?)defaults?.Attribute("directory") ?? @"%SystemDrive%\inetpub\logs\LogFiles",
                fields,
                patterns.FirstOrDefault(p => p.Matches(name))?.Role);
        }).ToList();
    }

    private static IisBinding ParseBinding(XElement binding)
    {
        var protocol = (string?)binding.Attribute("protocol") ?? "http";
        var info = (string?)binding.Attribute("bindingInformation") ?? string.Empty;
        string? ip = null, host = null;
        int? port = null;
        if (protocol is "http" or "https")
        {
            // "ip:port:host"; IPv6 addresses are bracketed.
            var lastColon = info.LastIndexOf(':');
            var portColon = lastColon > 0 ? info.LastIndexOf(':', lastColon - 1) : -1;
            if (portColon >= 0)
            {
                ip = info[..portColon];
                port = int.TryParse(info[(portColon + 1)..lastColon], NumberStyles.Integer, CultureInfo.InvariantCulture, out var p) ? p : null;
                host = info[(lastColon + 1)..];
            }
        }

        return new IisBinding(protocol, info, ip is "*" or "" ? null : ip, port, string.IsNullOrEmpty(host) ? null : host,
            (string?)binding.Attribute("certificateHash"), (string?)binding.Attribute("certificateStoreName"));
    }

    private static WebFarm ParseFarm(XElement farm)
    {
        var arr = farm.Element("applicationRequestRouting");
        return new WebFarm(
            (string?)farm.Attribute("name") ?? "?",
            Bool((string?)farm.Attribute("enabled") ?? "true"),
            (string?)arr?.Element("loadBalancing")?.Attribute("algorithm") ?? "WeightedRoundRobin",
            (string?)arr?.Element("healthCheck")?.Attribute("url"),
            (string?)arr?.Element("healthCheck")?.Attribute("interval"),
            farm.Elements("server").Select(s =>
            {
                var sArr = s.Element("applicationRequestRouting");
                return new FarmServer(
                    (string?)s.Attribute("address") ?? "?",
                    Bool((string?)s.Attribute("enabled") ?? "true"),
                    double.TryParse((string?)sArr?.Attribute("weight"), NumberStyles.Float, CultureInfo.InvariantCulture, out var w) ? w : 100,
                    int.TryParse((string?)sArr?.Attribute("httpPort"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var hp) ? hp : null,
                    int.TryParse((string?)sArr?.Attribute("httpsPort"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var hsp) ? hsp : null);
            }).ToList());
    }

    internal static IEnumerable<RewriteRule> ParseRules(XElement? rules, string scope) =>
        rules?.Elements("rule").Select(rule => new RewriteRule(
            scope,
            (string?)rule.Attribute("name") ?? "?",
            Bool((string?)rule.Attribute("enabled") ?? "true"),
            (string?)rule.Element("match")?.Attribute("url"),
            rule.Element("conditions")?.Elements("add").Select(c => $"{(string?)c.Attribute("input")} ~ {(string?)c.Attribute("pattern")}").ToList() ?? [],
            (string?)rule.Element("action")?.Attribute("type"),
            (string?)rule.Element("action")?.Attribute("url"),
            Bool((string?)rule.Attribute("stopProcessing") ?? "false"))) ?? [];

    private static bool Bool(XAttribute? attribute, bool fallback) => attribute is null ? fallback : Bool((string)attribute);

    private static bool Bool(string value) => string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);

    private static long Long(string value) => long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;

    private static TimeSpan Time(string? value, TimeSpan fallback) =>
        TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out var t) ? t : fallback;
}

/// <summary>
/// Site-name pattern → Rahkaran role (spec §10.1: AuxWebsiteN, AppRouterWebsite, ES-CS, Mobile). Configurable per
/// site profile; the defaults are the names the spec states, matched case-insensitively (* = any characters).
/// </summary>
public sealed record RolePattern(string Pattern, string Role)
{
    public static readonly IReadOnlyList<RolePattern> Defaults =
    [
        new("AuxWebsite*", "Aux"),
        new("AppRouterWebsite*", "AppRouter"),
        new("ES-CS*", "ES-CS"),
        new("*Mobile*", "Mobile"),
    ];

    public bool Matches(string name) => System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(Pattern, name, ignoreCase: true);
}
