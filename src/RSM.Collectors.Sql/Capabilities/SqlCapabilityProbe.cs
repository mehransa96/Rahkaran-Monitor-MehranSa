using RSM.Core.Capabilities;
using RSM.Core.Model;
using RSM.Safety.Sql;

namespace RSM.Collectors.Sql.Capabilities;

/// <summary>
/// Fills the SQL part of the Capability Matrix with cheap, read-only probes (catalog keys <c>cap.*</c>).
/// Also sets <see cref="SqlNodeSession.ServerVersion"/> so later queries are version-gated.
/// </summary>
public sealed class SqlCapabilityProbe(SafeSqlExecutor executor)
{
    private static readonly string[] SqlCapabilities =
    [
        CapabilityIds.SqlConnect, CapabilityIds.SqlViewServerState, CapabilityIds.SqlViewAnyDefinition,
        CapabilityIds.SqlConnectAnyDatabase, CapabilityIds.SqlMsdbRead, CapabilityIds.SqlAgentRead,
        CapabilityIds.SqlErrorLog, CapabilityIds.SqlSsrsLog, CapabilityIds.SqlSysadmin,
    ];

    public async Task ProbeAsync(SqlNodeSession session, CapabilityMatrix matrix, CancellationToken cancellationToken)
    {
        // 1. Connect + version.
        var version = await executor.QueryAsync<VersionRow>(session, "cap.version", cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!version.IsOk || version.Rows.Count == 0)
        {
            var state = version.Status switch
            {
                SqlQueryStatus.LoginFailed or SqlQueryStatus.PermissionDenied => CapabilityState.Denied,
                SqlQueryStatus.Unreachable or SqlQueryStatus.Timeout => CapabilityState.Unreachable,
                _ => CapabilityState.Error,
            };
            matrix.Set(CapabilityIds.SqlConnect, state, version.Message);
            foreach (var id in SqlCapabilities.Skip(1))
            {
                matrix.Set(id, CapabilityState.Unreachable, "no SQL connection");
            }

            return;
        }

        var v = version.Rows[0];
        matrix.Set(CapabilityIds.SqlConnect, CapabilityState.Available, $"{v.Edition} {v.ProductVersion} {v.ProductLevel} {v.ProductUpdateLevel}".Trim());
        matrix.Facts["sql.version"] = v.ProductVersion ?? string.Empty;
        matrix.Facts["sql.edition"] = v.Edition ?? string.Empty;
        matrix.Facts["sql.level"] = v.ProductLevel ?? string.Empty;
        matrix.Facts["sql.update_level"] = v.ProductUpdateLevel ?? string.Empty;
        matrix.Facts["sql.server_name"] = v.ServerName ?? string.Empty;
        if (SqlServerVersion.TryParse(v.ProductVersion, out var parsed))
        {
            session.ServerVersion = parsed;
            if (parsed < SqlServerVersion.Sql2016)
            {
                matrix.Banners.Add(new LocalizedText(
                    $"نسخه {parsed.ProductName} پشتیبانی نمی‌شود؛ RSM برای SQL Server 2016 تا 2022 طراحی شده و برخی بررسی‌ها در دسترس نخواهند بود.",
                    $"{parsed.ProductName} is not supported; RSM targets SQL Server 2016–2022 and some checks will be unavailable."));
            }
        }

        // 2. Server permissions and roles.
        var perms = await executor.QueryAsync<ServerPermsRow>(session, "cap.server_perms", cancellationToken: cancellationToken).ConfigureAwait(false);
        var p = perms.Rows.FirstOrDefault();
        if (p is null)
        {
            foreach (var id in new[] { CapabilityIds.SqlViewServerState, CapabilityIds.SqlViewAnyDefinition, CapabilityIds.SqlSysadmin })
            {
                matrix.Set(id, CapabilityState.Error, perms.Message);
            }
        }
        else
        {
            matrix.Facts["sql.login"] = p.LoginName ?? string.Empty;
            var vss = p.ViewServerState == 1 || (p.ViewServerPerformanceState == 1 && p.ViewServerSecurityState == 1);
            matrix.Set(CapabilityIds.SqlViewServerState, vss ? CapabilityState.Available : CapabilityState.Denied,
                p.ViewServerPerformanceState is null ? null : $"PERFORMANCE STATE={p.ViewServerPerformanceState}, SECURITY STATE={p.ViewServerSecurityState}");
            matrix.Set(CapabilityIds.SqlViewAnyDefinition, p.ViewAnyDefinition == 1 ? CapabilityState.Available : CapabilityState.Denied);
            var sysadmin = p.IsSysadmin == 1;
            matrix.Set(CapabilityIds.SqlSysadmin, sysadmin ? CapabilityState.Available : CapabilityState.NotApplicable);
            if (sysadmin)
            {
                matrix.Banners.Add(new LocalizedText(
                    "حساب با دسترسی بالا (sysadmin) استفاده شده است؛ RSM همچنان فقط‌خواندنی است.",
                    "High-privilege account used (sysadmin); RSM stays read-only."));
            }
        }

        // 3. Databases.
        var dbs = await executor.QueryAsync<DbAccessRow>(session, "cap.db_access", cancellationToken: cancellationToken).ConfigureAwait(false);
        var online = dbs.Rows.Where(d => d.DatabaseId > 4 && string.Equals(d.StateDesc, "ONLINE", StringComparison.OrdinalIgnoreCase)).ToList();
        var accessible = online.Count(d => d.HasAccess == 1);
        if (!dbs.IsOk)
        {
            matrix.Set(CapabilityIds.SqlConnectAnyDatabase, CapabilityState.Error, dbs.Message);
        }
        else if (p?.ConnectAnyDatabase == 1 || accessible == online.Count)
        {
            matrix.Set(CapabilityIds.SqlConnectAnyDatabase, CapabilityState.Available, $"{accessible}/{online.Count} user databases");
        }
        else
        {
            matrix.Set(CapabilityIds.SqlConnectAnyDatabase, CapabilityState.Denied, $"{accessible}/{online.Count} user databases accessible");
        }

        // 4. msdb history and error log.
        var objects = await executor.QueryAsync<ObjectPermsRow>(session, "cap.object_perms", cancellationToken: cancellationToken).ConfigureAwait(false);
        var o = objects.Rows.FirstOrDefault();
        var msdb = o is not null && o.MsdbBackupset == 1 && o.MsdbBackupMediaFamily == 1;
        matrix.Set(CapabilityIds.SqlMsdbRead, msdb ? CapabilityState.Available : CapabilityState.Denied, o is null ? objects.Message : null);
        var errorLog = o?.XpReadErrorLog == 1 || p?.IsSecurityAdmin == 1 || p?.IsSysadmin == 1;
        matrix.Set(CapabilityIds.SqlErrorLog, errorLog ? CapabilityState.Available : CapabilityState.Denied,
            errorLog ? null : $"file fallback (local/admin share): {o?.ErrorLogFileName}");
        if (!string.IsNullOrEmpty(o?.ErrorLogFileName))
        {
            matrix.Facts["sql.errorlog_path"] = o.ErrorLogFileName;
        }

        // 5. SQL Agent (runs in msdb; denied if the login has no user there).
        var agent = await executor.QueryAsync<AgentRoleRow>(session, "cap.agent_role", cancellationToken: cancellationToken).ConfigureAwait(false);
        var a = agent.Rows.FirstOrDefault();
        var agentOk = p?.IsSysadmin == 1 || (a is not null && (a.IsAgentReader == 1 || (a.CanReadJobs == 1 && a.CanReadJobHistory == 1)));
        matrix.Set(CapabilityIds.SqlAgentRead, agentOk ? CapabilityState.Available : CapabilityState.Denied, agent.IsOk ? null : agent.Message);

        // 6. SSRS execution log in any ReportServer database (ReportServerTempDB excluded).
        var reportDbs = dbs.Rows
            .Where(d => d.Name.StartsWith("ReportServer", StringComparison.OrdinalIgnoreCase) && !d.Name.EndsWith("TempDB", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (reportDbs.Count == 0)
        {
            matrix.Set(CapabilityIds.SqlSsrsLog, CapabilityState.NotInstalled, "no ReportServer database on this instance");
        }
        else
        {
            var state = CapabilityState.Denied;
            string? detail = null;
            foreach (var db in reportDbs)
            {
                var ssrs = await executor.QueryAsync<SsrsAccessRow>(session, "cap.ssrs_access", database: db.Name, cancellationToken: cancellationToken).ConfigureAwait(false);
                var s = ssrs.Rows.FirstOrDefault();
                if (s is { HasExecutionLog3: 1, CanReadExecutionLog3: 1 })
                {
                    state = CapabilityState.Available;
                    detail = db.Name;
                    matrix.Facts["ssrs.database"] = db.Name;
                    break;
                }

                detail = ssrs.IsOk ? $"{db.Name}: no SELECT on dbo.ExecutionLog3" : $"{db.Name}: {ssrs.Status}";
            }

            matrix.Set(CapabilityIds.SqlSsrsLog, state, detail);
        }
    }

    // Dapper row types (public settable properties map by column name).
    public sealed class VersionRow
    {
        public string? ProductVersion { get; set; }

        public string? ProductLevel { get; set; }

        public string? ProductUpdateLevel { get; set; }

        public string? Edition { get; set; }

        public int? EngineEdition { get; set; }

        public string? ServerName { get; set; }

        public int? IsClustered { get; set; }

        public int? IsHadrEnabled { get; set; }

        public string? VersionText { get; set; }
    }

    public sealed class ServerPermsRow
    {
        public string? LoginName { get; set; }

        public int? ViewServerState { get; set; }

        public int? ViewServerPerformanceState { get; set; }

        public int? ViewServerSecurityState { get; set; }

        public int? ViewAnyDefinition { get; set; }

        public int? ConnectAnyDatabase { get; set; }

        public int? ControlServer { get; set; }

        public int? IsSysadmin { get; set; }

        public int? IsSecurityAdmin { get; set; }
    }

    public sealed class DbAccessRow
    {
        public string Name { get; set; } = string.Empty;

        public int DatabaseId { get; set; }

        public string? StateDesc { get; set; }

        public int? HasAccess { get; set; }
    }

    public sealed class ObjectPermsRow
    {
        public int? MsdbBackupset { get; set; }

        public int? MsdbBackupMediaFamily { get; set; }

        public int? MsdbSuspectPages { get; set; }

        public int? XpReadErrorLog { get; set; }

        public string? ErrorLogFileName { get; set; }
    }

    public sealed class AgentRoleRow
    {
        public int? IsAgentReader { get; set; }

        public int? CanReadJobs { get; set; }

        public int? CanReadJobHistory { get; set; }
    }

    public sealed class SsrsAccessRow
    {
        public int? HasExecutionLog3 { get; set; }

        public int? CanReadExecutionLog3 { get; set; }
    }
}
