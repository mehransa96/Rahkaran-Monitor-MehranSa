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

/// <summary>Everything RSM can read from one Windows node; created by the host, never by a collector.</summary>
public sealed class WindowsNodeTransport(ICimQuery cim, IWindowsCounters counters, IRegistryValues? registry, bool isLocal) : IDisposable
{
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
    }
}
