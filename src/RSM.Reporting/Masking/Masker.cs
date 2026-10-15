using System.Collections;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using RSM.Core.Snapshots;

namespace RSM.Reporting.Masking;

/// <summary>
/// "Mask sensitive data" for shared reports (spec §2.5, §13): site, node, host and server names, database names,
/// logins, job names, IPv4 addresses and UNC hosts become stable pseudonyms (SITE-1, SRV-1, DB-2, 10.x.x.7 …).
/// Built once per export from the snapshot and applied to every output — HTML text, and JSON values <em>and</em>
/// property names (instance fact keys such as <c>db[Rahkaran].state</c> contain database names).
/// </summary>
public sealed partial class Masker
{
    /// <summary>Names that identify nobody (system databases, built-in principals, loopback) — never masked.</summary>
    private static readonly HashSet<string> Public = new(StringComparer.OrdinalIgnoreCase)
    {
        "master", "model", "msdb", "tempdb", "sa", "localhost", "127.0.0.1", "::1", ".", "(local)",
        "NT Service", "NT AUTHORITY", "LocalSystem", "NT AUTHORITY\\SYSTEM", "NT AUTHORITY\\NETWORK SERVICE", "NT AUTHORITY\\LOCAL SERVICE",
        "MSSQLSERVER", "SQLSERVERAGENT",
    };

    private readonly Dictionary<string, string> _map = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _counters = new(StringComparer.Ordinal);
    private Regex? _tokens;

    private Masker(bool enabled) => IsEnabled = enabled;

    /// <summary>Pass-through masker for unmasked exports.</summary>
    public static Masker None { get; } = new(false);

    public bool IsEnabled { get; }

    /// <summary>Original → pseudonym. Never exported; useful to the operator who produced the report.</summary>
    public IReadOnlyDictionary<string, string> Map => _map;

    public static Masker From(SiteSnapshot snapshot)
    {
        var m = new Masker(true);
        m.Add(snapshot.Site.Name, "SITE");
        foreach (var name in snapshot.Site.Settings.RahkaranDatabases)
        {
            m.Add(name, "DB");
        }

        foreach (var node in snapshot.Nodes)
        {
            m.Add(node.Node.Name, "NODE");
            m.Add(node.Node.Host, "HOST");
            m.AddServer(node.Node.Sql?.Server);
            m.Add(node.Node.Sql?.UserName, "LOGIN");
            m.Add(node.Node.Redis?.Host, "HOST");
            m.Add(node.Node.RabbitMq?.ManagementUrl.Host, "HOST");
            m.Add(node.Node.Windows.UserName, "LOGIN");

            foreach (var source in new IReadOnlyDictionary<string, object?>[] { node.Facts, node.Capabilities.Facts.ToDictionary(p => p.Key, p => (object?)p.Value) })
            {
                m.AddServer(Text(source.GetValueOrDefault("sql.server_name")));
                m.AddServer(Text(source.GetValueOrDefault("sql.machine_name")));
                m.Add(Text(source.GetValueOrDefault("sql.login")), "LOGIN");
                m.Add(Text(source.GetValueOrDefault("ssrs.database")), "DB");
                m.Add(Text(source.GetValueOrDefault("sql.service.engine.account")), "LOGIN");
            }

            foreach (var (key, value) in node.Facts)
            {
                if (InstanceKey().Match(key) is { Success: true } match && match.Groups["prefix"].Value.Equals("db", StringComparison.OrdinalIgnoreCase))
                {
                    m.Add(match.Groups["instance"].Value, "DB");
                }

                if (key.Equals("sql.errorlog.failed_login_groups", StringComparison.OrdinalIgnoreCase))
                {
                    // "login (state 8) from 10.1.2.3 ×5" — the login is attacker-controlled text, mask it too.
                    foreach (var item in Items(value))
                    {
                        var cut = item?.IndexOf(" (state ", StringComparison.Ordinal) ?? -1;
                        m.Add(cut > 0 ? item![..cut] : null, "LOGIN");
                    }
                }

                var kind = KindOfList(key);
                if (kind is not null)
                {
                    foreach (var item in Items(value))
                    {
                        m.Add(item, kind);
                    }
                }
            }
        }

        return m;
    }

    /// <summary>Which pseudonym family the items of a list-valued fact belong to (null = not sensitive).</summary>
    internal static string? KindOfList(string key) => key switch
    {
        "rk.databases" => "DB",
        _ when key.EndsWith("job_names", StringComparison.OrdinalIgnoreCase) || key.StartsWith("agent.maint_plan", StringComparison.OrdinalIgnoreCase) && key.EndsWith("names", StringComparison.OrdinalIgnoreCase) => "JOB",
        _ when key.EndsWith("user_names", StringComparison.OrdinalIgnoreCase) || key.EndsWith("login_names", StringComparison.OrdinalIgnoreCase)
               || key.StartsWith("sec.", StringComparison.OrdinalIgnoreCase) && key.EndsWith("_names", StringComparison.OrdinalIgnoreCase) => "LOGIN",
        "sec.linked_servers" => "SRV",
        _ => null,
    };

    /// <summary><c>HOST\INSTANCE,1433</c> → the whole string, the host, and the named instance are all masked.</summary>
    private void AddServer(string? server)
    {
        if (string.IsNullOrWhiteSpace(server))
        {
            return;
        }

        var withoutPort = server.Split(',')[0].Trim();
        if (withoutPort.StartsWith("tcp:", StringComparison.OrdinalIgnoreCase))
        {
            withoutPort = withoutPort[4..];
        }

        Add(server, "SRV");
        Add(withoutPort, "SRV");
        var parts = withoutPort.Split('\\', 2);
        Add(parts[0], "HOST");
        if (parts.Length == 2 && !parts[1].Equals("MSSQLSERVER", StringComparison.OrdinalIgnoreCase))
        {
            Add(parts[1], "INST");
        }
    }

    private void Add(string? value, string kind)
    {
        value = value?.Trim();
        if (string.IsNullOrEmpty(value) || Public.Contains(value) || _map.ContainsKey(value) || Ipv4().IsMatch(value) && IsIp(Ipv4().Match(value))
            || value.StartsWith(@"NT Service\", StringComparison.OrdinalIgnoreCase) || value.StartsWith(@"NT AUTHORITY\", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var n = _counters[kind] = _counters.GetValueOrDefault(kind) + 1;
        _map[value] = $"{kind}-{n}";
        _tokens = null;

        // DOMAIN\user — the bare user (and the domain) identify people/organisations too.
        if (kind == "LOGIN" && value.Split('\\', 2) is [var domain, var user])
        {
            Add(user, "LOGIN");
            Add(domain, "DOMAIN");
        }
    }

    public string Apply(string? text)
    {
        if (string.IsNullOrEmpty(text) || !IsEnabled)
        {
            return text ?? string.Empty;
        }

        if (_map.Count > 0)
        {
            _tokens ??= new Regex(
                @"(?<![\p{L}\p{N}_])(?:" + string.Join('|', _map.Keys.OrderByDescending(k => k.Length).Select(Regex.Escape)) + @")(?![\p{L}\p{N}_])",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
            text = _tokens.Replace(text, m => _map.TryGetValue(m.Value, out var r) ? r : m.Value);
        }

        text = UncHost().Replace(text, m => @"\\" + (_map.TryGetValue(m.Groups["host"].Value, out var r) ? r : "HOST-X"));
        return Ipv4().Replace(text, m => IsIp(m) && !Public.Contains(m.Value) ? $"{m.Groups[1].Value}.x.x.{m.Groups[4].Value}" : m.Value);
    }

    /// <summary>Returns a masked deep copy of a JSON tree (the input is not modified).</summary>
    public JsonNode? Apply(JsonNode? node)
    {
        if (node is null)
        {
            return null;
        }

        if (!IsEnabled)
        {
            return node.DeepClone();
        }

        switch (node)
        {
            case JsonObject obj:
                var copy = new JsonObject();
                foreach (var (key, value) in obj)
                {
                    copy[Apply(key)] = Apply(value);
                }

                return copy;
            case JsonArray array:
                return new JsonArray(array.Select(Apply).ToArray());
            case JsonValue value when value.GetValueKind() == JsonValueKind.String:
                return JsonValue.Create(Apply(value.GetValue<string>()));
            default:
                return node.DeepClone();
        }
    }

    private static bool IsIp(Match m) => Enumerable.Range(1, 4).All(i => int.Parse(m.Groups[i].Value, System.Globalization.CultureInfo.InvariantCulture) <= 255);

    private static string? Text(object? value) => value switch
    {
        string s => s,
        JsonElement { ValueKind: JsonValueKind.String } e => e.GetString(),
        _ => null,
    };

    private static IEnumerable<string?> Items(object? value) => value switch
    {
        string => [],
        JsonElement { ValueKind: JsonValueKind.Array } e => e.EnumerateArray().Select(x => Text(x)),
        IEnumerable items => items.Cast<object?>().Select(Text),
        _ => [],
    };

    [GeneratedRegex(@"^(?<prefix>[A-Za-z_][\w.]*)\[(?<instance>[^\]]+)\]\.")]
    private static partial Regex InstanceKey();

    // Not preceded/followed by a digit or dot, so build numbers like 15.0.4430.1.2 are not split into an "address".
    [GeneratedRegex(@"(?<![\d.])(\d{1,3})\.(\d{1,3})\.(\d{1,3})\.(\d{1,3})(?![\d.])")]
    private static partial Regex Ipv4();

    [GeneratedRegex(@"\\\\(?<host>[^\\\s'""<>]+)")]
    private static partial Regex UncHost();
}
