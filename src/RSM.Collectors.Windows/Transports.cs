using System.Globalization;
using System.Text.RegularExpressions;

namespace RSM.Collectors.Windows;

/// <summary>
/// A CIM/WMI query built only from identifiers fixed in code (class, properties) and a constant filter. There is no
/// way to express a method call, an instance change or free text: <see cref="ToWql"/> validates every part.
/// </summary>
public sealed partial record CimQuery(string ClassName, IReadOnlyList<string> Properties, string? Where = null, string Namespace = @"root\cimv2")
{
    public string ToWql()
    {
        if (!NamespacePattern().IsMatch(Namespace) || !IdentifierPattern().IsMatch(ClassName) || Properties.Count == 0
            || Properties.Any(p => !IdentifierPattern().IsMatch(p)) || (Where is not null && !WherePattern().IsMatch(Where)))
        {
            throw new ArgumentException($"Invalid CIM query for {ClassName}.");
        }

        var wql = $"SELECT {string.Join(", ", Properties)} FROM {ClassName}";
        return Where is null ? wql : $"{wql} WHERE {Where}";
    }

    [GeneratedRegex(@"^[A-Za-z0-9_]{1,128}$")]
    private static partial Regex IdentifierPattern();

    [GeneratedRegex(@"^root(\\[A-Za-z0-9_]{1,64}){1,4}$")]
    private static partial Regex NamespacePattern();

    // Constant filters such as "Name = '_Total'" or "IsActive = TRUE"; no parentheses, semicolons or keywords beyond AND/OR/NOT.
    [GeneratedRegex(@"^[A-Za-z0-9_ ='<>!%.:\-]{1,200}$")]
    private static partial Regex WherePattern();
}

/// <summary>One CIM instance as property name → value (case-insensitive).</summary>
public sealed class CimRow(IDictionary<string, object?> values)
{
    private readonly Dictionary<string, object?> _values = new(values, StringComparer.OrdinalIgnoreCase);

    public object? this[string property] => _values.GetValueOrDefault(property);

    public string? String(string property) => this[property] switch
    {
        null => null,
        string s => s,
        var v => Convert.ToString(v, CultureInfo.InvariantCulture),
    };

    public double? Number(string property) => this[property] switch
    {
        null => null,
        bool b => b ? 1 : 0,
        IConvertible c when c is not string && c is not DateTime => c.ToDouble(CultureInfo.InvariantCulture),
        string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => d,
        _ => null,
    };

    public bool? Bool(string property) => this[property] switch
    {
        null => null,
        bool b => b,
        var v => Number(property) is { } d ? d != 0 : null,
    };

    public DateTimeOffset? Time(string property) => this[property] switch
    {
        DateTime dt => new DateTimeOffset(dt.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(dt, DateTimeKind.Local) : dt).ToUniversalTime(),
        DateTimeOffset dto => dto.ToUniversalTime(),
        _ => null,
    };

    public static CimRow Of(params (string Name, object? Value)[] values) => new(values.ToDictionary(v => v.Name, v => v.Value));
}

/// <summary>Runs read-only CIM queries against one machine (local or remote).</summary>
public interface ICimQuery
{
    /// <summary>"local", "wsman" or "dcom" (shown in the Capability Matrix).</summary>
    string Protocol { get; }

    Task<IReadOnlyList<CimRow>> QueryAsync(CimQuery query, CancellationToken cancellationToken);
}

/// <summary>A counter value with its RSM metric key — identical keys whatever the transport (PDH locally, CIM remotely).</summary>
public sealed record MetricReading(string Key, string? Instance, double Value);

/// <summary>Reads every counter of <see cref="WindowsMetricCatalog"/> from one machine.</summary>
public interface IWindowsCounters
{
    /// <summary>"pdh" or "cim" (shown in the Capability Matrix).</summary>
    string Transport { get; }

    Task<IReadOnlyList<MetricReading>> ReadAsync(CancellationToken cancellationToken);
}

/// <summary>Reads values from the fixed whitelist in <see cref="RegistryWhitelist"/> (read-only keys only).</summary>
public interface IRegistryValues
{
    /// <summary>Returns the value, null when absent, or throws <see cref="UnauthorizedAccessException"/>/<see cref="IOException"/>.</summary>
    object? Read(RegistryValueId id);

    bool KeyExists(RegistryValueId id);
}

/// <summary>
/// An event-log read: one log, events newer than <see cref="SinceUtc"/>, either problems (critical/error/warning) plus
/// the listed ids, or only the listed ids. <see cref="ToXPath"/> builds the filter from integers and a validated log
/// name only — there is no free text.
/// </summary>
public sealed partial record EventQuery(string Log, DateTimeOffset SinceUtc, IReadOnlyList<int> Ids, bool IncludeProblems, int MaxEvents)
{
    public string ToXPath()
    {
        if (!LogNamePattern().IsMatch(Log) || (Ids.Count == 0 && !IncludeProblems) || Ids.Count > 40 || Ids.Any(i => i is < 0 or > 65535) || MaxEvents is < 1 or > 20000)
        {
            throw new ArgumentException($"Invalid event query for {Log}.");
        }

        var terms = new List<string>();
        if (IncludeProblems)
        {
            terms.AddRange(["Level=1", "Level=2", "Level=3"]);
        }

        terms.AddRange(Ids.Distinct().Order().Select(i => $"EventID={i.ToString(CultureInfo.InvariantCulture)}"));
        var since = SinceUtc.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);
        return $"*[System[({string.Join(" or ", terms)}) and TimeCreated[@SystemTime>'{since}']]]";
    }

    /// <summary>Channel names such as "System" or "Microsoft-Windows-IIS-Configuration/Operational".</summary>
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9 _\-]{0,100}(/[A-Za-z0-9 _\-]{1,60})?$")]
    private static partial Regex LogNamePattern();
}

/// <summary>Reads events (newest first) through <c>System.Diagnostics.Eventing.Reader</c>; never writes, clears or exports.</summary>
public interface IEventLogSource
{
    /// <summary>Returns the events and whether <see cref="EventQuery.MaxEvents"/> cut the result short.</summary>
    Task<(IReadOnlyList<RSM.Parsers.Events.EventRecordData> Events, bool Truncated)> ReadAsync(EventQuery query, CancellationToken cancellationToken);
}

/// <summary>Everything RSM can read from one Windows node; created by the host, never by a collector.</summary>
public sealed class WindowsNodeTransport(ICimQuery cim, IWindowsCounters counters, IRegistryValues? registry, bool isLocal, IEventLogSource? events = null) : IDisposable
{
    /// <summary>Null when the event logs could not be opened (Event Log Readers membership / firewall rule).</summary>
    public IEventLogSource? Events { get; } = events;

    public ICimQuery Cim { get; } = cim;

    public IWindowsCounters Counters { get; } = counters;

    /// <summary>Null when the registry is unreachable (remote registry needs the current user's rights).</summary>
    public IRegistryValues? Registry { get; } = registry;

    public bool IsLocal { get; } = isLocal;

    public void Dispose()
    {
        (Counters as IDisposable)?.Dispose();
        if (!ReferenceEquals(Cim, Counters))
        {
            (Cim as IDisposable)?.Dispose();
        }

        (Registry as IDisposable)?.Dispose();
        (Events as IDisposable)?.Dispose();
    }
}
