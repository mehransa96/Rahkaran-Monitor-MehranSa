using System.Globalization;
using System.Text.Json;
using RSM.Collectors.Windows;
using RSM.Core.Model;
using RSM.Parsers.Iis;

namespace RSM.Collectors.Iis;

/// <summary>The web.config of one application (masked by <see cref="WebConfigParser"/>), or why it could not be read.</summary>
public sealed record SiteWebConfig(string Site, string AppPath, string? PhysicalPath, WebConfigSummary? Summary, string? Problem);

/// <summary>The certificate of one https binding (null when unknown: remote node, store not readable, not found).</summary>
public sealed record BindingCertificate(string Site, string Binding, string Thumbprint, CertificateInfo? Certificate);

public sealed record IisConfigPayload(ApplicationHostConfig Config, IReadOnlyList<SiteWebConfig> WebConfigs, IReadOnlyList<BindingCertificate> Certificates, bool CertificatesReadable);

public sealed class NoIisState;

/// <summary>
/// §10.1, 10.2, 10.8–10.11: sites, pools, web.config (secrets masked), ARR farms, URL Rewrite rules and bound
/// certificates, parsed from applicationHost.config read through the local path or the admin share. Read-only; RSM
/// never changes logging fields, pools or rules — findings are text recommendations.
/// </summary>
public sealed class IisConfigCollector : IisCollectorBase<NoIisState>
{
    public const int MaxWebConfigs = 30;

    public override string Id => "iis.config";

    public override TimeSpan DefaultInterval => TimeSpan.FromMinutes(10);

    private static readonly CimQuery ProcessorInstances = new("Win32_PerfRawData_Counters_ProcessorInformation", ["Name"]);

    protected override async Task CollectCoreAsync(INodeContext node, IisNodeTransport transport, NoIisState state, CollectContext context, LiveOutput output, List<string> problems, CancellationToken cancellationToken)
    {
        var xml = await TryReadAsync(transport.Files, transport.Files.Map(IisNodeTransport.ApplicationHostPath), "applicationHost.config (Administrators / admin share)", problems, cancellationToken).ConfigureAwait(false);
        if (xml is null)
        {
            return;
        }

        ApplicationHostConfig config;
        try
        {
            config = ApplicationHostConfigParser.Parse(xml);
        }
        catch (Exception ex) when (ex is System.Xml.XmlException or FormatException)
        {
            problems.Add($"applicationHost.config: {ex.GetType().Name}");
            return;
        }

        var webConfigs = new List<SiteWebConfig>();
        foreach (var (site, app) in config.Sites.SelectMany(s => s.Applications.Select(a => (s, a))).Take(MaxWebConfigs))
        {
            var physical = ServerPaths.Combine(app.PhysicalPath, "web.config");
            var access = transport.Files.Map(physical);
            if (access is null || !transport.Files.Exists(access))
            {
                webConfigs.Add(new SiteWebConfig(site.Name, app.Path, app.PhysicalPath, null, access is null ? "path cannot be mapped" : "no web.config"));
                continue;
            }

            var appProblems = new List<string>();
            var text = await TryReadAsync(transport.Files, access, "web.config", appProblems, cancellationToken).ConfigureAwait(false);
            WebConfigSummary? summary = null;
            if (text is not null)
            {
                try
                {
                    summary = WebConfigParser.Parse(text);
                }
                catch (Exception ex) when (ex is System.Xml.XmlException or FormatException)
                {
                    appProblems.Add(ex.GetType().Name);
                }
            }

            webConfigs.Add(new SiteWebConfig(site.Name, app.Path, app.PhysicalPath, summary, appProblems.Count == 0 ? null : string.Join("; ", appProblems)));
        }

        var certificates = new List<BindingCertificate>();
        foreach (var (site, binding) in config.Sites.SelectMany(s => s.Bindings.Where(b => b.CertificateHash is not null).Select(b => (s, b))))
        {
            CertificateInfo? cert = null;
            try
            {
                cert = transport.Certificates?.Find(binding.CertificateHash!, binding.CertificateStore);
            }
            catch (System.Security.Cryptography.CryptographicException ex)
            {
                problems.Add($"certificate store: {ex.GetType().Name}");
            }

            certificates.Add(new BindingCertificate(site.Name, binding.BindingInformation, binding.CertificateHash!, cert));
        }

        if (node.GetTransport<WindowsNodeTransport>() is { } windows)
        {
            try
            {
                var rows = await windows.Cim.QueryAsync(ProcessorInstances, cancellationToken).ConfigureAwait(false);
                output.Fact("os.numa_nodes", NumaNodes(rows.Select(r => r.String("Name")).OfType<string>()));
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not ArgumentException)
            {
                problems.Add($"NUMA layout: {ex.GetType().Name}");
            }
        }

        Apply(config, webConfigs, certificates, transport.Certificates is not null, Clock.GetUtcNow(), output);
    }

    /// <summary>"Processor Information" instances are "&lt;node&gt;,&lt;cpu&gt;": the number of distinct nodes.</summary>
    internal static int? NumaNodes(IEnumerable<string> instances)
    {
        var nodes = instances.Where(i => i.Contains(',', StringComparison.Ordinal)).Select(i => i[..i.IndexOf(',', StringComparison.Ordinal)])
            .Where(n => n.All(char.IsAsciiDigit) && n.Length > 0).Distinct().Count();
        return nodes == 0 ? null : nodes;
    }

    /// <summary>Instance names may not contain ']' (fact key syntax).</summary>
    public static string Instance(string name) => name.Replace(']', ')').Replace('[', '(');

    internal static string Signature(IReadOnlyDictionary<string, string> signature) => JsonSerializer.Serialize(signature);

    /// <summary>Pure analysis (unit-tested on the fixture).</summary>
    internal static void Apply(ApplicationHostConfig config, IReadOnlyList<SiteWebConfig> webConfigs, IReadOnlyList<BindingCertificate> certificates, bool certificatesReadable, DateTimeOffset now, LiveOutput output)
    {
        output.Payload["iis"] = new IisConfigPayload(config, webConfigs, certificates, certificatesReadable);
        var rootConfigs = webConfigs.Where(w => w.AppPath == "/" && w.Summary is not null).ToDictionary(w => w.Site, w => w.Summary!, StringComparer.OrdinalIgnoreCase);
        var aspNetCoreSites = webConfigs.Where(w => w.Summary?.ProcessPath is not null || w.Summary?.HostingModel is not null).Select(w => w.Site).ToHashSet(StringComparer.OrdinalIgnoreCase);

        output.Fact("iis.site_count", config.Sites.Count);
        output.Fact("iis.aux_site_count", config.Sites.Count(s => s.RahkaranRole == "Aux"));
        output.Fact("iis.rahkaran_site_count", config.Sites.Count(s => s.RahkaranRole is not null));
        output.Fact("iis.arr_proxy_enabled", config.ArrProxyEnabled);
        output.Fact("iis.farm_count", config.WebFarms.Count);
        output.Fact("iis.app_segments", string.Join("|", config.Sites.SelectMany(s => s.Applications).Select(a => a.Path.Trim('/').Split('/')[0]).Where(p => p.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)));

        foreach (var site in config.Sites)
        {
            var key = $"site[{Instance(site.Name)}]";
            output.Fact($"{key}.role", site.RahkaranRole ?? string.Empty);
            output.Fact($"{key}.is_rahkaran", site.RahkaranRole is not null);
            output.Fact($"{key}.auto_start", site.AutoStart);
            output.Fact($"{key}.log_enabled", site.LogEnabled);
            output.Fact($"{key}.log_w3c", string.Equals(site.LogFormat ?? "W3C", "W3C", StringComparison.OrdinalIgnoreCase));
            output.Fact($"{key}.logs_time_taken", site.LogsTimeTaken);
            output.Fact($"{key}.aspnetcore", aspNetCoreSites.Contains(site.Name));
            if (rootConfigs.TryGetValue(site.Name, out var web))
            {
                output.Fact($"{key}.hosting_model", web.HostingModel ?? string.Empty);
                output.Fact($"{key}.stdout_log_enabled", web.StdoutLogEnabled ?? false);
                output.Fact($"{key}.webconfig_signature", Signature(web.DriftSignature));
            }
        }

        foreach (var pool in config.AppPools)
        {
            var key = $"pool[{Instance(pool.Name)}]";
            var sites = config.Sites.Where(s => s.AppPools.Contains(pool.Name, StringComparer.OrdinalIgnoreCase)).ToList();
            output.Fact($"{key}.no_managed_code", pool.NoManagedCode);
            output.Fact($"{key}.managed_runtime", pool.ManagedRuntimeVersion);
            output.Fact($"{key}.start_mode", pool.StartMode);
            output.Fact($"{key}.idle_timeout_min", pool.IdleTimeout.TotalMinutes);
            output.Fact($"{key}.periodic_restart_min", pool.PeriodicRestart.TotalMinutes);
            output.Fact($"{key}.has_restart_schedule", pool.RestartSchedule.Count > 0);
            output.Fact($"{key}.restart_schedule", string.Join(", ", pool.RestartSchedule.Select(t => t.ToString(@"hh\:mm", CultureInfo.InvariantCulture))));
            output.Fact($"{key}.enable32bit", pool.Enable32Bit);
            output.Fact($"{key}.max_processes", pool.MaxProcesses);
            output.Fact($"{key}.rapid_fail", pool.RapidFailProtection);
            output.Fact($"{key}.queue_length", pool.QueueLength);
            output.Fact($"{key}.numa_assignment", pool.NumaNodeAssignment);
            output.Fact($"{key}.numa_affinity_mode", pool.NumaNodeAffinityMode);
            output.Fact($"{key}.smp_affinitized", pool.SmpAffinitized);
            output.Fact($"{key}.serves_rahkaran", sites.Any(s => s.RahkaranRole is not null));
            output.Fact($"{key}.serves_aspnetcore", sites.Any(s => aspNetCoreSites.Contains(s.Name)));
            output.Fact($"{key}.site_count", sites.Count);
            output.Fact($"{key}.signature", Signature(pool.DriftSignature));
        }

        foreach (var farm in config.WebFarms)
        {
            var key = $"farm[{Instance(farm.Name)}]";
            output.Fact($"{key}.enabled", farm.Enabled);
            output.Fact($"{key}.servers", farm.Servers.Count);
            output.Fact($"{key}.enabled_servers", farm.Servers.Count(s => s.Enabled));
            output.Fact($"{key}.health_url_set", !string.IsNullOrWhiteSpace(farm.HealthCheckUrl));
            output.Fact($"{key}.load_balancing", farm.LoadBalancing);
        }

        var farmNames = config.WebFarms.Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var farmServers = config.WebFarms.SelectMany(f => f.Servers).Select(s => s.Address).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var rule in config.RewriteRules.Concat(webConfigs.Where(w => w.Summary is not null).SelectMany(w => w.Summary!.RewriteRules.Select(r => r with { Scope = $"{w.Site}{w.AppPath}" }))))
        {
            if (!rule.Enabled || rule.ActionType is not ("Rewrite" or null))
            {
                continue;
            }

            var key = $"rewrite[{Instance($"{rule.Scope}/{rule.Name}")}]";
            output.Fact($"{key}.target_host", rule.TargetHost ?? string.Empty);
            output.Fact($"{key}.target_segment", rule.TargetPathSegment ?? string.Empty);
            if (rule.TargetHost is { } host && config.ArrProxyEnabled)
            {
                output.Fact($"{key}.targets_farm", farmNames.Contains(host));
                output.Fact($"{key}.target_unknown_farm", LooksLikeFarmName(host) && !farmNames.Contains(host) && !farmServers.Contains(host));
            }
        }

        foreach (var c in certificates)
        {
            if (c.Certificate is not { } cert)
            {
                continue;
            }

            var key = $"cert[{Instance($"{c.Site}|{c.Binding}")}]";
            output.Fact($"{key}.days_left", Math.Floor((cert.NotAfterUtc - now).TotalDays));
            output.Fact($"{key}.expired", cert.NotAfterUtc <= now);
            output.Fact($"{key}.key_size", cert.KeySize);
            output.Fact($"{key}.chain_valid", cert.ChainValid);
            output.Fact($"{key}.subject", cert.Subject);
        }
    }

    /// <summary>
    /// A bare name (no dots, not an IP, not localhost) in a rewrite target is how ARR routes to a farm; a missing farm
    /// with such a name breaks the route. Hosts with a domain or an IP are real servers and are not judged.
    /// </summary>
    internal static bool LooksLikeFarmName(string host) =>
        host.Length > 0 && !host.Contains('.', StringComparison.Ordinal) && !host.Contains(':', StringComparison.Ordinal)
        && !host.Equals("localhost", StringComparison.OrdinalIgnoreCase) && !System.Net.IPAddress.TryParse(host, out _);
}
