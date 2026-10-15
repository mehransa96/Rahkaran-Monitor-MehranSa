using System.Runtime.Versioning;
using System.Security;
using Microsoft.Management.Infrastructure;
using Microsoft.Management.Infrastructure.Options;

namespace RSM.Collectors.Windows;

/// <summary>
/// <see cref="ICimQuery"/> over a <see cref="CimSession"/> (WS-Man first, DCOM fallback for remote nodes). Only
/// <c>QueryInstances</c> is ever called; method invocation and instance changes are banned (BannedSymbols.txt).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class CimSessionQuery : ICimQuery, IDisposable
{
    private readonly CimSession _session;

    private CimSessionQuery(CimSession session, string protocol)
    {
        _session = session;
        Protocol = protocol;
    }

    /// <summary>"local", "wsman" or "dcom".</summary>
    public string Protocol { get; }

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>Opens a session; for a remote node with a user name, the password is used only to build the credential.</summary>
    public static CimSessionQuery Open(string? computer, string? userName, string? password)
    {
        if (computer is null)
        {
            return new CimSessionQuery(CimSession.Create(null), "local");
        }

        CimCredential? credential = null;
        if (!string.IsNullOrWhiteSpace(userName) && password is not null)
        {
            var slash = userName.IndexOf('\\', StringComparison.Ordinal);
            var domain = slash > 0 ? userName[..slash] : string.Empty;
            var user = slash > 0 ? userName[(slash + 1)..] : userName;
            // Not disposed: the credential keeps a reference for re-authentication (WS-Man reconnects).
            var secure = new SecureString();
            foreach (var c in password)
            {
                secure.AppendChar(c);
            }

            secure.MakeReadOnly();
            credential = new CimCredential(PasswordAuthenticationMechanism.Default, domain, user, secure);
        }

        var wsman = new WSManSessionOptions { Timeout = TimeSpan.FromSeconds(10) };
        if (credential is not null)
        {
            wsman.AddDestinationCredentials(credential);
        }

        var session = CimSession.Create(computer, wsman);
        if (session.TestConnection(out _, out _))
        {
            return new CimSessionQuery(session, "wsman");
        }

        session.Dispose();
        var dcom = new DComSessionOptions { Timeout = TimeSpan.FromSeconds(10) };
        if (credential is not null)
        {
            dcom.AddDestinationCredentials(credential);
        }

        return new CimSessionQuery(CimSession.Create(computer, dcom), "dcom");
    }

    public Task<IReadOnlyList<CimRow>> QueryAsync(CimQuery query, CancellationToken cancellationToken)
    {
        var wql = query.ToWql();
        return Task.Run(() =>
        {
            using var options = new CimOperationOptions { Timeout = Timeout, CancellationToken = cancellationToken };
            var rows = new List<CimRow>();
            try
            {
                foreach (var instance in _session.QueryInstances(query.Namespace, "WQL", wql, options))
                {
                    using (instance)
                    {
                        rows.Add(new CimRow(instance.CimInstanceProperties.ToDictionary(p => p.Name, p => (object?)p.Value)));
                    }
                }
            }
            catch (CimException ex) when (ex.NativeErrorCode == NativeErrorCode.AccessDenied)
            {
                throw new UnauthorizedAccessException($"CIM access denied: {query.ClassName}", ex);
            }

            return (IReadOnlyList<CimRow>)rows;
        }, cancellationToken);
    }

    public void Dispose() => _session.Dispose();
}
