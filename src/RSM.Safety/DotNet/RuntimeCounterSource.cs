using Microsoft.Diagnostics.NETCore.Client;

namespace RSM.Safety.DotNet;

/// <summary>An open EventPipe session that carries EventCounters only. Dispose stops it.</summary>
public sealed class RuntimeCounterSession : IDisposable
{
    private readonly EventPipeSession _session;

    internal RuntimeCounterSession(EventPipeSession session) => _session = session;

    /// <summary>The nettrace stream (read-only telemetry) to parse with an EventPipe reader.</summary>
    public Stream EventStream => _session.EventStream;

    public void Stop()
    {
        try
        {
            _session.Stop();
        }
        catch (Exception ex) when (ex is ServerNotAvailableException or EndOfStreamException or IOException or TimeoutException)
        {
            // The process exited or already closed the session.
        }
    }

    public void Dispose() => _session.Dispose();
}

/// <summary>
/// The only use of the .NET diagnostics IPC in RSM (spec §5, §9.4 — optional, local, off by default): list processes
/// that publish a diagnostics endpoint, and open an EventPipe session limited to the <c>System.Runtime</c> and
/// <c>Microsoft.AspNetCore.Hosting</c> EventCounters. No dumps, profilers, startup hooks, environment reads or writes,
/// rundown or other providers can be requested through this type.
/// </summary>
public static class RuntimeCounterSource
{
    public static readonly IReadOnlyList<string> Providers = ["System.Runtime", "Microsoft.AspNetCore.Hosting"];

    /// <summary>EventCounters are emitted by the counter provider at Informational level with no keywords.</summary>
    private const long NoKeywords = 0;

    /// <summary>PIDs of local processes with a .NET diagnostics endpoint (w3wp in-process, dotnet out-of-process …).</summary>
    public static IReadOnlyList<int> PublishedProcesses()
    {
        try
        {
            return DiagnosticsClient.GetPublishedProcesses().ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return [];
        }
    }

    /// <summary>Starts a counters-only session (interval 1–60 s, 16 MB circular buffer, no rundown).</summary>
    public static RuntimeCounterSession Start(int processId, int intervalSeconds)
    {
        var interval = Math.Clamp(intervalSeconds, 1, 60).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var providers = Providers.Select(p => new EventPipeProvider(p, System.Diagnostics.Tracing.EventLevel.Informational, NoKeywords,
            new Dictionary<string, string> { ["EventCounterIntervalSec"] = interval })).ToList();
        var client = new DiagnosticsClient(processId);
        return new RuntimeCounterSession(client.StartEventPipeSession(providers, requestRundown: false, circularBufferMB: 16));
    }
}
