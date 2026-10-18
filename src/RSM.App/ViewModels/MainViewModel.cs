using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using RSM.App.Localization;

namespace RSM.App.ViewModels;

/// <summary>One entry of the left navigation. <see cref="Phase"/> &gt; 0 means "arrives in that phase".</summary>
public sealed partial class NavItem(string titleKey, Func<object> createPage) : ObservableObject
{
    private object? _page;

    public string TitleKey { get; } = titleKey;

    public string Title => Loc.Instance[TitleKey];

    public object Page => _page ??= createPage();

    public void RefreshTitle() => OnPropertyChanged(nameof(Title));
}

public sealed partial class MainViewModel : ObservableObject
{
    public MainViewModel(SitesViewModel sites, SettingsViewModel settings, DiagnosisViewModel diagnosis, LiveViewModel live, StatusBarViewModel status, IUserDialogs dialogs,
        RSM.Safety.Sql.QueryPackCatalog queryPacks, Hosting.SlownessInvestigationService slowness, Storage.Investigations.InvestigationStore investigations,
        Hosting.ReportService reports, Storage.History.HistoryStore history, Storage.History.BaselineStore baselines, Microsoft.Extensions.Logging.ILoggerFactory loggers)
    {
        Status = status;
        Live = live;
        NavItems =
        [
            new NavItem("Nav_Overview", () => new OverviewViewModel(live)),
            new NavItem("Nav_Sql", () => new SqlPageViewModel(live, new LayerFindingsViewModel("Nav_Sql", Core.Model.Layer.Sql, diagnosis))),
            new NavItem("Nav_Databases", () => new LayerFindingsViewModel("Nav_Databases", Core.Model.Layer.Database, diagnosis)),
            new NavItem("Nav_Queries", () => new QueriesPageViewModel(live)),
            new NavItem("Nav_Windows", () => new WindowsPageViewModel(live)),
            new NavItem("Nav_Iis", () => new IisPageViewModel(live, dialogs)),
            new NavItem("Nav_Arr", () => new ArrPageViewModel(live)),
            new NavItem("Nav_Redis", () => new RedisPageViewModel(live)),
            new NavItem("Nav_Ssrs", () => new SsrsPageViewModel(live)),
            new NavItem("Nav_Workflow", () => new WorkflowPageViewModel(live)),
            new NavItem("Nav_DotNet", () => new DotNetPageViewModel(live)),
            new NavItem("Nav_Topology", () => new TopologyPageViewModel(live)),
            new NavItem("Nav_Services", () => new ServicesPageViewModel(live)),
            new NavItem("Nav_Events", () => new EventsPageViewModel(live)),
            new NavItem("Nav_Slowness", () => new SlownessPageViewModel(live, slowness, investigations, reports, status, Microsoft.Extensions.Logging.LoggerFactoryExtensions.CreateLogger<SlownessPageViewModel>(loggers))),
            new NavItem("Nav_QueryPacks", () => new QueryPacksPageViewModel(live, queryPacks)),
            new NavItem("Nav_Reports", () =>
            {
                _ = diagnosis.RefreshSavedAsync();
                return diagnosis;
            }),
            new NavItem("Nav_History", () => new HistoryPageViewModel(sites, history, baselines, status)),
            new NavItem("Nav_Sites", () => sites),
            new NavItem("Nav_Settings", () => settings),
        ];
        _selectedNav = NavItems[0];
        sites.ShowRequested += (_, _) => SelectedNav = NavItems.First(n => n.TitleKey == "Nav_Sites");
        Loc.Instance.PropertyChanged += (_, _) =>
        {
            foreach (var item in NavItems)
            {
                item.RefreshTitle();
            }
        };
    }

    public ObservableCollection<NavItem> NavItems { get; }

    public StatusBarViewModel Status { get; }

    public LiveViewModel Live { get; }

    [ObservableProperty]
    private NavItem _selectedNav;
}

public sealed partial class StatusBarViewModel : ObservableObject
{
    [ObservableProperty]
    private string _message = Loc.Instance["Status_Ready"];

    public void Show(string message) => Message = message;
}

public sealed class PlaceholderViewModel : ObservableObject
{
    public PlaceholderViewModel(string titleKey, int phase)
    {
        TitleKey = titleKey;
        Phase = phase;
        Loc.Instance.PropertyChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(Title));
            OnPropertyChanged(nameof(PhaseText));
        };
    }

    public string TitleKey { get; }

    public int Phase { get; }

    public string Title => Loc.Instance[TitleKey];

    public string PhaseText => Loc.Instance.Format("Placeholder_Phase", Phase);
}
