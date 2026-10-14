using System.Text.RegularExpressions;
using Serilog.Core;
using Serilog.Events;

namespace RSM.Safety.Logging;

/// <summary>Removes secrets from text: connection-string passwords, user ids, AUTH arguments, bearer/basic tokens.</summary>
public static partial class Redactor
{
    public const string Mask = "***";

    private static readonly string[] SecretNameFragments = ["password", "pwd", "secret", "token", "apikey", "api_key", "credential", "auth"];

    /// <summary>True for property/field names that must never be logged with their value.</summary>
    public static bool IsSecretName(string name)
    {
        var lower = name.ToLowerInvariant();
        return SecretNameFragments.Any(lower.Contains) || lower == "key" || lower.EndsWith("key", StringComparison.Ordinal)
            || lower.Contains("connectionstring", StringComparison.Ordinal);
    }

    public static string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text ?? string.Empty;
        }

        var result = ConnectionStringSecret().Replace(text, m => $"{m.Groups["name"].Value}={Mask}");
        result = AuthCommand().Replace(result, m => $"{m.Groups["cmd"].Value} {Mask}");
        result = HttpAuthorization().Replace(result, m => $"{m.Groups["scheme"].Value} {Mask}");
        return result;
    }

    // Password=..., Pwd=..., User ID=..., UID=..., Token=... inside connection strings (value up to ; or end).
    [GeneratedRegex(@"(?<name>\b(?:password|pwd|user\s*id|uid|token|secret|access\s*key)\b)\s*=\s*(?:""[^""]*""|'[^']*'|[^;]*)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ConnectionStringSecret();

    [GeneratedRegex(@"(?<cmd>\bAUTH\b)\s+\S+(?:\s+\S+)?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AuthCommand();

    [GeneratedRegex(@"(?<scheme>\b(?:Basic|Bearer))\s+[A-Za-z0-9+/=._-]{6,}", RegexOptions.CultureInvariant)]
    private static partial Regex HttpAuthorization();
}

/// <summary>
/// Serilog enricher applied to every sink: properties with secret-like names are replaced by "***" and all
/// string values are passed through <see cref="Redactor"/>.
/// </summary>
public sealed class RedactionEnricher : ILogEventEnricher
{
    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        foreach (var (name, value) in logEvent.Properties.ToList())
        {
            var redacted = Redactor.IsSecretName(name) ? new ScalarValue(Redactor.Mask) : RedactValue(value);
            if (!ReferenceEquals(redacted, value))
            {
                logEvent.AddOrUpdateProperty(new LogEventProperty(name, redacted));
            }
        }
    }

    private static LogEventPropertyValue RedactValue(LogEventPropertyValue value) => value switch
    {
        ScalarValue { Value: string s } => Redactor.Redact(s) is var r && r != s ? new ScalarValue(r) : value,
        SequenceValue seq => new SequenceValue(seq.Elements.Select(RedactValue)),
        StructureValue st => new StructureValue(
            st.Properties.Select(p => new LogEventProperty(p.Name, Redactor.IsSecretName(p.Name) ? new ScalarValue(Redactor.Mask) : RedactValue(p.Value))),
            st.TypeTag),
        DictionaryValue dict => new DictionaryValue(dict.Elements.Select(kv => new KeyValuePair<ScalarValue, LogEventPropertyValue>(
            kv.Key, kv.Key.Value is string k && Redactor.IsSecretName(k) ? new ScalarValue(Redactor.Mask) : RedactValue(kv.Value)))),
        _ => value,
    };
}
