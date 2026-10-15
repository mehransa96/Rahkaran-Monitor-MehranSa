using RSM.Core.Model;
using RSM.Safety.Sql;

namespace RSM.Collectors.Sql.Instance;

/// <summary>§7.17 security posture (read-only audit): logins, sa, sysadmins, encryption, linked servers.</summary>
public sealed class SqlSecurityCollector(SafeSqlExecutor executor) : SqlCollectorBase(executor)
{
    public override string Id => "sql.security";

    public override CollectorCost Cost => CollectorCost.Cheap;

    /// <summary>Built-in service principals that are sysadmin by design and are not counted.</summary>
    internal static bool IsBuiltInPrincipal(string? name) =>
        name is not null && (name.StartsWith(@"NT SERVICE\", StringComparison.OrdinalIgnoreCase)
            || name.Equals(@"NT AUTHORITY\SYSTEM", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("##", StringComparison.Ordinal));

    protected override async Task<IReadOnlyDictionary<string, object?>> CollectCoreAsync(INodeContext node, CollectContext context, SqlQueryRunner runner, List<Fact> facts, List<UnifiedEvent> events)
    {
        var logins = await runner.QueryAsync<LoginRow>("sql.sec.logins").ConfigureAwait(false);
        var sysadmins = await runner.QueryAsync<SysadminRow>("sql.sec.sysadmins").ConfigureAwait(false);
        var blank = (await runner.QueryAsync<BlankPasswordRow>("sql.sec.sa_blank_pwd").ConfigureAwait(false))?.FirstOrDefault();
        var encryption = await runner.QueryAsync<EncryptionRow>("sql.sec.encryption").ConfigureAwait(false);
        var linked = await runner.QueryAsync<LinkedServerRow>("sql.sec.linked_servers").ConfigureAwait(false);

        if (logins is not null)
        {
            var sa = logins.FirstOrDefault(l => l.IsSa == 1);
            if (sa is not null)
            {
                Add(facts, "sec.sa.enabled", sa.IsDisabled == 0);
                Add(facts, "sec.sa.renamed", !string.Equals(sa.Name, "sa", StringComparison.OrdinalIgnoreCase));
            }

            var noPolicy = logins.Where(l => l.TypeDesc == "SQL_LOGIN" && l.IsPolicyChecked == 0 && l.IsDisabled == 0).Select(l => (object?)l.Name).ToList();
            Add(facts, "sec.logins_without_policy", noPolicy.Count);
            Add(facts, "sec.logins_without_policy_names", noPolicy);
            Add(facts, "sec.sql_logins", logins.Count(l => l.TypeDesc == "SQL_LOGIN"));
        }

        // NULL = cannot tell (password_hash needs CONTROL SERVER) → fact stays missing, rule stays indeterminate.
        if (blank?.BlankPassword is { } b)
        {
            Add(facts, "sec.sa.blank_password", b == 1);
        }

        if (sysadmins is not null)
        {
            var members = sysadmins.Where(s => s.IsDisabled == 0 && !IsBuiltInPrincipal(s.Name)).Select(s => (object?)s.Name).ToList();
            Add(facts, "sec.sysadmin_count", members.Count);
            Add(facts, "sec.sysadmin_names", members);
        }

        if (encryption is not null)
        {
            Add(facts, "sec.unencrypted_remote_connections", encryption.Where(e => !string.Equals(e.EncryptOption, "TRUE", StringComparison.OrdinalIgnoreCase)).Sum(e => e.RemoteConnections));
            Add(facts, "sec.connections_total", encryption.Sum(e => e.Connections));
        }

        Add(facts, "sec.linked_servers", linked?.Count);

        return new Dictionary<string, object?>
        {
            ["logins"] = logins,
            ["sysadmins"] = sysadmins,
            ["saBlankPassword"] = blank?.BlankPassword,
            ["encryption"] = encryption,
            ["linkedServers"] = linked,
        };
    }

    public sealed class LoginRow
    {
        public string Name { get; set; } = string.Empty;

        public string? TypeDesc { get; set; }

        public int IsDisabled { get; set; }

        public string? DefaultDatabase { get; set; }

        public int IsSa { get; set; }

        public int? IsPolicyChecked { get; set; }

        public int? IsExpirationChecked { get; set; }
    }

    public sealed class SysadminRow
    {
        public string Name { get; set; } = string.Empty;

        public string? TypeDesc { get; set; }

        public int IsDisabled { get; set; }
    }

    public sealed class BlankPasswordRow
    {
        public int? BlankPassword { get; set; }
    }

    public sealed class EncryptionRow
    {
        public string? EncryptOption { get; set; }

        public string? AuthScheme { get; set; }

        public string? NetTransport { get; set; }

        public int Connections { get; set; }

        public int RemoteConnections { get; set; }
    }

    public sealed class LinkedServerRow
    {
        public string? Name { get; set; }

        public string? Product { get; set; }

        public string? Provider { get; set; }

        public string? DataSource { get; set; }
    }
}
