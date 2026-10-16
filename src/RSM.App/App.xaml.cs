using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RSM.App.Localization;
using RSM.App.ViewModels;
using RSM.Hosting;
using RSM.Safety.Files;

namespace RSM.App;

public partial class App : Application
{
    private IHost? _host;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnDispatcherUnhandledException;

        try
        {
            await StartAsync(e.Args);
        }
        catch (Exception ex)
        {
            _host?.Services.GetService<ILogger<App>>()?.LogCritical(ex, "RSM failed to start");
            MessageBox.Show(ex.Message, "RSM", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private async Task StartAsync(string[] args)
    {
        var portable = args.Any(a => string.Equals(a, "--portable", StringComparison.OrdinalIgnoreCase));
        var dataFolder = DataFolder.CreateDefault(AppContext.BaseDirectory, portable);
        _host = RsmHost.Build(dataFolder, services =>
        {
            services.AddSingleton<IUserDialogs, WpfDialogs>();
            services.AddSingleton<RSM.Hosting.IPdfPrinter, Services.WebView2PdfPrinter>();
            services.AddSingleton<StatusBarViewModel>();
            services.AddSingleton<SitesViewModel>();
            services.AddSingleton<SettingsViewModel>();
            services.AddSingleton<DiagnosisViewModel>();
            services.AddSingleton<LiveViewModel>();
            services.AddSingleton<MainViewModel>();
            services.AddSingleton<MainWindow>();
        });

        var services = _host.Services;
        await services.GetRequiredService<SettingsViewModel>().LoadAsync();
        var window = services.GetRequiredService<MainWindow>();
        MainWindow = window;
        window.Show();
        await services.GetRequiredService<SitesViewModel>().LoadAsync();
        services.GetRequiredService<ILogger<App>>().LogInformation("RSM started (data folder {DataFolder}, portable={Portable})", dataFolder.Root, dataFolder.IsPortable);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Close the live connections first (disposes the scheduler loops and SQL/CIM sessions).
        _host?.Services.GetService<LiveMonitorService>()?.StopAsync().GetAwaiter().GetResult();
        _host?.Dispose();
        base.OnExit(e);
    }

    // RSM must never crash on a collector or UI error: log, tell the user, keep running.
    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _host?.Services.GetService<ILogger<App>>()?.LogError(e.Exception, "Unhandled UI exception");
        MessageBox.Show(e.Exception.Message, Loc.Instance["App_Title"], MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true;
    }

    private sealed class WpfDialogs : IUserDialogs
    {
        public bool Confirm(string message)
        {
            var options = Loc.Instance.FlowDirection == FlowDirection.RightToLeft ? MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign : MessageBoxOptions.None;
            return MessageBox.Show(message, Loc.Instance["App_Title"], MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No, options) == MessageBoxResult.Yes;
        }
    }
}
