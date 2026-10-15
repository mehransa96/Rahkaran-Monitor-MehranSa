using System.Net;
using RSM.Core.Capabilities;
using RSM.Core.Profiles;

namespace RSM.Collectors.Windows;

/// <summary>
/// Opens the read-only Windows transports for a node: PDH + local CIM + local registry when the node is this machine,
/// CIM (WS-Man, then DCOM) + CIM counters + Remote Registry otherwise. Returns null with a reason when unavailable.
/// </summary>
public static class WindowsTransportFactory
{
    public static bool IsLocal(string host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return false;
        }

        var h = host.Trim();
        var machine = Environment.MachineName;
        return h is "." or "localhost" or "127.0.0.1" or "::1"
            || h.Equals(machine, StringComparison.OrdinalIgnoreCase)
            || h.StartsWith(machine + ".", StringComparison.OrdinalIgnoreCase)
            || (IPAddress.TryParse(h, out var ip) && IPAddress.IsLoopback(ip));
    }

    /// <summary>Never throws: failures are returned as the reason (shown in the Capability Matrix).</summary>
    public static (WindowsNodeTransport? Transport, string? Reason) TryOpen(NodeProfile node, string? password)
    {
        if (!OperatingSystem.IsWindows())
        {
            return (null, "RSM runs on Windows only for OS checks");
        }

        var local = IsLocal(node.Host);
        CimSessionQuery? cim = null;
        try
        {
            cim = CimSessionQuery.Open(local ? null : node.Host, node.Windows.UserName, password);
            IWindowsCounters counters;
            try
            {
                counters = local ? new PdhCounterSource() : new CimCounterSource(cim);
            }
            catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                counters = new CimCounterSource(cim);
            }

            IRegistryValues? registry = null;
            try
            {
                registry = RegistryReader.Open(local ? null : node.Host);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                // Remote Registry service stopped or no rights: registry-based checks report "unknown".
            }

            IEventLogSource? events = null;
            try
            {
                events = EventLogReaderSource.Open(local ? null : node.Host, node.Windows.UserName, password);
            }
            catch (Exception ex) when (ex is System.Diagnostics.Eventing.Reader.EventLogException or UnauthorizedAccessException)
            {
                // Event Log Readers membership or the Remote Event Log Management firewall rule missing: events are "unknown".
            }

            return (new WindowsNodeTransport(cim, counters, registry, local, events), null);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            cim?.Dispose();
            return (null, ex.Message);
        }
    }
}

/// <summary>Capability probes for the Windows layer (CIM, counters, registry).</summary>
public static class WindowsCapabilityProbe
{
    public static async Task ProbeAsync(WindowsNodeTransport? transport, string? openFailure, CapabilityMatrix matrix, CancellationToken cancellationToken)
    {
        if (transport is null)
        {
            var state = OperatingSystem.IsWindows() ? CapabilityState.Unreachable : CapabilityState.NotApplicable;
            matrix.Set(CapabilityIds.WinCim, state, openFailure);
            matrix.Set(CapabilityIds.WinPerf, state, openFailure);
            matrix.Set(CapabilityIds.WinRegistry, state, openFailure);
            matrix.Set(CapabilityIds.WinEventLog, state, openFailure);
            matrix.Set(CapabilityIds.WinServices, state, openFailure);
            return;
        }

        try
        {
            var os = await transport.Cim.QueryAsync(new CimQuery("Win32_OperatingSystem", ["Caption", "Version"]), cancellationToken).ConfigureAwait(false);
            matrix.Set(CapabilityIds.WinCim, CapabilityState.Available, transport.Cim.Protocol);
            var services = await transport.Cim.QueryAsync(new CimQuery("Win32_Service", ["Name", "State"], "Name = 'EventLog'"), cancellationToken).ConfigureAwait(false);
            matrix.Set(CapabilityIds.WinServices, services.Count > 0 ? CapabilityState.Available : CapabilityState.Denied,
                services.Count > 0 ? transport.Cim.Protocol : "Win32_Service returned nothing (Service Control Manager access?)");
            if (os.FirstOrDefault()?.String("Caption") is { } caption)
            {
                matrix.Facts["os.caption"] = caption;
            }
        }
        catch (UnauthorizedAccessException ex)
        {
            matrix.Set(CapabilityIds.WinCim, CapabilityState.Denied, ex.Message);
            matrix.Set(CapabilityIds.WinServices, CapabilityState.Denied, ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            matrix.Set(CapabilityIds.WinCim, CapabilityState.Unreachable, ex.Message);
            matrix.Set(CapabilityIds.WinServices, CapabilityState.Unreachable, ex.Message);
        }

        await ProbeEventsAsync(transport.Events, matrix, cancellationToken).ConfigureAwait(false);

        try
        {
            var readings = await transport.Counters.ReadAsync(cancellationToken).ConfigureAwait(false);
            matrix.Set(CapabilityIds.WinPerf, readings.Count > 0 ? CapabilityState.Available : CapabilityState.Denied,
                readings.Count > 0 ? transport.Counters.Transport : "no counters readable (Performance Monitor Users group?)");
        }
        catch (UnauthorizedAccessException ex)
        {
            matrix.Set(CapabilityIds.WinPerf, CapabilityState.Denied, ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            matrix.Set(CapabilityIds.WinPerf, CapabilityState.Error, ex.Message);
        }

        if (transport.Registry is null)
        {
            matrix.Set(CapabilityIds.WinRegistry, CapabilityState.Unreachable, "Remote Registry not reachable with the current Windows user");
        }
        else
        {
            try
            {
                transport.Registry.Read(RegistryWhitelist.PrioritySeparation);
                matrix.Set(CapabilityIds.WinRegistry, CapabilityState.Available);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
            {
                matrix.Set(CapabilityIds.WinRegistry, CapabilityState.Denied, ex.Message);
            }
        }
    }

    private static async Task ProbeEventsAsync(IEventLogSource? events, CapabilityMatrix matrix, CancellationToken cancellationToken)
    {
        if (events is null)
        {
            matrix.Set(CapabilityIds.WinEventLog, CapabilityState.Denied, "event log session could not be opened (Event Log Readers / Remote Event Log Management)");
            return;
        }

        try
        {
            await events.ReadAsync(new EventQuery("System", DateTimeOffset.UtcNow.AddHours(-1), [6005], false, 1), cancellationToken).ConfigureAwait(false);
            await events.ReadAsync(new EventQuery("Application", DateTimeOffset.UtcNow.AddHours(-1), [1000], false, 1), cancellationToken).ConfigureAwait(false);
            matrix.Set(CapabilityIds.WinEventLog, CapabilityState.Available);
        }
        catch (UnauthorizedAccessException ex)
        {
            matrix.Set(CapabilityIds.WinEventLog, CapabilityState.Denied, ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not ArgumentException)
        {
            matrix.Set(CapabilityIds.WinEventLog, CapabilityState.Unreachable, ex.Message);
        }
    }
}
