using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using RSM.App.Localization;
using RSM.Core.Model;
using RSM.Core.Snapshots;
using RSM.Hosting;

namespace RSM.App.ViewModels;

/// <summary>
/// "Full diagnosis" (Reports page): collects one snapshot of the selected site — read-only queries only — and exports
/// it as HTML (fa/en) or JSON bundle into the data folder. The latest snapshot also feeds the SQL Server and Databases
/// pages. Nothing here acts on a server.
/// </summary>
public sealed partial class DiagnosisViewModel : ObservableObject
{
    private readonly SnapshotService _snapshots;
    private readonly ReportService _reports;
    private readonly StatusBarViewModel _status;
    private readonly ILogger<DiagnosisViewModel> _logger;
    private readonly BundleImportService _import;
    private readonly IUserDialogs _dialogs;
    private readonly RSM.Storage.Snapshots.SnapshotStore _store;
    private CancellationTokenSource? _cts;

    public DiagnosisViewModel(SitesViewModel sites, SnapshotService snapshots, ReportService reports, BundleImportService import, IUserDialogs dialogs,
        RSM.Storage.Snapshots.SnapshotStore store, StatusBarViewModel status, ILogger<DiagnosisViewModel> logger)
    {
        Sites = sites;
        _store = store;
        _import = import;
        _dialogs = dialogs;
        _snapshots = snapshots;
        _reports = reports;
        _status = status;
        _logger = logger;
        ReportInPersian = Loc.Instance.Language == UiLanguage.Fa;
        Loc.Instance.PropertyChanged += (_, _) => Rebuild();
    }

    public SitesViewModel Sites { get; }

    public ObservableCollection<FindingRowViewModel> Findings { get; } = [];

    /// <summary>Saved diagnoses (newest first) for "compare before vs after".</summary>
    public ObservableCollection<SnapshotChoice> SavedSnapshots { get; } = [];

    public ObservableCollection<CompareFindingRow> CompareFindings { get; } = [];

    public ObservableCollection<CompareValueRow> CompareNumbers { get; } = [];

    public ObservableCollection<CompareValueRow> CompareTexts { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CompareSnapshotsCommand))]
    private SnapshotChoice? _compareBefore;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CompareSnapshotsCommand))]
    private SnapshotChoice? _compareAfter;

    [ObservableProperty]
    private string _compareSummary = string.Empty;

    /// <summary>After an import: one line per merged node (source bundles, remaining capability gaps) plus warnings.</summary>
    public ObservableCollection<string> ImportNotes { get; } = [];

    [ObservableProperty]
    private SiteItemViewModel? _selectedSite;

    [ObservableProperty]
    private bool _includeHeavy;

    [ObservableProperty]
    private bool _mask;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ReportInEnglish))]
    private bool _reportInPersian;

    public bool ReportInEnglish
    {
        get => !ReportInPersian;
        set => ReportInPersian = !value;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    [NotifyCanExecuteChangedFor(nameof(DiagnoseCommand), nameof(ExportHtmlCommand), nameof(ExportPdfCommand), nameof(ExportBundleCommand), nameof(ImportBundlesCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private string _progress = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSnapshot), nameof(Summary))]
    [NotifyCanExecuteChangedFor(nameof(ExportHtmlCommand), nameof(ExportPdfCommand), nameof(ExportBundleCommand))]
    private SiteSnapshot? _latest;

    /// <summary>Absolute path of the last exported file, shown in a read-only box to copy (RSM opens nothing itself).</summary>
    [ObservableProperty]
    private string _lastExportPath = string.Empty;

    public bool IsIdle => !IsBusy;

    public bool HasSnapshot => Latest is not null;

    public string Summary => Latest is null
        ? Loc.Instance["Diag_NoSnapshot"]
        : Loc.Instance.Format("Diag_Summary",
            Loc.Instance.FormatTimestamp(Latest.StartedUtc),
            Latest.AllFindings.Count(f => f.Severity == Severity.Critical),
            Latest.AllFindings.Count(f => f.Severity == Severity.Warning),
            Latest.AllFindings.Count(f => f.Severity == Severity.Info),
            Latest.Nodes.Sum(n => n.Indeterminate.Count));

    /// <summary>Raised after every diagnosis so the SQL Server / Databases pages refresh.</summary>
    public event EventHandler? SnapshotChanged;

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private async Task DiagnoseAsync()
    {
        var site = SelectedSite ?? Sites.SelectedSite;
        if (site is null)
        {
            _status.Show(Loc.Instance["Diag_NoSite"]);
            return;
        }

        IsBusy = true;
        _cts = new CancellationTokenSource();
        try
        {
            var progress = new Progress<string>(p => Progress = Loc.Instance.Format("Diag_Progress", p));
            Latest = await _snapshots.RunAsync(site.ToProfile(), new SnapshotOptions(IncludeHeavy), progress, _cts.Token);
            await RefreshSavedAsync();
            Rebuild();
            SnapshotChanged?.Invoke(this, EventArgs.Empty);
            _status.Show(Summary);
        }
        catch (OperationCanceledException)
        {
            _status.Show(Loc.Instance["Diag_Cancelled"]);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Diagnosis of {Site} failed", site.Name);
            _status.Show(Loc.Instance.Format("Status_Error", ex.Message));
        }
        finally
        {
            Progress = string.Empty;
            IsBusy = false;
            _cts.Dispose();
            _cts = null;
        }
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    /// <summary>Reloads the list of saved diagnoses (after a diagnosis or an import, and when the page opens).</summary>
    [RelayCommand]
    public async Task RefreshSavedAsync()
    {
        var list = await _store.ListAsync(null, 100, CancellationToken.None);
        SavedSnapshots.Clear();
        foreach (var s in list)
        {
            SavedSnapshots.Add(new SnapshotChoice(s.Id, $"{s.SiteName} — {Loc.Instance.FormatTimestamp(s.StartedUtc)} (C{s.Critical} W{s.Warning} I{s.Info})"));
        }

        CompareAfter ??= SavedSnapshots.FirstOrDefault();
        CompareBefore ??= SavedSnapshots.Skip(1).FirstOrDefault();
    }

    private bool CanCompare() => CompareBefore is not null && CompareAfter is not null && CompareBefore.Id != CompareAfter.Id;

    /// <summary>Before vs after (spec §15): changed numbers, texts and findings between two saved diagnoses.</summary>
    [RelayCommand(CanExecute = nameof(CanCompare))]
    private async Task CompareSnapshotsAsync()
    {
        if (await _store.LoadAsync(CompareBefore!.Id, CancellationToken.None) is not { } before || await _store.LoadAsync(CompareAfter!.Id, CancellationToken.None) is not { } after)
        {
            _status.Show(Loc.Instance["Diag_CompareMissing"]);
            return;
        }

        var result = Core.Snapshots.SnapshotCompare.Compare(before, after);
        var language = Loc.Instance.Language;
        CompareFindings.Clear();
        foreach (var f in result.Findings)
        {
            CompareFindings.Add(new CompareFindingRow(Loc.Instance["Change_" + f.Kind], f.RuleId, f.NodeName, f.Instance ?? string.Empty, f.Title.Get(language),
                f.Before is { } b ? Loc.Instance["Severity_" + b] : string.Empty, f.After is { } a ? Loc.Instance["Severity_" + a] : string.Empty));
        }

        CompareNumbers.Clear();
        foreach (var c in result.Numbers.Take(200))
        {
            CompareNumbers.Add(new CompareValueRow(c.NodeName, c.Key, c.Before, c.After, c.Change is { } v ? v.ToString("+0%;-0%", Loc.Instance.Culture) : "—"));
        }

        CompareTexts.Clear();
        foreach (var c in result.Texts.Take(200))
        {
            CompareTexts.Add(new CompareValueRow(c.NodeName, c.Key, c.Before, c.After, string.Empty));
        }

        CompareSummary = Loc.Instance.Format("Diag_CompareSummary",
            result.Findings.Count(f => f.Kind == Core.Snapshots.FindingChangeKind.New), result.Findings.Count(f => f.Kind == Core.Snapshots.FindingChangeKind.Resolved),
            result.Numbers.Count, result.Texts.Count)
            + (result.UnmatchedNodes.Count == 0 ? string.Empty : " " + Loc.Instance.Format("Diag_CompareUnmatched", string.Join(", ", result.UnmatchedNodes)));
    }

    /// <summary>Imports one or more JSON bundles (e.g. RSM run on each node) and merges them into one site snapshot.</summary>
    [RelayCommand(CanExecute = nameof(IsIdle))]
    private async Task ImportBundlesAsync()
    {
        var files = _dialogs.PickBundles();
        if (files.Count == 0)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var result = await _import.ImportAsync(files, CancellationToken.None);
            Latest = result.Snapshot;
            await RefreshSavedAsync();
            ImportNotes.Clear();
            foreach (var warning in result.Warnings)
            {
                ImportNotes.Add(warning);
            }

            foreach (var node in result.Nodes)
            {
                ImportNotes.Add(Loc.Instance.Format("Diag_ImportNode", node.NodeName, string.Join(", ", node.FromBundles),
                    node.Gaps.Count == 0 ? Loc.Instance["Diag_ImportNoGaps"] : string.Join(", ", node.Gaps.Select(g => $"{g.Id} ({g.State})"))));
            }

            _status.Show(Loc.Instance.Format("Diag_Imported", files.Count, result.Snapshot.Nodes.Count));
        }
        catch (Exception ex) when (ex is RSM.Reporting.Bundles.BundleFormatException or IOException or UnauthorizedAccessException)
        {
            _status.Show(Loc.Instance.Format("Diag_ImportFailed", ex.Message));
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanExport() => IsIdle && Latest is not null;

    [RelayCommand(CanExecute = nameof(CanExport))]
    private Task ExportHtmlAsync() => ExportAsync(async s => (await _reports.ExportHtmlAsync(s, ReportInPersian ? UiLanguage.Fa : UiLanguage.En, Mask, CancellationToken.None), null));

    [RelayCommand(CanExecute = nameof(CanExport))]
    private Task ExportPdfAsync() => ExportAsync(async s =>
    {
        var result = await _reports.ExportPdfAsync(s, ReportInPersian ? UiLanguage.Fa : UiLanguage.En, Mask, CancellationToken.None);
        return result.PdfPath is { } pdf
            ? (pdf, null)
            : (result.HtmlPath, Loc.Instance.Format("Diag_PdfFallback", result.UnavailableReason ?? string.Empty, result.HtmlPath));
    });

    [RelayCommand(CanExecute = nameof(CanExport))]
    private Task ExportBundleAsync() => ExportAsync(async s => (await _reports.ExportBundleAsync(s, Mask, CancellationToken.None), null));

    /// <summary>Runs an export; the export returns the saved path and, optionally, its own status text.</summary>
    private async Task ExportAsync(Func<SiteSnapshot, Task<(string Path, string? Status)>> export)
    {
        if (Latest is not { } snapshot)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var (path, status) = await export(snapshot);
            LastExportPath = path;
            _status.Show(status ?? Loc.Instance.Format("Diag_Exported", path));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Export failed");
            _status.Show(Loc.Instance.Format("Status_Error", ex.Message));
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void Rebuild()
    {
        Findings.Clear();
        foreach (var row in FindingRowViewModel.From(Latest, _ => true))
        {
            Findings.Add(row);
        }

        OnPropertyChanged(nameof(Summary));
    }
}

/// <summary>One finding in a grid (text in the current UI language).</summary>
public sealed record FindingRowViewModel(
    Severity Severity,
    string SeverityText,
    string RuleId,
    string Node,
    string Instance,
    string Title,
    string Recommendation,
    string Source,
    bool IsGeneralGuidance)
{
    public string GuidanceLabel => IsGeneralGuidance ? Loc.Instance["General_Guidance_Label"] : string.Empty;

    public static IEnumerable<FindingRowViewModel> From(SiteSnapshot? snapshot, Func<Finding, bool> filter)
    {
        if (snapshot is null)
        {
            yield break;
        }

        var language = Loc.Instance.Language;
        foreach (var node in snapshot.Nodes)
        {
            foreach (var f in node.Findings.Where(filter).OrderByDescending(f => f.Severity).ThenBy(f => f.RuleId, StringComparer.Ordinal))
            {
                yield return new FindingRowViewModel(
                    f.Severity,
                    Loc.Instance["Severity_" + f.Severity.ToString()],
                    f.RuleId,
                    node.Node.Name,
                    f.Instance ?? string.Empty,
                    f.Title.Get(language),
                    f.Recommendation.Get(language),
                    f.Source.Citation,
                    f.Source.IsGeneralGuidance);
            }
        }
    }
}

/// <summary>SQL Server or Databases page: findings of one layer plus key facts from the latest snapshot.</summary>
public sealed partial class LayerFindingsViewModel : ObservableObject
{
    private readonly DiagnosisViewModel _diagnosis;
    private readonly Layer _layer;

    public LayerFindingsViewModel(string titleKey, Layer layer, DiagnosisViewModel diagnosis)
    {
        TitleKey = titleKey;
        _layer = layer;
        _diagnosis = diagnosis;
        diagnosis.SnapshotChanged += (_, _) => Rebuild();
        Loc.Instance.PropertyChanged += (_, _) => Rebuild();
        Rebuild();
    }

    public string TitleKey { get; }

    public string Title => Loc.Instance[TitleKey];

    public ObservableCollection<FindingRowViewModel> Findings { get; } = [];

    public ObservableCollection<FactRowViewModel> Facts { get; } = [];

    public bool HasSnapshot => _diagnosis.Latest is not null;

    public string Hint => _diagnosis.Latest is { } latest
        ? Loc.Instance.Format("Layer_FromSnapshot", Loc.Instance.FormatTimestamp(latest.StartedUtc))
        : Loc.Instance["Layer_NoSnapshot"];

    private void Rebuild()
    {
        Findings.Clear();
        foreach (var row in FindingRowViewModel.From(_diagnosis.Latest, f => f.Layer == _layer || (_layer == Layer.Database && f.Layer == Layer.Storage)))
        {
            Findings.Add(row);
        }

        Facts.Clear();
        foreach (var node in _diagnosis.Latest?.Nodes ?? [])
        {
            foreach (var row in _layer == Layer.Database ? FactRowViewModel.Databases(node) : FactRowViewModel.Instance(node))
            {
                Facts.Add(row);
            }
        }

        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(HasSnapshot));
        OnPropertyChanged(nameof(Hint));
    }
}

/// <summary>A read-only row of key facts (instance inventory, or one database).</summary>
public sealed record FactRowViewModel(string Node, string Name, string Value)
{
    private static readonly (string Key, string Fact)[] InstanceFacts =
    [
        ("Fact_Version", "sql.product_name"), ("Fact_Build", "sql.version"), ("Fact_Edition", "sql.edition"), ("Fact_LatestCu", "sql.build.latest"),
        ("Fact_SupportDays", "sql.support.days_left"), ("Fact_Cpus", "sql.cpu.logical"), ("Fact_MemoryMb", "sql.memory.physical_mb"),
        ("Fact_MaxMemoryMb", "sql.config.max_server_memory_mb"), ("Fact_Maxdop", "sql.config.max_degree_of_parallelism"),
        ("Fact_CostThreshold", "sql.config.cost_threshold_for_parallelism"), ("Fact_TempdbFiles", "tempdb.data_files"),
        ("Fact_Agent", "sql.service.agent.status"), ("Fact_RahkaranDbs", "rk.databases"), ("Fact_DetectedBy", "rk.detected_by"),
    ];

    public static IEnumerable<FactRowViewModel> Instance(NodeSnapshot node) =>
        InstanceFacts.Where(f => node.Facts.ContainsKey(f.Fact))
            .Select(f => new FactRowViewModel(node.Node.Name, Loc.Instance[f.Key], Text(node.Facts[f.Fact])));

    public static IEnumerable<FactRowViewModel> Databases(NodeSnapshot node)
    {
        var names = node.Facts.Keys
            .Where(k => k.StartsWith("db[", StringComparison.OrdinalIgnoreCase) && k.EndsWith("].state", StringComparison.OrdinalIgnoreCase))
            .Select(k => k[3..^"].state".Length])
            .Order(StringComparer.OrdinalIgnoreCase);
        foreach (var db in names)
        {
            object? F(string p) => node.Facts.GetValueOrDefault($"db[{db}].{p}");
            var value = Loc.Instance.Format("Fact_DbLine", Text(F("state")), Text(F("recovery_model")), Text(F("data_size_mb")),
                Hours(F("hours_since_full")), Hours(F("hours_since_log")), Hours(F("days_since_checkdb")));
            yield return new FactRowViewModel(node.Node.Name, F("is_rahkaran") is true ? $"{db} ★" : db, value);
        }
    }

    private static string Hours(object? value) => value is double d && d >= 99999 ? Loc.Instance["Fact_Never"] : Text(value);

    private static string Text(object? value) => value switch
    {
        null => "—",
        bool b => Loc.Instance[b ? "Fact_Yes" : "Fact_No"],
        double d => d.ToString("0.##", CultureInfo.InvariantCulture),
        string s => s,
        System.Collections.IEnumerable items => string.Join(", ", items.Cast<object?>().Select(Text)),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty,
    };
}

public sealed record SnapshotChoice(string Id, string Label);

public sealed record CompareFindingRow(string Change, string RuleId, string Node, string Instance, string Title, string Before, string After);

public sealed record CompareValueRow(string Node, string Key, string Before, string After, string Change);
