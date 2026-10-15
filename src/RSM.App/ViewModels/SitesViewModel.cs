using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using RSM.App.Localization;
using RSM.Core.Capabilities;
using RSM.Core.Model;
using RSM.Core.Profiles;
using RSM.Hosting;
using RSM.Storage.Profiles;

namespace RSM.App.ViewModels;

/// <summary>Asks the user to confirm destructive actions on RSM's own data (never on servers).</summary>
public interface IUserDialogs
{
    bool Confirm(string message);
}

/// <summary>Site list + node editor + "Test connection" (Capability Matrix).</summary>
public sealed partial class SitesViewModel : ObservableObject
{
    private readonly ProfileStore _profiles;
    private readonly SecretStore _secrets;
    private readonly ConnectionTester _tester;
    private readonly StatusBarViewModel _status;
    private readonly IUserDialogs _dialogs;
    private readonly ILogger<SitesViewModel> _logger;

    public SitesViewModel(ProfileStore profiles, SecretStore secrets, ConnectionTester tester, StatusBarViewModel status, IUserDialogs dialogs, ILogger<SitesViewModel> logger)
    {
        _profiles = profiles;
        _secrets = secrets;
        _tester = tester;
        _status = status;
        _dialogs = dialogs;
        _logger = logger;
        CanRememberPasswords = secrets.CanPersist;
        Loc.Instance.PropertyChanged += (_, _) =>
        {
            foreach (var node in Sites.SelectMany(s => s.Nodes))
            {
                node.RefreshLocalized();
            }
        };
    }

    public ObservableCollection<SiteItemViewModel> Sites { get; } = [];

    public bool CanRememberPasswords { get; }

    public IReadOnlyList<SqlEncryptMode> EncryptModes { get; } = Enum.GetValues<SqlEncryptMode>();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedSite))]
    private SiteItemViewModel? _selectedSite;

    public bool HasSelectedSite => SelectedSite is not null;

    public async Task LoadAsync()
    {
        try
        {
            Sites.Clear();
            foreach (var site in await _profiles.LoadAllAsync(CancellationToken.None))
            {
                var item = SiteItemViewModel.From(site);
                foreach (var node in item.Nodes)
                {
                    node.HasStoredSqlPassword = await _secrets.GetAsync(site.Id, node.Id, SecretKind.SqlPassword, CancellationToken.None) is not null;
                    node.HasStoredRedisPassword = await _secrets.GetAsync(site.Id, node.Id, SecretKind.RedisPassword, CancellationToken.None) is not null;
                }

                Sites.Add(item);
            }

            SelectedSite = Sites.FirstOrDefault();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Loading site profiles failed");
            _status.Show(Loc.Instance.Format("Status_Error", ex.Message));
        }
    }

    [RelayCommand]
    private void NewSite()
    {
        var site = new SiteItemViewModel(SiteProfile.NewId()) { Name = Loc.Instance["Sites_DefaultName"] };
        site.AddNode();
        Sites.Add(site);
        SelectedSite = site;
    }

    [RelayCommand]
    private async Task SaveSiteAsync()
    {
        if (SelectedSite is not { } site)
        {
            return;
        }

        try
        {
            await _profiles.SaveAsync(site.ToProfile(), CancellationToken.None);
            foreach (var node in site.Nodes)
            {
                await SaveSecretsAsync(site, node);
            }

            _status.Show(Loc.Instance["Status_Saved"]);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Saving site {SiteName} failed", site.Name);
            _status.Show(Loc.Instance.Format("Status_Error", ex.Message));
        }
    }

    [RelayCommand]
    private void DeleteSite()
    {
        if (SelectedSite is not { } site || !_dialogs.Confirm(Loc.Instance.Format("Confirm_DeleteSite", site.Name)))
        {
            return;
        }

        _profiles.Delete(site.Id);
        _secrets.DeleteSite(site.Id);
        Sites.Remove(site);
        SelectedSite = Sites.FirstOrDefault();
    }

    [RelayCommand]
    private void AddNode() => SelectedSite?.AddNode();

    [RelayCommand]
    private void RemoveNode()
    {
        if (SelectedSite is { SelectedNode: { } node } site)
        {
            site.Nodes.Remove(node);
            site.SelectedNode = site.Nodes.FirstOrDefault();
        }
    }

    [RelayCommand]
    private async Task TestConnectionAsync()
    {
        if (SelectedSite is not { SelectedNode: { } node } site)
        {
            return;
        }

        node.IsTesting = true;
        _status.Show(Loc.Instance["Testing"]);
        try
        {
            // Secrets typed in this session must be available to the probe even before "Save".
            await SaveSecretsAsync(site, node);
            var matrix = await _tester.TestAsync(site.ToProfile(), node.ToProfile(), CancellationToken.None);
            node.ShowMatrix(matrix);
            _status.Show(Loc.Instance.Format("Status_TestDone", matrix.Results.Count(r => r.IsAvailable), matrix.Results.Count(r => r.State != CapabilityState.NotApplicable)));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Test connection failed for node {NodeName}", node.Name);
            _status.Show(Loc.Instance.Format("Status_Error", ex.Message));
        }
        finally
        {
            node.IsTesting = false;
        }
    }

    private async Task SaveSecretsAsync(SiteItemViewModel site, NodeEditorViewModel node)
    {
        if (!string.IsNullOrEmpty(node.SqlPassword))
        {
            await _secrets.SetAsync(site.Id, node.Id, SecretKind.SqlPassword, node.SqlPassword, node.RememberPasswords, CancellationToken.None);
            node.HasStoredSqlPassword = true;
        }

        if (!string.IsNullOrEmpty(node.RedisPassword))
        {
            await _secrets.SetAsync(site.Id, node.Id, SecretKind.RedisPassword, node.RedisPassword, node.RememberPasswords, CancellationToken.None);
            node.HasStoredRedisPassword = true;
        }
    }
}

public sealed partial class SiteItemViewModel(string id) : ObservableObject
{
    public string Id { get; } = id;

    public ObservableCollection<NodeEditorViewModel> Nodes { get; } = [];

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string? _notes;

    [ObservableProperty]
    private NodeEditorViewModel? _selectedNode;

    /// <summary>Rahkaran database names, comma separated (empty = auto-detect the busiest user database).</summary>
    [ObservableProperty]
    private string _rahkaranDatabases = string.Empty;

    /// <summary>Name patterns (regex) of ستاد فنی maintenance-tool jobs, one per line — product specific, unverified.</summary>
    [ObservableProperty]
    private string _stadFanniJobPatterns = string.Empty;

    private SiteSettings _settings = new();

    public static SiteItemViewModel From(SiteProfile profile)
    {
        var site = new SiteItemViewModel(profile.Id)
        {
            Name = profile.Name,
            Notes = profile.Notes,
            _settings = profile.Settings,
            RahkaranDatabases = string.Join(", ", profile.Settings.RahkaranDatabases),
            StadFanniJobPatterns = string.Join(Environment.NewLine, profile.Settings.StadFanniToolJobPatterns),
        };
        foreach (var node in profile.Nodes)
        {
            site.Nodes.Add(NodeEditorViewModel.From(node));
        }

        site.SelectedNode = site.Nodes.FirstOrDefault();
        return site;
    }

    public void AddNode()
    {
        var node = new NodeEditorViewModel(SiteProfile.NewId()) { Name = $"{Loc.Instance["Node_DefaultName"]} {Nodes.Count + 1}" };
        Nodes.Add(node);
        SelectedNode = node;
    }

    public SiteProfile ToProfile() => new()
    {
        Id = Id,
        Name = string.IsNullOrWhiteSpace(Name) ? Id : Name.Trim(),
        Notes = Notes,
        Settings = _settings with
        {
            RahkaranDatabases = Split(RahkaranDatabases, ','),
            StadFanniToolJobPatterns = Split(StadFanniJobPatterns, '\n').Where(IsValidPattern).ToList(),
        },
        Nodes = Nodes.Select(n => n.ToProfile()).ToList(),
    };

    private static List<string> Split(string? text, char separator) =>
        (text ?? string.Empty).Split(separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    private static bool IsValidPattern(string pattern)
    {
        try
        {
            _ = new System.Text.RegularExpressions.Regex(pattern, System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromMilliseconds(100));
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    public override string ToString() => Name;
}

public sealed partial class NodeEditorViewModel(string id) : ObservableObject
{
    public string Id { get; } = id;

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _host = string.Empty;

    [ObservableProperty]
    private bool _roleData;

    [ObservableProperty]
    private bool _roleReport;

    [ObservableProperty]
    private bool _roleApp;

    [ObservableProperty]
    private bool _roleArr;

    [ObservableProperty]
    private bool _roleRedis;

    [ObservableProperty]
    private bool _roleLock;

    [ObservableProperty]
    private bool _roleWorkflow;

    [ObservableProperty]
    private bool _sqlEnabled;

    [ObservableProperty]
    private string _sqlServer = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SqlAuthIsWindows))]
    private bool _sqlAuthIsSql = true;

    [ObservableProperty]
    private string? _sqlUser;

    [ObservableProperty]
    private SqlEncryptMode _sqlEncrypt = SqlEncryptMode.Mandatory;

    [ObservableProperty]
    private bool _trustServerCertificate;

    [ObservableProperty]
    private bool _rememberPasswords = true;

    [ObservableProperty]
    private bool _hasStoredSqlPassword;

    [ObservableProperty]
    private bool _redisEnabled;

    [ObservableProperty]
    private string _redisHost = string.Empty;

    [ObservableProperty]
    private int _redisPort = 6379;

    [ObservableProperty]
    private bool _redisHasPassword;

    [ObservableProperty]
    private bool _hasStoredRedisPassword;

    [ObservableProperty]
    private bool _isTesting;

    [ObservableProperty]
    private string? _probedAt;

    /// <summary>Set from the PasswordBox (not data-bound); kept only in memory.</summary>
    public string? SqlPassword { get; set; }

    public string? RedisPassword { get; set; }

    public bool SqlAuthIsWindows
    {
        get => !SqlAuthIsSql;
        set => SqlAuthIsSql = !value;
    }

    public ObservableCollection<CapabilityRowViewModel> Capabilities { get; } = [];

    public ObservableCollection<string> Banners { get; } = [];

    private CapabilityMatrix? _matrix;

    public static NodeEditorViewModel From(NodeProfile p) => new(p.Id)
    {
        Name = p.Name,
        Host = p.Host,
        RoleData = p.Roles.HasFlag(NodeRoles.Data),
        RoleReport = p.Roles.HasFlag(NodeRoles.Report),
        RoleApp = p.Roles.HasFlag(NodeRoles.App),
        RoleArr = p.Roles.HasFlag(NodeRoles.Arr),
        RoleRedis = p.Roles.HasFlag(NodeRoles.Redis),
        RoleLock = p.Roles.HasFlag(NodeRoles.Lock),
        RoleWorkflow = p.Roles.HasFlag(NodeRoles.Workflow),
        SqlEnabled = p.Sql is not null,
        SqlServer = p.Sql?.Server ?? string.Empty,
        SqlAuthIsSql = p.Sql?.Auth != SqlAuthKind.Windows,
        SqlUser = p.Sql?.UserName,
        SqlEncrypt = p.Sql?.Encrypt ?? SqlEncryptMode.Mandatory,
        TrustServerCertificate = p.Sql?.TrustServerCertificate ?? false,
        RememberPasswords = p.Sql?.RememberPassword ?? p.Redis?.RememberPassword ?? true,
        RedisEnabled = p.Redis is not null,
        RedisHost = p.Redis?.Host ?? string.Empty,
        RedisPort = p.Redis?.Port ?? 6379,
        RedisHasPassword = p.Redis?.HasPassword ?? false,
    };

    public NodeProfile ToProfile()
    {
        var roles = NodeRoles.None;
        roles |= RoleData ? NodeRoles.Data : 0;
        roles |= RoleReport ? NodeRoles.Report : 0;
        roles |= RoleApp ? NodeRoles.App : 0;
        roles |= RoleArr ? NodeRoles.Arr : 0;
        roles |= RoleRedis ? NodeRoles.Redis : 0;
        roles |= RoleLock ? NodeRoles.Lock : 0;
        roles |= RoleWorkflow ? NodeRoles.Workflow : 0;
        var host = Host.Trim();
        return new NodeProfile
        {
            Id = Id,
            Name = string.IsNullOrWhiteSpace(Name) ? host : Name.Trim(),
            Host = host,
            Roles = roles,
            Sql = SqlEnabled
                ? new SqlEndpoint
                {
                    Server = string.IsNullOrWhiteSpace(SqlServer) ? host : SqlServer.Trim(),
                    Auth = SqlAuthIsSql ? SqlAuthKind.Sql : SqlAuthKind.Windows,
                    UserName = SqlAuthIsSql ? SqlUser?.Trim() : null,
                    Encrypt = SqlEncrypt,
                    TrustServerCertificate = TrustServerCertificate,
                    RememberPassword = RememberPasswords,
                }
                : null,
            Redis = RedisEnabled
                ? new RedisEndpoint
                {
                    Host = string.IsNullOrWhiteSpace(RedisHost) ? host : RedisHost.Trim(),
                    Port = RedisPort is > 0 and < 65536 ? RedisPort : 6379,
                    HasPassword = RedisHasPassword,
                    RememberPassword = RememberPasswords,
                }
                : null,
        };
    }

    public void ShowMatrix(CapabilityMatrix matrix)
    {
        _matrix = matrix;
        RefreshLocalized();
    }

    public void RefreshLocalized()
    {
        if (_matrix is null)
        {
            return;
        }

        Capabilities.Clear();
        var order = CapabilityCatalog.All.Select((d, i) => (d.Id, i)).ToDictionary(x => x.Id, x => x.i);
        foreach (var result in _matrix.Results.OrderBy(r => order.GetValueOrDefault(r.Id, int.MaxValue)))
        {
            Capabilities.Add(new CapabilityRowViewModel(CapabilityCatalog.Get(result.Id), result));
        }

        Banners.Clear();
        foreach (var banner in _matrix.Banners)
        {
            Banners.Add(banner.Get(Loc.Instance.Language));
        }

        ProbedAt = Loc.Instance.Format("Matrix_ProbedAt", Loc.Instance.FormatTimestamp(_matrix.ProbedAtUtc));
    }

    public override string ToString() => Name;
}
