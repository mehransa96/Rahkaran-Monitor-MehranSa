using System.Collections.Concurrent;
using RSM.Core.Profiles;
using RSM.Safety.Files;
using RSM.Safety.Secrets;

namespace RSM.Storage.Profiles;

/// <summary>
/// Passwords per (site, node, kind). Persisted only when the user chose "remember" and the platform can protect
/// them (DPAPI CurrentUser, <c>data/secrets/*.bin</c>); otherwise kept in memory for this RSM session only.
/// Secrets are never logged and never part of profile JSON or reports.
/// </summary>
public sealed class SecretStore(IDataFolder dataFolder, ISecretProtector protector)
{
    private const string Folder = "secrets";
    private readonly ConcurrentDictionary<string, string> _sessionOnly = new(StringComparer.Ordinal);

    public bool CanPersist => protector.CanPersist;

    public async Task SetAsync(string siteId, string nodeId, SecretKind kind, string? secret, bool remember, CancellationToken cancellationToken)
    {
        var name = Name(siteId, nodeId, kind);
        dataFolder.Delete(FileFor(name));
        _sessionOnly.TryRemove(name, out _);
        if (string.IsNullOrEmpty(secret))
        {
            return;
        }

        if (remember && protector.CanPersist)
        {
            await dataFolder.WriteAllBytesAsync(FileFor(name), protector.Protect(secret, name), cancellationToken).ConfigureAwait(false);
        }
        else
        {
            _sessionOnly[name] = secret;
        }
    }

    public async Task<string?> GetAsync(string siteId, string nodeId, SecretKind kind, CancellationToken cancellationToken)
    {
        var name = Name(siteId, nodeId, kind);
        if (_sessionOnly.TryGetValue(name, out var inMemory))
        {
            return inMemory;
        }

        if (!protector.CanPersist || !dataFolder.Exists(FileFor(name)))
        {
            return null;
        }

        var bytes = await dataFolder.ReadAllBytesAsync(FileFor(name), cancellationToken).ConfigureAwait(false);
        return protector.Unprotect(bytes, name);
    }

    public void DeleteSite(string siteId)
    {
        ProfileStore.ValidateId(siteId);
        foreach (var file in dataFolder.ListFiles(Folder, $"{siteId}.*.bin"))
        {
            dataFolder.Delete(file);
        }

        foreach (var key in _sessionOnly.Keys.Where(k => k.StartsWith(siteId + ".", StringComparison.Ordinal)))
        {
            _sessionOnly.TryRemove(key, out _);
        }
    }

    private static string Name(string siteId, string nodeId, SecretKind kind)
    {
        ProfileStore.ValidateId(siteId);
        ProfileStore.ValidateId(nodeId);
        return $"{siteId}.{nodeId}.{kind}";
    }

    private static string FileFor(string name) => Path.Combine(Folder, name + ".bin");
}
