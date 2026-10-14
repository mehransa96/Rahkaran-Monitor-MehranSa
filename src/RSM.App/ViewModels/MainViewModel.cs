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
    public MainViewModel(SitesViewModel sites, SettingsViewModel settings, StatusBarViewModel status)
    {
        Status = status;
        NavItems =
        [
            new NavItem("Nav_Overview", () => new OverviewViewModel()),
            new NavItem("Nav_Sql", () => new PlaceholderViewModel("Nav_Sql", 1)),
            new NavItem("Nav_Databases", () => new PlaceholderViewModel("Nav_Databases", 1)),
            new NavItem("Nav_Queries", () => new PlaceholderViewModel("Nav_Queries", 2)),
            new NavItem("Nav_Windows", () => new PlaceholderViewModel("Nav_Windows", 2)),
            new NavItem("Nav_Iis", () => new PlaceholderViewModel("Nav_Iis", 3)),
            new NavItem("Nav_Arr", () => new PlaceholderViewModel("Nav_Arr", 3)),
            new NavItem("Nav_Redis", () => new PlaceholderViewModel("Nav_Redis", 4)),
            new NavItem("Nav_Ssrs", () => new PlaceholderViewModel("Nav_Ssrs", 4)),
            new NavItem("Nav_Services", () => new PlaceholderViewModel("Nav_Services", 3)),
            new NavItem("Nav_Events", () => new PlaceholderViewModel("Nav_Events", 3)),
            new NavItem("Nav_Slowness", () => new PlaceholderViewModel("Nav_Slowness", 5)),
            new NavItem("Nav_Reports", () => new PlaceholderViewModel("Nav_Reports", 1)),
            new NavItem("Nav_History", () => new PlaceholderViewModel("Nav_History", 5)),
            new NavItem("Nav_Sites", () => sites),
            new NavItem("Nav_Settings", () => settings),
        ];
        _selectedNav = NavItems[0];
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

    [ObservableProperty]
    private NavItem _selectedNav;
}

public sealed partial class StatusBarViewModel : ObservableObject
{
    [ObservableProperty]
    private string _message = Loc.Instance["Status_Ready"];

    public void Show(string message) => Message = message;
}

public sealed class OverviewViewModel;

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
