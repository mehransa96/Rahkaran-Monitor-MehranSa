using System.Data.Common;
using System.Text.RegularExpressions;
using RSM.Parsers.Xml;

namespace RSM.Parsers.Iis;

public sealed record NameValue(string Name, string Value);

/// <summary>A connection string reduced to its target (server / database); credentials never leave the parser.</summary>
public sealed record ConnectionTarget(string Name, string? Server, string? Database, bool IntegratedSecurity, bool HadCredentials);

/// <summary>
/// What RSM shows of a web.config (spec §10.8). Every value has passed <see cref="SecretMasker"/>: connection strings
/// are reduced to server/database, and any setting whose name or content looks secret is replaced by a mask.
/// </summary>
public sealed record WebConfigSummary(
    string? HostingModel,
    string? ProcessPath,
    string? Arguments,
    bool? StdoutLogEnabled,
    string? StdoutLogFile,
    IReadOnlyList<NameValue> EnvironmentVariables,
    IReadOnlyList<ConnectionTarget> ConnectionStrings,
    IReadOnlyList<NameValue> AppSettings,
    IReadOnlyList<string> RedisAddresses,
    long? MaxAllowedContentLength,
    int? MaxRequestLengthKb,
    int? ExecutionTimeoutSeconds,
    IReadOnlyList<RewriteRule> RewriteRules)
{
    /// <summary>Values compared across App nodes (drift); secrets are already masked so masks compare equal.</summary>
    public IReadOnlyDictionary<string, string> DriftSignature
    {
        get
        {
            var d = new SortedDictionary<string, string>(StringComparer.Ordinal)
            {
                ["hostingModel"] = HostingModel ?? string.Empty,
                ["processPath"] = ProcessPath ?? string.Empty,
                ["arguments"] = Arguments ?? string.Empty,
                ["stdoutLogEnabled"] = StdoutLogEnabled?.ToString() ?? string.Empty,
                ["maxAllowedContentLength"] = MaxAllowedContentLength?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
            };
            foreach (var env in EnvironmentVariables)
            {
                d["env:" + env.Name] = env.Value;
            }

            foreach (var cs in ConnectionStrings)
            {
                d["cs:" + cs.Name] = $"{cs.Server}/{cs.Database}";
            }

            return d;
        }
    }
}

public static class WebConfigParser
{
    public static WebConfigSummary Parse(string xml)
    {
        var doc = SafeXml.Parse(xml, 4 * 1024 * 1024);
        var root = doc.Root ?? throw new FormatException("web.config has no root element");
        // web.config may wrap settings in <location path="."> (dotnet publish does).
        var webServer = root.Element("system.webServer") ?? root.Elements("location").Select(l => l.Element("system.webServer")).FirstOrDefault(e => e is not null);
        var ancm = webServer?.Element("aspNetCore");
        var env = ancm?.Element("environmentVariables")?.Elements("environmentVariable")
            .Select(e => new NameValue((string?)e.Attribute("name") ?? "?", SecretMasker.MaskSetting((string?)e.Attribute("name"), (string?)e.Attribute("value")))).ToList() ?? [];

        var connectionStrings = root.Element("connectionStrings")?.Elements("add")
            .Select(e => SecretMasker.Target((string?)e.Attribute("name") ?? "?", (string?)e.Attribute("connectionString"))).ToList() ?? [];
        var appSettingsRaw = root.Element("appSettings")?.Elements("add")
            .Select(e => ((string?)e.Attribute("key") ?? "?", (string?)e.Attribute("value"))).ToList() ?? [];

        var redis = appSettingsRaw.Where(a => a.Item1.Contains("redis", StringComparison.OrdinalIgnoreCase))
            .Concat(root.Element("connectionStrings")?.Elements("add")
                .Where(e => ((string?)e.Attribute("name"))?.Contains("redis", StringComparison.OrdinalIgnoreCase) == true)
                .Select(e => ((string?)e.Attribute("name") ?? "?", (string?)e.Attribute("connectionString"))) ?? [])
            .Concat(env.Where(e => e.Name.Contains("redis", StringComparison.OrdinalIgnoreCase)).Select(e => (e.Name, (string?)e.Value)))
            .Select(a => SecretMasker.RedisAddress(a.Item2))
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var limits = webServer?.Element("security")?.Element("requestFiltering")?.Element("requestLimits");
        var httpRuntime = root.Element("system.web")?.Element("httpRuntime");
        return new WebConfigSummary(
            (string?)ancm?.Attribute("hostingModel") ?? (ancm is null ? null : "OutOfProcess"),
            (string?)ancm?.Attribute("processPath"),
            SecretMasker.MaskInline((string?)ancm?.Attribute("arguments")),
            ancm?.Attribute("stdoutLogEnabled") is { } s ? string.Equals((string)s, "true", StringComparison.OrdinalIgnoreCase) : null,
            (string?)ancm?.Attribute("stdoutLogFile"),
            env,
            connectionStrings,
            appSettingsRaw.Select(a => new NameValue(a.Item1, SecretMasker.MaskSetting(a.Item1, a.Item2))).ToList(),
            redis,
            long.TryParse((string?)limits?.Attribute("maxAllowedContentLength"), out var max) ? max : null,
            int.TryParse((string?)httpRuntime?.Attribute("maxRequestLength"), out var mrl) ? mrl : null,
            TimeSpan.TryParse((string?)httpRuntime?.Attribute("executionTimeout"), System.Globalization.CultureInfo.InvariantCulture, out var et) ? (int)et.TotalSeconds : null,
            ApplicationHostConfigParser.ParseRules(webServer?.Element("rewrite")?.Element("rules"), "web.config").ToList());
    }
}

/// <summary>Masking of secrets in configuration values (spec §10.8: password|pwd|secret|token|key).</summary>
public static partial class SecretMasker
{
    public const string Mask = "••••••";

    public static bool IsSecretName(string? name) => name is not null && SecretName().IsMatch(name);

    /// <summary>A setting value as shown: masked when its name looks secret or the value carries credentials.</summary>
    public static string MaskSetting(string? name, string? value)
    {
        if (value is null)
        {
            return string.Empty;
        }

        if (IsSecretName(name))
        {
            return Mask;
        }

        if (LooksLikeConnectionString(value))
        {
            var target = Target(name ?? string.Empty, value);
            return $"Server={target.Server}; Database={target.Database}{(target.HadCredentials ? "; " + Mask : string.Empty)}";
        }

        return MaskInline(value) ?? string.Empty;
    }

    /// <summary>Masks "key=value" / "--key value" pairs whose key looks secret inside free text (arguments, URLs).</summary>
    public static string? MaskInline(string? text)
    {
        if (text is null)
        {
            return null;
        }

        var masked = InlinePair().Replace(text, m => $"{m.Groups["k"].Value}{m.Groups["sep"].Value}{Mask}");
        return UrlCredentials().Replace(masked, m => $"{m.Groups["scheme"].Value}{Mask}@");
    }

    public static ConnectionTarget Target(string name, string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return new ConnectionTarget(name, null, null, false, false);
        }

        try
        {
            var builder = new DbConnectionStringBuilder { ConnectionString = connectionString };
            string? Get(params string[] keys) => keys.Select(k => builder.TryGetValue(k, out var v) ? v?.ToString() : null).FirstOrDefault(v => !string.IsNullOrEmpty(v));
            var integrated = Get("integrated security", "trusted_connection") is { } i && (i.Equals("true", StringComparison.OrdinalIgnoreCase) || i.Equals("sspi", StringComparison.OrdinalIgnoreCase) || i.Equals("yes", StringComparison.OrdinalIgnoreCase));
            var credentials = builder.Keys.Cast<string>().Any(k => IsSecretName(k) || k.Equals("user id", StringComparison.OrdinalIgnoreCase) || k.Equals("uid", StringComparison.OrdinalIgnoreCase) || k.Equals("user", StringComparison.OrdinalIgnoreCase));
            return new ConnectionTarget(name, Get("server", "data source", "address", "addr", "network address", "host"), Get("database", "initial catalog"), integrated, credentials);
        }
        catch (ArgumentException)
        {
            // Not a key=value connection string (e.g. a Redis "host:port,password=…"): keep only what precedes the first comma.
            var head = connectionString.Split(',')[0];
            return new ConnectionTarget(name, IsSecretName(head) || head.Contains('=', StringComparison.Ordinal) ? null : head, null, false, true);
        }
    }

    /// <summary>"redis01:6379,password=…,ssl=false" → "redis01:6379".</summary>
    public static string? RedisAddress(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var head = value.Split(',')[0].Trim();
        return head.Contains('=', StringComparison.Ordinal) || IsSecretName(head) ? null : head;
    }

    private static bool LooksLikeConnectionString(string value) =>
        value.Contains(';', StringComparison.Ordinal) && value.Contains('=', StringComparison.Ordinal)
        && (value.Contains("server", StringComparison.OrdinalIgnoreCase) || value.Contains("data source", StringComparison.OrdinalIgnoreCase)
            || value.Contains("password", StringComparison.OrdinalIgnoreCase) || value.Contains("pwd", StringComparison.OrdinalIgnoreCase));

    [GeneratedRegex("password|pwd|secret|token|key|credential|passphrase|apikey|connectionstring", RegexOptions.IgnoreCase)]
    private static partial Regex SecretName();

    [GeneratedRegex(@"(?<k>[A-Za-z0-9_\-.]*(?:password|pwd|secret|token|key|credential)[A-Za-z0-9_\-.]*)(?<sep>\s*[=:]\s*|\s+)(?<v>""[^""]*""|'[^']*'|[^\s;&,]+)", RegexOptions.IgnoreCase)]
    private static partial Regex InlinePair();

    [GeneratedRegex(@"(?<scheme>[a-z][a-z0-9+.\-]*://)[^/\s:@]+:[^/\s@]+@", RegexOptions.IgnoreCase)]
    private static partial Regex UrlCredentials();
}
