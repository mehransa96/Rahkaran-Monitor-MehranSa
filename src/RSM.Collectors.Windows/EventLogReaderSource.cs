using System.Diagnostics.Eventing.Reader;
using System.Runtime.Versioning;
using System.Security;
using RSM.Parsers.Events;

namespace RSM.Collectors.Windows;

/// <summary>
/// Event logs through <see cref="EventLogReader"/> on a local or remote <see cref="EventLogSession"/> (Remote Event Log
/// Management). Read-only: the session is only used to query; clearing, exporting and configuration are banned APIs.
/// Messages are formatted only for the newest event of each source+id (and for crash providers, whose message names
/// the process), then masked and truncated by <see cref="EventDigest.CleanMessage"/>.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class EventLogReaderSource : IEventLogSource, IDisposable
{
    private static readonly HashSet<string> AlwaysFormat = new(StringComparer.OrdinalIgnoreCase)
    {
        "Application Error", ".NET Runtime", "Windows Error Reporting",
    };

    private readonly EventLogSession _session;

    private EventLogReaderSource(EventLogSession session) => _session = session;

    /// <summary>Opens a session; remote sessions use the node's Windows credentials when given.</summary>
    public static EventLogReaderSource Open(string? remoteHost, string? userName, string? password)
    {
        if (remoteHost is null)
        {
            return new EventLogReaderSource(new EventLogSession());
        }

        if (string.IsNullOrEmpty(userName))
        {
            return new EventLogReaderSource(new EventLogSession(remoteHost));
        }

        var (domain, user) = userName.Contains('\\', StringComparison.Ordinal)
            ? (userName[..userName.IndexOf('\\', StringComparison.Ordinal)], userName[(userName.IndexOf('\\', StringComparison.Ordinal) + 1)..])
            : (null, userName);
        using var secure = new SecureString();
        foreach (var c in password ?? string.Empty)
        {
            secure.AppendChar(c);
        }

        secure.MakeReadOnly();
        return new EventLogReaderSource(new EventLogSession(remoteHost, domain, user, secure, SessionAuthentication.Default));
    }

    public Task<(IReadOnlyList<EventRecordData> Events, bool Truncated)> ReadAsync(EventQuery query, CancellationToken cancellationToken) =>
        Task.Run(() => Read(query, cancellationToken), cancellationToken);

    private (IReadOnlyList<EventRecordData>, bool) Read(EventQuery query, CancellationToken cancellationToken)
    {
        var logQuery = new EventLogQuery(query.Log, PathType.LogName, query.ToXPath()) { Session = _session, ReverseDirection = true };
        using var reader = new EventLogReader(logQuery);
        var result = new List<EventRecordData>();
        var formatted = new HashSet<(string, int)>();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var record = reader.ReadEvent();
            if (record is null)
            {
                return (result, false);
            }

            if (result.Count >= query.MaxEvents)
            {
                return (result, true);
            }

            var provider = record.ProviderName ?? "?";
            string? message = null;
            if (formatted.Add((provider.ToUpperInvariant(), record.Id)) || AlwaysFormat.Contains(provider))
            {
                try
                {
                    message = EventDigest.CleanMessage(record.FormatDescription());
                }
                catch (EventLogException)
                {
                    // Provider message DLL not registered on this machine: the event is still counted.
                }
            }

            var at = record.TimeCreated is { } t ? new DateTimeOffset(t.ToUniversalTime(), TimeSpan.Zero) : DateTimeOffset.MinValue;
            result.Add(new EventRecordData(query.Log, provider, record.Id, record.Level ?? 0, at, message));
        }
    }

    public void Dispose() => _session.Dispose();
}
