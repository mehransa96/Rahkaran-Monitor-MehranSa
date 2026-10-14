using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace RSM.Safety.Secrets;

/// <summary>Encrypts secrets at rest. Passwords are either protected with this or not stored at all.</summary>
public interface ISecretProtector
{
    /// <summary>False when secrets cannot be stored safely on this platform (they are then kept in memory only).</summary>
    bool CanPersist { get; }

    byte[] Protect(string secret, string purpose);

    string Unprotect(byte[] protectedBytes, string purpose);
}

/// <summary>Windows DPAPI, CurrentUser scope, with per-purpose entropy (site/node/kind).</summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiSecretProtector : ISecretProtector
{
    private static readonly byte[] AppEntropy = Encoding.UTF8.GetBytes("RSM-ReadOnlyMonitor/v1");

    public bool CanPersist => true;

    public byte[] Protect(string secret, string purpose) =>
        ProtectedData.Protect(Encoding.UTF8.GetBytes(secret), Entropy(purpose), DataProtectionScope.CurrentUser);

    public string Unprotect(byte[] protectedBytes, string purpose) =>
        Encoding.UTF8.GetString(ProtectedData.Unprotect(protectedBytes, Entropy(purpose), DataProtectionScope.CurrentUser));

    private static byte[] Entropy(string purpose) => [.. AppEntropy, .. Encoding.UTF8.GetBytes(purpose)];
}

/// <summary>Used where DPAPI is unavailable (non-Windows CI): secrets are never written to disk.</summary>
public sealed class NoPersistenceSecretProtector : ISecretProtector
{
    public bool CanPersist => false;

    public byte[] Protect(string secret, string purpose) =>
        throw new PlatformNotSupportedException("Secrets cannot be persisted on this platform; they are kept in memory only.");

    public string Unprotect(byte[] protectedBytes, string purpose) =>
        throw new PlatformNotSupportedException("Secrets cannot be persisted on this platform.");
}
