using RSM.Collectors.Windows;
using RSM.Core.Capabilities;
using RSM.Core.Profiles;

namespace RSM.Collectors.Iis;

/// <summary>
/// Opens the read-only IIS transport of an App/ARR node: files locally or through the admin share; the MWA runtime
/// (read-only ServerManager) and the certificate store only on the local node, the runtime only when RSM is elevated.
/// </summary>
public static class IisTransportFactory
{
    /// <summary>Never throws: failures are returned as the reason (shown in the Capability Matrix).</summary>
    public static async Task<(IisNodeTransport? Transport, string? Reason)> TryOpenAsync(NodeProfile node, WindowsNodeTransport? windows, bool elevated, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            return (null, "IIS checks need RSM on Windows");
        }

        var local = WindowsTransportFactory.IsLocal(node.Host);
        var systemRoot = local ? Environment.GetFolderPath(Environment.SpecialFolder.Windows) : @"C:\Windows";
        if (!local && windows is not null)
        {
            try
            {
                var os = await windows.Cim.QueryAsync(new CimQuery("Win32_OperatingSystem", ["WindowsDirectory"]), cancellationToken).ConfigureAwait(false);
                systemRoot = os.FirstOrDefault()?.String("WindowsDirectory") ?? systemRoot;
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not ArgumentException)
            {
                // Keep the default Windows folder.
            }
        }

        var files = new ServerFiles(local ? null : node.Host, systemRoot[..2], systemRoot);
        IIisRuntime? runtime = null;
        ICertificateSource? certificates = null;
        if (local)
        {
            certificates = new StoreCertificates();
            if (elevated && files.Map(IisNodeTransport.ApplicationHostPath) is { } path && files.Exists(path))
            {
                try
                {
                    runtime = MwaRuntime.Open(path);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    // IIS management components missing: configuration and logs still work from the files.
                }
            }
        }

        return (new IisNodeTransport(files, runtime, certificates, local, node.Host), null);
    }
}

/// <summary>Capability probes for the IIS layer (configuration, live requests, logs).</summary>
public static class IisCapabilityProbe
{
    public static async Task ProbeAsync(IisNodeTransport? transport, string? openFailure, bool elevated, CapabilityMatrix matrix, CancellationToken cancellationToken)
    {
        if (transport is null)
        {
            var state = OperatingSystem.IsWindows() ? CapabilityState.Unreachable : CapabilityState.NotApplicable;
            matrix.Set(CapabilityIds.IisConfig, state, openFailure);
            matrix.Set(CapabilityIds.IisRequests, state, openFailure);
            matrix.Set(CapabilityIds.IisLogs, state, openFailure);
            return;
        }

        var path = transport.Files.Map(IisNodeTransport.ApplicationHostPath);
        string? xml = null;
        try
        {
            xml = path is null ? null : await transport.Files.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            matrix.Set(CapabilityIds.IisConfig, CapabilityState.Available, transport.IsLocal ? "local" : "admin share");
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            matrix.Set(CapabilityIds.IisConfig, CapabilityState.NotInstalled, "applicationHost.config not found (IIS not installed?)");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            matrix.Set(CapabilityIds.IisConfig, transport.IsLocal && !elevated ? CapabilityState.NeedsElevation : CapabilityState.Denied, ex.Message);
        }
        catch (IOException ex)
        {
            matrix.Set(CapabilityIds.IisConfig, CapabilityState.Unreachable, ex.Message);
        }

        if (xml is null)
        {
            var state = matrix.Get(CapabilityIds.IisConfig)?.State ?? CapabilityState.Error;
            matrix.Set(CapabilityIds.IisLogs, state, "depends on applicationHost.config (log folders)");
        }
        else
        {
            try
            {
                var config = RSM.Parsers.Iis.ApplicationHostConfigParser.Parse(xml);
                var site = config.Sites.FirstOrDefault(s => s.LogEnabled);
                var dir = site is null ? null : transport.Files.Map(ServerPaths.Combine(site.LogDirectory ?? @"%SystemDrive%\inetpub\logs\LogFiles", $"W3SVC{site.Id}"));
                if (dir is null)
                {
                    matrix.Set(CapabilityIds.IisLogs, CapabilityState.NotApplicable, "no site with W3C logging");
                }
                else
                {
                    transport.Files.List(dir, "u_ex*.log", 1);
                    matrix.Set(CapabilityIds.IisLogs, CapabilityState.Available);
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
            {
                matrix.Set(CapabilityIds.IisLogs, CapabilityState.Denied, ex.Message);
            }
            catch (Exception ex) when (ex is IOException or System.Xml.XmlException or FormatException)
            {
                matrix.Set(CapabilityIds.IisLogs, CapabilityState.Error, ex.Message);
            }
        }

        if (!transport.IsLocal)
        {
            matrix.Set(CapabilityIds.IisRequests, CapabilityState.NotApplicable, "live requests are read on the IIS server itself (run RSM there)");
        }
        else if (transport.Runtime is null)
        {
            matrix.Set(CapabilityIds.IisRequests, elevated ? CapabilityState.NotInstalled : CapabilityState.NeedsElevation,
                elevated ? "IIS management components not available" : "run RSM as Administrator");
        }
        else
        {
            try
            {
                transport.Runtime.Requests(int.MaxValue);
                matrix.Set(CapabilityIds.IisRequests, CapabilityState.Available);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                matrix.Set(CapabilityIds.IisRequests, CapabilityState.NotInstalled, "IIS Request Monitor feature not installed (" + ex.GetType().Name + ")");
            }
        }
    }
}
