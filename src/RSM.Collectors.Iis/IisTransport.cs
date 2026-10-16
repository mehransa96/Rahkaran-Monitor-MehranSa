using System.Text.RegularExpressions;
using RSM.Safety.Files;

namespace RSM.Collectors.Iis;

/// <summary>
/// Maps a path as the server sees it (<c>%SystemDrive%\inetpub\logs</c>, <c>D:\Rahkaran</c>) to the path RSM opens:
/// the same path locally, the administrative share remotely (<c>\\host\D$\Rahkaran</c>). Anything that is not a drive
/// path or a UNC path is refused (null).
/// </summary>
public static partial class ServerPaths
{
    public static string? Expand(string? serverPath, string systemDrive = "C:", string systemRoot = @"C:\Windows")
    {
        if (string.IsNullOrWhiteSpace(serverPath))
        {
            return null;
        }

        var p = serverPath.Trim().Trim('"');
        p = Regex.Replace(p, "%SystemDrive%", systemDrive.TrimEnd('\\'), RegexOptions.IgnoreCase);
        p = Regex.Replace(p, "%(SystemRoot|windir)%", systemRoot.TrimEnd('\\'), RegexOptions.IgnoreCase);
        return p.Contains('%', StringComparison.Ordinal) || p.Contains("..", StringComparison.Ordinal) ? null : p.Replace('/', '\\');
    }

    /// <summary>The path to open from the RSM machine, or null when it cannot be mapped.</summary>
    public static string? ToAccessPath(string? serverPath, string? remoteHost, string systemDrive = "C:", string systemRoot = @"C:\Windows")
    {
        var p = Expand(serverPath, systemDrive, systemRoot);
        if (p is null)
        {
            return null;
        }

        if (p.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return p;
        }

        if (!DrivePath().IsMatch(p))
        {
            return null;
        }

        return remoteHost is null ? p : $@"\\{remoteHost}\{char.ToUpperInvariant(p[0])}${p[2..]}";
    }

    /// <summary>Combines a site's physical path with a path from web.config such as <c>.\logs\stdout</c>.</summary>
    public static string? Combine(string? root, string? relative)
    {
        if (string.IsNullOrWhiteSpace(relative))
        {
            return root;
        }

        var r = relative.Trim().Replace('/', '\\');
        if (DrivePath().IsMatch(r) || r.StartsWith(@"\\", StringComparison.Ordinal) || r.StartsWith('%'))
        {
            return r;
        }

        if (root is null)
        {
            return null;
        }

        r = r.StartsWith(@".\", StringComparison.Ordinal) ? r[2..] : r.TrimStart('\\');
        return r.Contains("..", StringComparison.Ordinal) ? null : $@"{root.TrimEnd('\\')}\{r}";
    }

    [GeneratedRegex(@"^[A-Za-z]:(\\|$)")]
    private static partial Regex DrivePath();
}

/// <summary>Read-only access to a node's files (IIS configuration and logs) — locally or through the admin share.</summary>
public interface IServerFiles
{
    /// <summary>Maps a server path to an access path (see <see cref="ServerPaths.ToAccessPath"/>).</summary>
    string? Map(string? serverPath);

    Task<string> ReadAllTextAsync(string accessPath, CancellationToken cancellationToken);

    Task<(string Text, bool Truncated)> ReadTailAsync(string accessPath, long maxBytes, CancellationToken cancellationToken);

    Task<(string Text, long NextOffset)> ReadFromAsync(string accessPath, long offset, long maxBytes, CancellationToken cancellationToken);

    IReadOnlyList<ServerFileInfo> List(string accessDirectory, string pattern, int max = 1000);

    bool Exists(string accessPath);

    /// <summary>SHA-256 (hex) of a file read with FileAccess.Read, or null when not supported.</summary>
    Task<string?> Sha256Async(string accessPath, CancellationToken cancellationToken) => Task.FromResult<string?>(null);
}

/// <summary><see cref="IServerFiles"/> over <see cref="ReadOnlyFile"/> (FileAccess.Read, FileShare.ReadWrite | Delete).</summary>
public sealed class ServerFiles(string? remoteHost, string systemDrive = "C:", string systemRoot = @"C:\Windows") : IServerFiles
{
    public string? Map(string? serverPath) => ServerPaths.ToAccessPath(serverPath, remoteHost, systemDrive, systemRoot);

    public Task<string> ReadAllTextAsync(string accessPath, CancellationToken cancellationToken) => ReadOnlyFile.ReadAllTextAsync(accessPath, cancellationToken);

    public Task<(string Text, bool Truncated)> ReadTailAsync(string accessPath, long maxBytes, CancellationToken cancellationToken) =>
        ReadOnlyFile.ReadTailTextAsync(accessPath, maxBytes, cancellationToken);

    public Task<(string Text, long NextOffset)> ReadFromAsync(string accessPath, long offset, long maxBytes, CancellationToken cancellationToken) =>
        ReadOnlyFile.ReadFromAsync(accessPath, offset, maxBytes, cancellationToken);

    public IReadOnlyList<ServerFileInfo> List(string accessDirectory, string pattern, int max = 1000) => ReadOnlyFile.ListInfo(accessDirectory, pattern, max);

    public bool Exists(string accessPath) => ReadOnlyFile.Exists(accessPath);

    public async Task<string?> Sha256Async(string accessPath, CancellationToken cancellationToken)
    {
        await using var stream = ReadOnlyFile.OpenRead(accessPath);
        return Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
    }
}

public sealed record PoolRuntime(string Name, string? State);

public sealed record SiteRuntime(string Name, long Id, string? State);

public sealed record WorkerProcessInfo(int ProcessId, string AppPool, string? State);

/// <summary>A request executing right now (IIS Request Monitor).</summary>
public sealed record LiveRequest(int ProcessId, string AppPool, long SiteId, string? Url, string? Verb, string? ClientIp, string? HostName, int TimeElapsedMs, string? PipelineState, string? CurrentModule);

/// <summary>IIS runtime state (local only, administrators, <c>Microsoft.Web.Administration</c> opened read-only).</summary>
public interface IIisRuntime
{
    IReadOnlyList<PoolRuntime> Pools();

    IReadOnlyList<SiteRuntime> Sites();

    IReadOnlyList<WorkerProcessInfo> WorkerProcesses();

    /// <summary>Requests running for at least <paramref name="minElapsedMs"/>; throws when Request Monitor is not installed.</summary>
    IReadOnlyList<LiveRequest> Requests(int minElapsedMs);
}

/// <summary>A certificate as shown (public data only; private keys are never touched).</summary>
public sealed record CertificateInfo(string Thumbprint, string Subject, string Issuer, DateTimeOffset NotBeforeUtc, DateTimeOffset NotAfterUtc, int? KeySize, string? KeyAlgorithm, bool ChainValid, string? ChainProblem);

/// <summary>Certificates of the LocalMachine stores, opened read-only (local node only).</summary>
public interface ICertificateSource
{
    CertificateInfo? Find(string thumbprint, string? storeName);
}

/// <summary>Everything RSM reads from one IIS node; created by the host, never by a collector.</summary>
public sealed class IisNodeTransport(IServerFiles files, IIisRuntime? runtime, ICertificateSource? certificates, bool isLocal, string host) : IDisposable
{
    public IServerFiles Files { get; } = files;

    /// <summary>Null on remote nodes or when RSM is not elevated (pool state then comes from the WAS counters).</summary>
    public IIisRuntime? Runtime { get; } = runtime;

    /// <summary>Null on remote nodes: certificate expiry is then unknown.</summary>
    public ICertificateSource? Certificates { get; } = certificates;

    public bool IsLocal { get; } = isLocal;

    public string Host { get; } = host;

    public const string ApplicationHostPath = @"%SystemRoot%\System32\inetsrv\config\applicationHost.config";

    public const string HttpErrPath = @"%SystemRoot%\System32\LogFiles\HTTPERR";

    public void Dispose()
    {
        (Runtime as IDisposable)?.Dispose();
        (Certificates as IDisposable)?.Dispose();
    }
}
