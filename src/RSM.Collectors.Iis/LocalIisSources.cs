using System.Runtime.Versioning;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Web.Administration;

namespace RSM.Collectors.Iis;

/// <summary>
/// IIS runtime state through <see cref="ServerManager"/> opened read-only on the local applicationHost.config
/// (<c>new ServerManager(true, path)</c>; the parameterless and read-write constructors, <c>OpenRemote</c>,
/// <c>CommitChanges</c> and every Start/Stop/Recycle are banned APIs). Requires administrators; live requests also
/// need the IIS Request Monitor feature.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class MwaRuntime : IIisRuntime, IDisposable
{
    private readonly ServerManager _manager;

    private MwaRuntime(ServerManager manager) => _manager = manager;

    public static MwaRuntime Open(string applicationHostPath) => new(new ServerManager(true, applicationHostPath));

    public IReadOnlyList<PoolRuntime> Pools() =>
        _manager.ApplicationPools.Select(p => new PoolRuntime(p.Name, TryState(() => p.State.ToString()))).ToList();

    public IReadOnlyList<SiteRuntime> Sites() =>
        _manager.Sites.Select(s => new SiteRuntime(s.Name, s.Id, TryState(() => s.State.ToString()))).ToList();

    public IReadOnlyList<WorkerProcessInfo> WorkerProcesses() =>
        _manager.WorkerProcesses.Select(w => new WorkerProcessInfo(w.ProcessId, w.AppPoolName, TryState(() => w.State.ToString()))).ToList();

    public IReadOnlyList<LiveRequest> Requests(int minElapsedMs)
    {
        var result = new List<LiveRequest>();
        foreach (var w in _manager.WorkerProcesses)
        {
            foreach (var r in w.GetRequests(minElapsedMs))
            {
                result.Add(new LiveRequest(w.ProcessId, w.AppPoolName, r.SiteId, r.Url, r.Verb, r.ClientIPAddr, r.HostName, r.TimeElapsed,
                    r.PipelineState.ToString(), r.CurrentModule));
            }
        }

        return result;
    }

    /// <summary>State needs the WAS service; when it is unavailable the state is shown as unknown.</summary>
    private static string? TryState(Func<string> read)
    {
        try
        {
            return read();
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or UnauthorizedAccessException or InvalidOperationException)
        {
            return null;
        }
    }

    public void Dispose() => _manager.Dispose();
}

/// <summary>
/// Certificates from the LocalMachine stores, opened <see cref="OpenFlags.ReadOnly"/> | <see cref="OpenFlags.OpenExistingOnly"/>.
/// Chains are built offline (no certificate downloads, no revocation lookups: RSM makes no internet calls).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class StoreCertificates : ICertificateSource
{
    public CertificateInfo? Find(string thumbprint, string? storeName)
    {
        using var store = new X509Store(string.IsNullOrWhiteSpace(storeName) ? "My" : storeName, StoreLocation.LocalMachine);
        try
        {
            store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return null;
        }

        var cert = store.Certificates.Find(X509FindType.FindByThumbprint, thumbprint, validOnly: false).OfType<X509Certificate2>().FirstOrDefault();
        if (cert is null)
        {
            return null;
        }

        using (cert)
        {
            using var chain = new X509Chain();
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            chain.ChainPolicy.DisableCertificateDownloads = true;
            chain.ChainPolicy.VerificationFlags = X509VerificationFlags.IgnoreNotTimeValid;
            var valid = chain.Build(cert);
            var problem = valid ? null : string.Join(", ", chain.ChainStatus.Select(s => s.Status.ToString()).Distinct());
            var algorithm = cert.PublicKey.Oid.FriendlyName;
            int? keySize = cert.PublicKey.GetRSAPublicKey() is { } rsa ? rsa.KeySize : cert.PublicKey.GetECDsaPublicKey() is { } ec ? ec.KeySize : null;
            return new CertificateInfo(cert.Thumbprint, cert.Subject, cert.Issuer,
                new DateTimeOffset(cert.NotBefore.ToUniversalTime(), TimeSpan.Zero), new DateTimeOffset(cert.NotAfter.ToUniversalTime(), TimeSpan.Zero),
                keySize, algorithm, valid, problem);
        }
    }
}
