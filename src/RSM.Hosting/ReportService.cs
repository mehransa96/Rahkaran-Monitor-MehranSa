using RSM.Core.Model;
using RSM.Core.Rules;
using RSM.Core.Snapshots;
using RSM.Reporting;
using RSM.Reporting.Model;

namespace RSM.Hosting;

/// <summary>Exports a snapshot as HTML (fa/en) or JSON bundle, using the same rule set the diagnosis used.</summary>
public sealed class ReportService(RuleRepository ruleRepository, SnapshotService snapshots, ReportExporter exporter)
{
    public Task<string> ExportHtmlAsync(SiteSnapshot snapshot, UiLanguage language, bool mask, CancellationToken cancellationToken) =>
        exporter.WriteHtmlAsync(snapshot, Rules(), new ReportOptions(language, mask, DateTimeOffset.UtcNow, TimeZoneInfo.Local), cancellationToken);

    public Task<string> ExportBundleAsync(SiteSnapshot snapshot, bool mask, CancellationToken cancellationToken) =>
        exporter.WriteBundleAsync(snapshot, mask, DateTimeOffset.UtcNow, cancellationToken);

    private List<RuleDefinition> Rules() => ruleRepository.Load(snapshots.RuleOverrideDirectory).Rules.Select(r => r.Definition).ToList();
}
