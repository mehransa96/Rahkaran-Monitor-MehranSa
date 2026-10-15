using RSM.Core.Model;
using RSM.Parsers.Iis;

namespace RSM.Parsers.Events;

/// <summary>Events of one source+id grouped (spec §9.8): count, first/last occurrence and the latest message.</summary>
public sealed record EventGroup(
    string Log,
    string Provider,
    int Id,
    int Level,
    long Count,
    DateTimeOffset FirstUtc,
    DateTimeOffset LastUtc,
    string? SampleMessage,
    string? KbKey,
    Severity Severity,
    bool WebWorker);

public static class EventDigest
{
    public const int MaxMessageLength = 1000;

    /// <summary>Severity of one event: the KB entry when known, else the Windows level.</summary>
    public static Severity SeverityOf(EventRecordData e, EventKbEntry? kb) => kb?.Severity ?? e.Level switch
    {
        1 => Severity.Critical,
        2 => Severity.Warning,
        3 => Severity.Info,
        _ => Severity.Ok,
    };

    /// <summary>A message as RSM shows it: secrets masked (key=value, URL credentials), whitespace collapsed, truncated.</summary>
    public static string? CleanMessage(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return null;
        }

        var masked = SecretMasker.MaskInline(message.Trim())!;
        masked = string.Join(' ', masked.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return masked.Length > MaxMessageLength ? masked[..MaxMessageLength] + "…" : masked;
    }

    public static IReadOnlyList<EventGroup> Group(IEnumerable<EventRecordData> events) =>
        events.GroupBy(e => (e.Log, Provider: e.Provider.ToUpperInvariant(), e.Id))
            .Select(g =>
            {
                var latest = g.MaxBy(e => e.AtUtc)!;
                var kb = EventKnowledgeBase.Find(latest.Provider, latest.Id, g.Min(e => e.Level is 0 ? 4 : e.Level));
                return new EventGroup(latest.Log, latest.Provider, latest.Id, g.Min(e => e.Level is 0 ? 4 : e.Level), g.LongCount(),
                    g.Min(e => e.AtUtc), latest.AtUtc, latest.Message, kb?.Key, SeverityOf(latest, kb), g.Any(e => EventKnowledgeBase.NamesWebWorker(e.Message)));
            })
            .OrderByDescending(g => g.Severity)
            .ThenByDescending(g => g.LastUtc)
            .ToList();
}
