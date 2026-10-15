using System.Globalization;
using RSM.Core.Model;
using RSM.Parsers.Iis;
using RSM.Safety.Files;

namespace RSM.Collectors.Iis;

/// <summary>Per-site incremental read position and the recent requests (rolling <see cref="IisLogCollector.Window"/>).</summary>
public sealed class SiteLogState
{
    public string? File { get; set; }

    public long Offset { get; set; }

    public string[] Fields { get; set; } = [];

    public List<W3CRecord> Recent { get; } = [];

    /// <summary>p95 per URL stem of earlier windows (the baseline for IIS-LAT-001), trimmed to <see cref="IisLogCollector.BaselineSpan"/>.</summary>
    public Dictionary<string, List<(DateTimeOffset At, int P95)>> P95History { get; } = new(StringComparer.OrdinalIgnoreCase);

    public DateTimeOffset? LastHistoryAt { get; set; }
}

public sealed class IisLogState
{
    public Dictionary<string, SiteLogState> Sites { get; } = new(StringComparer.OrdinalIgnoreCase);

    public SiteLogState HttpErr { get; } = new();

    public List<(DateTimeOffset At, string[] Fields, string Line)> HttpErrLines { get; } = [];

    public Dictionary<string, IReadOnlyList<FrebEntry>> Freb { get; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>Latency regression of one URL stem against its own earlier p95 (IIS-LAT-001).</summary>
public sealed record StemRegression(string UriStem, long Requests, int P95Ms, int BaselineP95Ms);

public sealed record SiteLogSummary(string Site, string? LogFile, W3CSummary? Summary, IReadOnlyList<StemRegression> Regressions, StdoutSummary? Stdout, IReadOnlyList<FrebEntry> Freb, string? Problem);

public sealed record IisLogPayload(IReadOnlyList<SiteLogSummary> Sites, IReadOnlyList<HttpErrReasonCount> HttpErr, IReadOnlyList<HttpErrHour> HttpErrHours, TimeSpan Window);

/// <summary>
/// §10.5–10.7 in the live view: reads only what IIS appended since the last tick (≤ <see cref="MaxBytesPerTick"/> per
/// file, FileShare.ReadWrite), keeps the last <see cref="Window"/> of requests per site and derives the 5xx rate,
/// 503 bursts, ASP.NET Core Module errors, p95 per URL stem against that stem's own recent baseline, HTTPERR reasons,
/// stdout error lines and existing FREB files. Large time ranges are an on-demand analysis (with a time estimate),
/// never an automatic one.
/// </summary>
public sealed class IisLogCollector : IisCollectorBase<IisLogState>
{
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan HttpErrWindow = TimeSpan.FromMinutes(60);
    public static readonly TimeSpan BaselineSpan = TimeSpan.FromHours(24);
    public const long MaxBytesPerTick = 16 * 1024 * 1024;
    public const int MaxRecentPerSite = 300_000;
    public const int MinRequestsForLatency = 20;
    public const double RegressionFactor = 1.5;

    public override string Id => "iis.logs";

    public override CollectorCost Cost => CollectorCost.Medium;

    public override TimeSpan DefaultInterval => TimeSpan.FromMinutes(5);

    protected override string Capability => "iis.logs";

    protected override async Task CollectCoreAsync(INodeContext node, IisNodeTransport transport, IisLogState state, CollectContext context, LiveOutput output, List<string> problems, CancellationToken cancellationToken)
    {
        var xml = await TryReadAsync(transport.Files, transport.Files.Map(IisNodeTransport.ApplicationHostPath), "applicationHost.config", problems, cancellationToken).ConfigureAwait(false);
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

        var now = Clock.GetUtcNow();
        var sites = new List<SiteLogSummary>();
        foreach (var site in config.Sites)
        {
            var siteState = state.Sites.TryGetValue(site.Name, out var s) ? s : state.Sites[site.Name] = new SiteLogState();
            string? problem = null;
            if (site.LogEnabled && string.Equals(site.LogFormat ?? "W3C", "W3C", StringComparison.OrdinalIgnoreCase))
            {
                var dir = transport.Files.Map(ServerPaths.Combine(site.LogDirectory ?? @"%SystemDrive%\inetpub\logs\LogFiles", $"W3SVC{site.Id}"));
                problem = dir is null ? "log folder cannot be mapped" : await ReadNewAsync(transport.Files, dir, "u_ex*.log", siteState, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                problem = "W3C logging disabled";
            }

            var stdout = await ReadStdoutAsync(transport.Files, config, site, cancellationToken).ConfigureAwait(false);
            var freb = await ReadFrebAsync(transport.Files, site, state, problems, cancellationToken).ConfigureAwait(false);
            sites.Add(Analyze(site.Name, siteState, now, stdout, freb, problem));
        }

        var httpErrDir = transport.Files.Map(IisNodeTransport.HttpErrPath);
        if (httpErrDir is not null)
        {
            var problem = await ReadNewAsync(transport.Files, httpErrDir, "httperr*.log", state.HttpErr, cancellationToken, httpErr: state.HttpErrLines).ConfigureAwait(false);
            if (problem is not null and not "no log files")
            {
                problems.Add($"HTTPERR: {problem}");
            }
        }

        foreach (var p in sites.Where(s => s.Problem is not null && s.Problem is not ("W3C logging disabled" or "no log files")))
        {
            problems.Add($"{p.Site} logs: {p.Problem}");
        }

        Apply(sites, state, now, output);
    }

    /// <summary>
    /// Reads what was appended to the newest matching file since the last read (the tail on the first read, after
    /// the file's #Fields header). Returns a problem text or null.
    /// </summary>
    internal static async Task<string?> ReadNewAsync(IServerFiles files, string directory, string pattern, SiteLogState state, CancellationToken cancellationToken,
        List<(DateTimeOffset, string[], string)>? httpErr = null)
    {
        ServerFileInfo? newest;
        try
        {
            newest = files.List(directory, pattern, 5).FirstOrDefault();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return ex.GetType().Name;
        }

        if (newest is null)
        {
            return "no log files";
        }

        try
        {
            if (!string.Equals(state.File, newest.Path, StringComparison.OrdinalIgnoreCase))
            {
                // New or rolled-over file: take the header, then start at most MaxBytesPerTick before the end.
                var (head, _) = await files.ReadFromAsync(newest.Path, 0, 64 * 1024, cancellationToken).ConfigureAwait(false);
                state.Fields = head.Split('\n').Select(l => l.TrimEnd('\r')).LastOrDefault(l => l.StartsWith("#Fields:", StringComparison.Ordinal)) is { } f
                    ? f[8..].Split(' ', StringSplitOptions.RemoveEmptyEntries) : [];
                state.File = newest.Path;
                state.Offset = Math.Max(0, newest.Length - MaxBytesPerTick);
            }

            var (text, next) = await files.ReadFromAsync(newest.Path, state.Offset, MaxBytesPerTick, cancellationToken).ConfigureAwait(false);
            var skipPartial = state.Offset > 0 && next > state.Offset && text.Length > 0 && !LineStartsAt(text);
            state.Offset = next;
            var lines = text.Split('\n');
            for (var i = skipPartial ? 1 : 0; i < lines.Length; i++)
            {
                var line = lines[i].TrimEnd('\r');
                if (line.Length == 0)
                {
                    continue;
                }

                if (line[0] == '#')
                {
                    if (line.StartsWith("#Fields:", StringComparison.Ordinal))
                    {
                        state.Fields = line[8..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    }

                    continue;
                }

                if (httpErr is not null)
                {
                    if (DateTime.TryParseExact(line.Length >= 19 ? line[..19] : line, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture,
                            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var t))
                    {
                        httpErr.Add((new DateTimeOffset(t, TimeSpan.Zero), state.Fields, line));
                    }
                }
                else if (W3CAnalyzer.Parse(line, state.Fields) is { } record)
                {
                    state.Recent.Add(record);
                }
            }

            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return ex.GetType().Name;
        }
    }

    /// <summary>A tail read starting mid-file begins inside a line unless the text starts with a date or a directive.</summary>
    private static bool LineStartsAt(string text) => text[0] == '#' || (text.Length >= 5 && char.IsAsciiDigit(text[0]) && text[4] == '-');

    private static async Task<StdoutSummary?> ReadStdoutAsync(IServerFiles files, ApplicationHostConfig config, IisSite site, CancellationToken cancellationToken)
    {
        if (site.RootPhysicalPath is not { } root || files.Map(ServerPaths.Combine(root, "web.config")) is not { } webPath || !files.Exists(webPath))
        {
            return null;
        }

        try
        {
            var web = WebConfigParser.Parse(await files.ReadAllTextAsync(webPath, cancellationToken).ConfigureAwait(false));
            if (web.StdoutLogEnabled != true || string.IsNullOrWhiteSpace(web.StdoutLogFile))
            {
                return null;
            }

            var path = ServerPaths.Combine(root, web.StdoutLogFile);
            var dir = path is null ? null : files.Map(path[..Math.Max(0, path.LastIndexOf('\\'))]);
            var prefix = path?[(path.LastIndexOf('\\') + 1)..];
            if (dir is null || string.IsNullOrEmpty(prefix))
            {
                return null;
            }

            var newest = files.List(dir, prefix + "*.log", 1).FirstOrDefault();
            if (newest is null)
            {
                return null;
            }

            var (text, truncated) = await files.ReadTailAsync(newest.Path, 256 * 1024, cancellationToken).ConfigureAwait(false);
            return StdoutLog.Summarize(newest.Name, text, truncated);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException or FormatException)
        {
            return null;
        }
    }

    /// <summary>Existing FREB files only (RSM never enables tracing); re-parsed only when the newest file changes.</summary>
    private static async Task<IReadOnlyList<FrebEntry>> ReadFrebAsync(IServerFiles files, IisSite site, IisLogState state, List<string> problems, CancellationToken cancellationToken)
    {
        var dir = files.Map($@"%SystemDrive%\inetpub\logs\FailedReqLogFiles\W3SVC{site.Id}");
        if (dir is null)
        {
            return [];
        }

        try
        {
            var list = files.List(dir, "fr*.xml", 20);
            if (list.Count == 0)
            {
                return [];
            }

            if (state.Freb.TryGetValue(site.Name, out var cached) && cached.Count > 0 && cached[0].File == list[0].Name)
            {
                return cached;
            }

            var entries = new List<FrebEntry>();
            foreach (var f in list.Where(f => f.Length < 4 * 1024 * 1024))
            {
                var (text, _) = await files.ReadFromAsync(f.Path, 0, 4 * 1024 * 1024, cancellationToken).ConfigureAwait(false);
                if (FrebLog.Parse(f.Name, text) is { } e)
                {
                    entries.Add(e);
                }
            }

            return state.Freb[site.Name] = entries;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            problems.Add($"FREB {site.Name}: {ex.GetType().Name}");
            return [];
        }
    }

    /// <summary>Prunes the window, summarises it and compares each stem's p95 with its own earlier windows.</summary>
    internal static SiteLogSummary Analyze(string site, SiteLogState state, DateTimeOffset now, StdoutSummary? stdout, IReadOnlyList<FrebEntry> freb, string? problem)
    {
        state.Recent.RemoveAll(r => r.AtUtc < now - Window);
        if (state.Recent.Count > MaxRecentPerSite)
        {
            state.Recent.RemoveRange(0, state.Recent.Count - MaxRecentPerSite);
        }

        if (state.Recent.Count == 0 && state.File is null)
        {
            return new SiteLogSummary(site, null, null, [], stdout, freb, problem);
        }

        var analyzer = new W3CAnalyzer(now - Window, now);
        foreach (var r in state.Recent)
        {
            analyzer.Add(r);
        }

        var summary = analyzer.Summarize(topStems: 200);
        var regressions = new List<StemRegression>();
        foreach (var stem in summary.UrlStems.Where(u => u.Requests >= MinRequestsForLatency))
        {
            if (state.P95History.TryGetValue(stem.UriStem, out var history) && history.Count >= 3)
            {
                var baseline = Median(history.Select(h => h.P95));
                if (baseline > 0 && stem.P95Ms > RegressionFactor * baseline && stem.P95Ms - baseline >= 200)
                {
                    regressions.Add(new StemRegression(stem.UriStem, stem.Requests, stem.P95Ms, baseline));
                }
            }
        }

        // One history point per full window, so overlapping ticks do not weigh the same requests several times.
        if (state.LastHistoryAt is null || now - state.LastHistoryAt >= Window)
        {
            foreach (var stem in summary.UrlStems.Where(u => u.Requests >= MinRequestsForLatency))
            {
                if (!state.P95History.TryGetValue(stem.UriStem, out var history))
                {
                    state.P95History[stem.UriStem] = history = [];
                }

                history.Add((now, stem.P95Ms));
                history.RemoveAll(h => h.At < now - BaselineSpan);
            }

            state.LastHistoryAt = now;
        }

        return new SiteLogSummary(site, state.File, summary, regressions.OrderByDescending(r => (double)r.P95Ms / r.BaselineP95Ms).ToList(), stdout, freb, problem);
    }

    private static int Median(IEnumerable<int> values)
    {
        var sorted = values.Order().ToList();
        return sorted.Count == 0 ? 0 : sorted[(sorted.Count - 1) / 2];
    }

    /// <summary>ASP.NET Core Module statuses (spec §10.5): 502.3, 502.5, 500.30–500.37.</summary>
    public static bool IsAncmStatus(string status) =>
        status is "502.3" or "502.5" || (status.StartsWith("500.3", StringComparison.Ordinal) && status.Length == 6 && char.IsAsciiDigit(status[5]) && status[5] <= '7');

    internal static void Apply(IReadOnlyList<SiteLogSummary> sites, IisLogState state, DateTimeOffset now, LiveOutput output)
    {
        state.HttpErrLines.RemoveAll(l => l.At < now - HttpErrWindow);
        var httpErr = new HttpErrAnalyzer(now - HttpErrWindow);
        string[]? fields = null;
        foreach (var (_, f, line) in state.HttpErrLines)
        {
            if (!ReferenceEquals(f, fields))
            {
                httpErr.AddLine("#Fields: " + string.Join(' ', f));
                fields = f;
            }

            httpErr.AddLine(line);
        }

        var reasons = httpErr.Reasons();
        output.Payload["logs"] = new IisLogPayload(sites, reasons, httpErr.Hours(), Window);
        if (state.HttpErr.File is not null)
        {
            output.Fact("iis.httperr.server_side_60m", reasons.Where(r => HttpErrAnalyzer.IsServerSide(r.Reason)).Sum(r => r.Count));
            output.Fact("iis.httperr.queue_full_60m", reasons.Where(r => r.Reason == "QueueFull").Sum(r => r.Count));
            output.Fact("iis.httperr.top_server_side", string.Join(", ", reasons.Where(r => HttpErrAnalyzer.IsServerSide(r.Reason)).Take(3).Select(r => $"{r.Reason} ×{r.Count}")));
        }

        if (sites.Any(s => s.Summary is not null))
        {
            output.Fact("iis.ancm_errors_15m", sites.Where(s => s.Summary is not null).Sum(s => s.Summary!.Statuses.Where(c => IsAncmStatus(c.Status)).Sum(c => c.Count)));
            output.Fact("iis.requests_15m", sites.Where(s => s.Summary is not null).Sum(s => s.Summary!.Requests));
        }

        foreach (var site in sites)
        {
            var key = $"site[{IisConfigCollector.Instance(site.Site)}]";
            if (site.Stdout is { } stdout)
            {
                output.Fact($"{key}.stdout_error_lines", stdout.ErrorLines);
            }

            output.Fact($"{key}.freb_files", site.Freb.Count);
            if (site.Summary is not { } s)
            {
                continue;
            }

            output.Fact($"{key}.requests_15m", s.Requests);
            output.Fact($"{key}.err5xx_15m", s.Errors5xx);
            output.Fact($"{key}.err5xx_pct_15m", s.ErrorRate5xx);
            output.Fact($"{key}.status503_15m", s.Statuses.Where(c => c.Status.StartsWith("503", StringComparison.Ordinal)).Sum(c => c.Count));
            output.Fact($"{key}.burst_503", s.Bursts.Any(b => b.Status503 >= W3CAnalyzer.BurstMin5xx));
            output.Fact($"{key}.ancm_errors_15m", s.Statuses.Where(c => IsAncmStatus(c.Status)).Sum(c => c.Count));
            output.Fact($"{key}.client_disconnects_15m", s.ClientDisconnects);
            output.Fact($"{key}.logs_time_taken_seen", s.HasTimeTaken);
            if (s.Slowest is { } slowest)
            {
                output.Fact($"{key}.slowest_stem", slowest.UriStem);
                output.Fact($"{key}.slowest_p95_ms", slowest.P95Ms);
            }

            output.Fact($"{key}.lat_regressed_stems", site.Regressions.Count);
            output.Fact($"{key}.lat_regressed_top", string.Join(", ", site.Regressions.Take(3).Select(r => $"{r.UriStem} p95 {r.P95Ms} ms (baseline {r.BaselineP95Ms} ms)")));
            output.Sample("iis.log.requests_15m", site.Site, s.Requests, now);
            output.Sample("iis.log.err5xx_pct_15m", site.Site, s.ErrorRate5xx, now);
        }
    }
}
