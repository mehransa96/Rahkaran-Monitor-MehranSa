using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RSM.Core.Model;
using RSM.Core.Profiles;
using RSM.Core.Snapshots;
using RSM.Hosting;
using RSM.Reporting.Bundles;
using RSM.Safety.Files;
using RSM.Storage.Profiles;
using RSM.Storage.Snapshots;

namespace RSM.Cli;

/// <summary>
/// Runs one CLI command against the same services as the app. Exit codes: 0 ok, 1 usage, 2 failed, 3 not found.
/// </summary>
public sealed class CliRunner(IDataFolder dataFolder, TextWriter output)
{
    public async Task<int> RunAsync(CliOptions options, CancellationToken cancellationToken)
    {
        if (options.Command == CliCommand.Help)
        {
            if (options.Error is { } error)
            {
                output.WriteLine($"Error: {error}");
                output.WriteLine();
            }

            output.WriteLine(CliOptions.Usage);
            return options.Error is null ? 0 : 1;
        }

        using var host = RsmHost.Build(dataFolder);
        var services = host.Services;
        try
        {
            return options.Command switch
            {
                CliCommand.List => await ListAsync(services, cancellationToken),
                CliCommand.Snapshot => await SnapshotAsync(services, options, cancellationToken),
                CliCommand.Collect => await CollectAsync(services, options, cancellationToken),
                CliCommand.Investigate => await InvestigateAsync(services, options, cancellationToken),
                CliCommand.Export => await ExportAsync(services, options, cancellationToken),
                CliCommand.Import => await ImportAsync(services, options, cancellationToken),
                _ => 1,
            };
        }
        catch (OperationCanceledException)
        {
            output.WriteLine("Stopped.");
            return 2;
        }
        catch (Exception ex) when (ex is BundleFormatException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            output.WriteLine($"Error: {ex.Message}");
            return 2;
        }
    }

    private async Task<SiteProfile?> FindSiteAsync(IServiceProvider services, string nameOrId, CancellationToken cancellationToken)
    {
        var sites = await services.GetRequiredService<ProfileStore>().LoadAllAsync(cancellationToken);
        var site = sites.FirstOrDefault(s => s.Id == nameOrId) ?? sites.FirstOrDefault(s => string.Equals(s.Name, nameOrId, StringComparison.OrdinalIgnoreCase));
        if (site is null)
        {
            output.WriteLine($"Site '{nameOrId}' not found. Known sites: {string.Join(", ", sites.Select(s => s.Name))}");
        }

        return site;
    }

    private async Task<int> ListAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        output.WriteLine("Sites:");
        foreach (var site in await services.GetRequiredService<ProfileStore>().LoadAllAsync(cancellationToken))
        {
            output.WriteLine($"  {site.Id}  {site.Name}  ({site.Nodes.Count} nodes)");
        }

        output.WriteLine("Snapshots (newest first):");
        foreach (var s in await services.GetRequiredService<SnapshotStore>().ListAsync(null, 20, cancellationToken))
        {
            output.WriteLine($"  {s.Id}  {s.SiteName}  {s.StartedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)}  C{s.Critical} W{s.Warning} I{s.Info}");
        }

        return 0;
    }

    private async Task<int> SnapshotAsync(IServiceProvider services, CliOptions options, CancellationToken cancellationToken)
    {
        if (await FindSiteAsync(services, options.Site!, cancellationToken) is not { } site)
        {
            return 3;
        }

        var progress = new Progress<string>(p => output.WriteLine($"  {p}"));
        var snapshot = await services.GetRequiredService<SnapshotService>().RunAsync(site, new SnapshotOptions(options.Heavy), progress, cancellationToken);
        WriteSummary(snapshot);
        return 0;
    }

    private async Task<int> CollectAsync(IServiceProvider services, CliOptions options, CancellationToken cancellationToken)
    {
        if (await FindSiteAsync(services, options.Site!, cancellationToken) is not { } site)
        {
            return 3;
        }

        var live = services.GetRequiredService<LiveMonitorService>();
        await live.StartAsync(site, new Progress<string>(n => output.WriteLine($"  connecting {n}")), cancellationToken);
        var until = DateTimeOffset.Now + options.Duration!.Value;
        output.WriteLine($"Collecting {site.Name} into local history until {until:yyyy-MM-dd HH:mm} (Ctrl+C stops earlier).");
        try
        {
            while (DateTimeOffset.Now < until)
            {
                var wait = until - DateTimeOffset.Now;
                await Task.Delay(wait < TimeSpan.FromMinutes(1) ? wait : TimeSpan.FromMinutes(1), cancellationToken);
                if (live.Current is { } session)
                {
                    output.WriteLine($"  {DateTimeOffset.Now:HH:mm}  findings {session.AllFindings().Count}");
                }
            }
        }
        catch (OperationCanceledException)
        {
            output.WriteLine("Stopping early.");
        }
        finally
        {
            await live.StopAsync(); // flushes the history buffer
        }

        output.WriteLine("Collection finished; history is in the data folder.");
        return 0;
    }

    private async Task<int> InvestigateAsync(IServiceProvider services, CliOptions options, CancellationToken cancellationToken)
    {
        if (await FindSiteAsync(services, options.Site!, cancellationToken) is not { } site)
        {
            return 3;
        }

        var live = services.GetRequiredService<LiveMonitorService>();
        await live.StartAsync(site, new Progress<string>(n => output.WriteLine($"  connecting {n}")), cancellationToken);
        try
        {
            var duration = options.Duration ?? RSM.Core.Slowness.SlownessCapture.DefaultDuration;
            output.WriteLine($"Capturing for {duration.TotalSeconds:0} s…");
            var saved = await services.GetRequiredService<SlownessInvestigationService>().RunAsync(duration, null, cancellationToken);
            output.WriteLine($"Investigation {saved.Capture.CaptureId}:");
            if (saved.Result.Causes.Count == 0)
            {
                output.WriteLine("  No likely cause found during the capture.");
            }

            var fa = options.Language == "fa";
            foreach (var cause in saved.Result.Causes.Take(5))
            {
                output.WriteLine($"  {cause.Confidence:P0}  {cause.Cause.Id}  {(fa ? cause.Cause.Title.Fa : cause.Cause.Title.En)}");
                foreach (var step in cause.Cause.NextSteps)
                {
                    output.WriteLine($"         - {(fa ? step.Fa : step.En)}");
                }
            }

            return 0;
        }
        finally
        {
            await live.StopAsync();
        }
    }

    private async Task<int> ExportAsync(IServiceProvider services, CliOptions options, CancellationToken cancellationToken)
    {
        var store = services.GetRequiredService<SnapshotStore>();
        var id = options.SnapshotId is null or "latest" ? (await store.ListAsync(null, 1, cancellationToken)).FirstOrDefault()?.Id : options.SnapshotId;
        if (id is null || await store.LoadAsync(id, cancellationToken) is not { } snapshot)
        {
            output.WriteLine($"Snapshot '{options.SnapshotId}' not found (see --list).");
            return 3;
        }

        var reports = services.GetRequiredService<ReportService>();
        var language = options.Language == "fa" ? UiLanguage.Fa : UiLanguage.En;
        switch (options.Format)
        {
            case "bundle":
                output.WriteLine($"Saved: {await reports.ExportBundleAsync(snapshot, options.Mask, cancellationToken)}");
                break;
            case "pdf":
                var pdf = await reports.ExportPdfAsync(snapshot, language, options.Mask, cancellationToken);
                output.WriteLine(pdf.PdfPath is { } p ? $"Saved: {p}" : $"PDF not available ({pdf.UnavailableReason}). HTML saved: {pdf.HtmlPath} — open it in a browser and print to PDF.");
                break;
            default:
                output.WriteLine($"Saved: {await reports.ExportHtmlAsync(snapshot, language, options.Mask, cancellationToken)}");
                break;
        }

        return 0;
    }

    private async Task<int> ImportAsync(IServiceProvider services, CliOptions options, CancellationToken cancellationToken)
    {
        var result = await services.GetRequiredService<BundleImportService>().ImportAsync(options.Files!, cancellationToken);
        output.WriteLine($"Imported {options.Files!.Count} bundle(s) into snapshot {result.Snapshot.Id}.");
        foreach (var warning in result.Warnings)
        {
            output.WriteLine($"  Warning: {warning}");
        }

        foreach (var node in result.Nodes)
        {
            output.WriteLine($"  {node.NodeName}: from {string.Join(", ", node.FromBundles)}; capability gaps: {(node.Gaps.Count == 0 ? "none" : string.Join(", ", node.Gaps.Select(g => $"{g.Id} ({g.State})")))}");
        }

        WriteSummary(result.Snapshot);
        return 0;
    }

    private void WriteSummary(SiteSnapshot snapshot)
    {
        var findings = snapshot.AllFindings.ToList();
        output.WriteLine($"Snapshot {snapshot.Id}: {findings.Count(f => f.Severity == Severity.Critical)} critical, {findings.Count(f => f.Severity == Severity.Warning)} warning, "
            + $"{findings.Count(f => f.Severity == Severity.Info)} info. Export with: RSM.Cli --export {snapshot.Id} --format html");
    }
}
