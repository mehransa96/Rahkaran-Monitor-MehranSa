using RSM.Core.Model;
using RSM.Core.Rules;
using RSM.Core.Snapshots;
using RSM.Reporting;
using RSM.Reporting.Model;

namespace RSM.Hosting;

/// <summary>Result of a PDF export: the PDF path, or the saved HTML path plus why no PDF could be made.</summary>
public sealed record PdfExport(string? PdfPath, string HtmlPath, string? UnavailableReason);

/// <summary>Turns a saved HTML report (path relative to the data folder) into a PDF in the data folder.</summary>
public interface IPdfPrinter
{
    /// <summary>Returns the absolute PDF path, or null with a reason when no PDF engine is available.</summary>
    Task<(string? PdfPath, string? UnavailableReason)> PrintAsync(string htmlRelativePath, string pdfRelativePath, CancellationToken cancellationToken);
}

/// <summary>Exports a snapshot as HTML (fa/en), PDF or JSON bundle, using the same rule set the diagnosis used.</summary>
public sealed class ReportService(RuleRepository ruleRepository, SnapshotService snapshots, ReportExporter exporter, IPdfPrinter? pdfPrinter = null)
{
    public Task<string> ExportHtmlAsync(SiteSnapshot snapshot, UiLanguage language, bool mask, CancellationToken cancellationToken) =>
        exporter.WriteHtmlAsync(snapshot, Rules(), new ReportOptions(language, mask, DateTimeOffset.UtcNow, TimeZoneInfo.Local), cancellationToken);

    public Task<string> ExportBundleAsync(SiteSnapshot snapshot, bool mask, CancellationToken cancellationToken) =>
        exporter.WriteBundleAsync(snapshot, mask, DateTimeOffset.UtcNow, cancellationToken);

    /// <summary>
    /// PDF (Q1): the HTML report printed by the desktop app's WebView2. Without WebView2 (not installed, or the CLI)
    /// the HTML is still saved and the caller tells the user to open it in a browser and print it.
    /// </summary>
    public async Task<PdfExport> ExportPdfAsync(SiteSnapshot snapshot, UiLanguage language, bool mask, CancellationToken cancellationToken)
    {
        var html = await ExportHtmlAsync(snapshot, language, mask, cancellationToken).ConfigureAwait(false);
        var htmlRelative = $"{ReportExporter.Folder}/{Path.GetFileName(html)}";
        if (pdfPrinter is null)
        {
            return new PdfExport(null, html, "PDF needs the desktop app (WebView2).");
        }

        var (pdf, reason) = await pdfPrinter.PrintAsync(htmlRelative, Path.ChangeExtension(htmlRelative, ".pdf"), cancellationToken).ConfigureAwait(false);
        return new PdfExport(pdf, html, reason);
    }

    public Task<string> ExportInvestigationAsync(RSM.Storage.Investigations.SavedInvestigation investigation, UiLanguage language, CancellationToken cancellationToken) =>
        exporter.WriteInvestigationHtmlAsync(investigation.Result, investigation.Capture, investigation.SiteName, language,
            SlownessDefinitionSource.Cached.Detectors, cancellationToken);

    private List<RuleDefinition> Rules() => ruleRepository.Load(snapshots.RuleOverrideDirectory).Rules.Select(r => r.Definition).ToList();
}
