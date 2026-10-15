using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using RSM.Parsers.Xml;

namespace RSM.Parsers.Sql;

public sealed record DeadlockProcess(
    string Id,
    int? Spid,
    bool IsVictim,
    string? Login,
    string? Host,
    string? Application,
    string? Database,
    string? LockMode,
    string? WaitResource,
    long? WaitMs,
    string? IsolationLevel,
    string? TransactionName,
    string? Statement,
    IReadOnlyList<string> Frames);

public sealed record DeadlockLockParty(string ProcessId, string? Mode, string? RequestType);

public sealed record DeadlockResource(
    string Kind,
    string? Database,
    string? ObjectName,
    string? IndexName,
    string? Mode,
    IReadOnlyList<DeadlockLockParty> Owners,
    IReadOnlyList<DeadlockLockParty> Waiters);

/// <summary>A parsed <c>xml_deadlock_report</c>: who was chosen as victim, what each process ran and which locks collided.</summary>
public sealed record DeadlockGraph(
    string Id,
    DateTimeOffset? AtUtc,
    IReadOnlyList<DeadlockProcess> Processes,
    IReadOnlyList<DeadlockResource> Resources)
{
    public IEnumerable<DeadlockProcess> Victims => Processes.Where(p => p.IsVictim);
}

/// <summary>
/// Parses deadlock reports read from the built-in <c>system_health</c> session (event files or ring buffer). No XE
/// session is created or altered; the XML is untrusted input and goes through <see cref="SafeXml"/>.
/// </summary>
public static class DeadlockReport
{
    /// <summary>One <c>&lt;event name="xml_deadlock_report"&gt;</c> (file target) or a whole ring-buffer target.</summary>
    public static IReadOnlyList<DeadlockGraph> Parse(string xml)
    {
        var doc = SafeXml.TryParse(xml);
        if (doc?.Root is null)
        {
            return [];
        }

        var events = doc.Root.Name.LocalName == "event"
            ? [doc.Root]
            : doc.Root.Descendants("event").ToList();
        var graphs = new List<DeadlockGraph>();
        foreach (var e in events.Where(e => (string?)e.Attribute("name") == "xml_deadlock_report"))
        {
            var deadlock = e.Descendants("deadlock").FirstOrDefault();
            if (deadlock is not null)
            {
                graphs.Add(ParseGraph(deadlock, ParseTime((string?)e.Attribute("timestamp"))));
            }
        }

        // A bare <deadlock> document (e.g. saved from SSMS) is accepted too.
        if (graphs.Count == 0 && doc.Root.Name.LocalName == "deadlock")
        {
            graphs.Add(ParseGraph(doc.Root, null));
        }

        return graphs;
    }

    private static DeadlockGraph ParseGraph(XElement deadlock, DateTimeOffset? at)
    {
        var victims = deadlock.Element("victim-list")?.Elements("victimProcess").Select(v => (string?)v.Attribute("id")).OfType<string>()
            .ToHashSet(StringComparer.Ordinal) ?? [];
        // Older versions put the victim on the <deadlock victim="…"> element.
        if ((string?)deadlock.Attribute("victim") is { } legacyVictim)
        {
            victims.Add(legacyVictim);
        }

        var processes = deadlock.Element("process-list")?.Elements("process").Select(p => new DeadlockProcess(
            (string?)p.Attribute("id") ?? "?",
            (int?)p.Attribute("spid"),
            victims.Contains((string?)p.Attribute("id") ?? string.Empty),
            (string?)p.Attribute("loginname"),
            (string?)p.Attribute("hostname"),
            (string?)p.Attribute("clientapp"),
            (string?)p.Attribute("currentdbname"),
            (string?)p.Attribute("lockMode"),
            (string?)p.Attribute("waitresource"),
            (long?)p.Attribute("waittime"),
            (string?)p.Attribute("isolationlevel"),
            (string?)p.Attribute("transactionname"),
            Clean(p.Element("inputbuf")?.Value),
            p.Element("executionStack")?.Elements("frame").Select(f => Clean(f.Value) ?? (string?)f.Attribute("procname") ?? string.Empty)
                .Where(s => s.Length > 0).ToList() ?? [])).ToList() ?? [];

        var resources = deadlock.Element("resource-list")?.Elements().Select(r => new DeadlockResource(
            r.Name.LocalName,
            (string?)r.Attribute("dbid"),
            (string?)r.Attribute("objectname") ?? (string?)r.Attribute("objectName"),
            (string?)r.Attribute("indexname"),
            (string?)r.Attribute("mode"),
            Parties(r.Element("owner-list")?.Elements("owner")),
            Parties(r.Element("waiter-list")?.Elements("waiter")))).ToList() ?? [];

        var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(deadlock.ToString(SaveOptions.DisableFormatting))))[..16];
        return new DeadlockGraph(id, at, processes, resources);
    }

    private static List<DeadlockLockParty> Parties(IEnumerable<XElement>? elements) =>
        elements?.Select(o => new DeadlockLockParty((string?)o.Attribute("id") ?? "?", (string?)o.Attribute("mode"), (string?)o.Attribute("requestType"))).ToList() ?? [];

    private static DateTimeOffset? ParseTime(string? text) =>
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var t) ? t : null;

    private static string? Clean(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var trimmed = text.Trim();
        return trimmed.Length > 4000 ? trimmed[..4000] : trimmed;
    }
}
