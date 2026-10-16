using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RSM.Collectors.Iis;
using RSM.Collectors.Rahkaran;
using RSM.Collectors.Redis;
using RSM.Collectors.Database;
using RSM.Collectors.Sql.Capabilities;
using RSM.Collectors.Sql.Instance;
using RSM.Collectors.Sql.Live;
using RSM.Collectors.Windows;
using RSM.Core.Model;
using RSM.Storage.Snapshots;
using RSM.Safety.Files;
using RSM.Safety.Logging;
using RSM.Safety.Secrets;
using RSM.Safety.Sql;
using RSM.Storage.Profiles;
using Serilog;
using Serilog.Events;

namespace RSM.Hosting;

/// <summary>Builds the RSM service container. No internet, telemetry or auto-update services exist.</summary>
public static class RsmHost
{
    /// <summary>Every assembly that embeds catalog queries (all are guarded at startup).</summary>
    public static readonly System.Reflection.Assembly[] CatalogAssemblies = [typeof(SqlCapabilityProbe).Assembly, typeof(DatabaseCollector).Assembly];

    public static IHost Build(IDataFolder dataFolder, Action<IServiceCollection>? configure = null)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            DisableDefaults = true,
            ContentRootPath = dataFolder.Root,
        });

        builder.Services.AddSerilog(CreateLogger(dataFolder), dispose: true);
        AddRsmServices(builder.Services, dataFolder);
        configure?.Invoke(builder.Services);
        return builder.Build();
    }

    public static void AddRsmServices(IServiceCollection services, IDataFolder dataFolder)
    {
        services.AddSingleton(dataFolder);
        services.AddSingleton<ISecretProtector>(_ => OperatingSystem.IsWindows() ? new DpapiSecretProtector() : new NoPersistenceSecretProtector());
        services.AddSingleton<ProfileStore>();
        services.AddSingleton<SecretStore>();
        services.AddSingleton<RSM.Storage.Settings.AppSettingsStore>();

        // Every embedded catalog query is parsed and passed through SqlGuard once at startup.
        services.AddSingleton(_ => QueryCatalog.Load(CatalogAssemblies));
        services.AddSingleton<SafeSqlExecutor>();
        services.AddSingleton<SqlCapabilityProbe>();
        services.AddSingleton<RedisCapabilityProbe>();
        services.AddSingleton<ConnectionTester>();
        services.AddSingleton<NodeTransportOpener>();

        // Phase 1 snapshot collectors (run once per diagnosis, sequentially per node).
        services.AddSingleton<ICollector, SqlInventoryCollector>();
        services.AddSingleton<ICollector, SqlConfigurationCollector>();
        services.AddSingleton<ICollector, SqlStorageCollector>();
        services.AddSingleton<ICollector, SqlErrorLogCollector>();
        services.AddSingleton<ICollector, SqlAgentCollector>();
        services.AddSingleton<ICollector, SqlSecurityCollector>();
        services.AddSingleton<ICollector, DatabaseCollector>();

        // Phase 2 live collectors (ILiveCollector): scheduled by the live view, and run once in a diagnosis too.
        services.AddSingleton<ICollector, SqlPerfCollector>();
        services.AddSingleton<ICollector, SqlCpuCollector>();
        services.AddSingleton<ICollector, SqlActivityCollector>();
        services.AddSingleton<ICollector, SqlDeadlockCollector>();
        services.AddSingleton<ICollector, SqlQueryStatsCollector>();
        services.AddSingleton<ICollector, SqlAvailabilityCollector>();
        services.AddSingleton<ICollector, SqlSelfOverheadCollector>();
        services.AddSingleton<ICollector, QueryStoreCollector>();
        services.AddSingleton<ICollector, WindowsInventoryCollector>();
        services.AddSingleton<ICollector, WindowsPerfCollector>();
        services.AddSingleton<ICollector, WindowsProcessCollector>();

        // Phase 3: services, event logs.
        services.AddSingleton<ICollector, WindowsServicesCollector>();
        services.AddSingleton<ICollector, WindowsEventLogCollector>();
        services.AddSingleton<ICollector, IisConfigCollector>();
        services.AddSingleton<ICollector, IisRuntimeCollector>();
        services.AddSingleton<ICollector, IisLogCollector>();

        // Phase 4: Redis, SSRS, RabbitMQ, query packs, .NET runtime.
        services.AddSingleton<ICollector, RedisCollector>();
        services.AddSingleton<ICollector, RabbitMqCollector>();
        services.AddSingleton<ICollector, RSM.Collectors.Sql.Live.SqlClientsCollector>();
        services.AddSingleton<ICollector>(_ => new DotNetRuntimeCollector());
        services.AddSingleton<RSM.Core.Rules.ISiteAnalyzer, RahkaranSiteAnalyzer>();

        // §8.7 query packs next to the exe (empty by default): loaded once, every file guarded or refused with the reason.
        services.AddSingleton(_ => RSM.Safety.Sql.QueryPackCatalog.LoadAsync(Path.Combine(AppContext.BaseDirectory, "QueryPacks"), CancellationToken.None).GetAwaiter().GetResult());
        services.AddSingleton<ICollector, RSM.Collectors.Database.QueryPackCollector>();
        services.AddSingleton<ICollector, SsrsCollector>();
        services.AddSingleton<RSM.Core.Rules.ISiteAnalyzer, IisSiteAnalyzer>();
        services.AddSingleton<RSM.Core.Rules.ISiteAnalyzer, WindowsNodeAnalyzer>();
        services.AddSingleton<RuleRepository>();
        services.AddSingleton<SnapshotStore>();
        services.AddSingleton<SnapshotService>();
        services.AddSingleton<RSM.Storage.History.HistoryStore>(sp => new RSM.Storage.History.HistoryStore(sp.GetRequiredService<IDataFolder>()));
        services.AddSingleton(sp => new RSM.Storage.History.BaselineStore(sp.GetRequiredService<RSM.Storage.History.HistoryStore>(), sp.GetRequiredService<IDataFolder>()));
        services.AddSingleton(sp =>
        {
            var recorder = new RSM.Storage.History.HistoryRecorder(sp.GetRequiredService<RSM.Storage.History.HistoryStore>(), sp.GetRequiredService<RSM.Storage.History.BaselineStore>());
            var log = sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<RSM.Storage.History.HistoryRecorder>>();
            recorder.Failed += ex => Microsoft.Extensions.Logging.LoggerExtensions.LogWarning(log, "History write failed: {Error}", ex.Message);
            return recorder;
        });
        services.AddSingleton<LiveMonitorService>();
        services.AddSingleton<RSM.Storage.Investigations.InvestigationStore>();
        services.AddSingleton<SlownessInvestigationService>();
        services.AddSingleton<RSM.Reporting.ReportExporter>();
        services.AddSingleton<ReportService>();
    }

    /// <summary>Rolling file log in the data folder; every event passes the redaction enricher.</summary>
    public static Serilog.ILogger CreateLogger(IDataFolder dataFolder)
    {
        dataFolder.EnsureDirectory("logs");
        return new LoggerConfiguration()
            .MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .Enrich.With<RedactionEnricher>()
            .WriteTo.File(
                Path.Combine(dataFolder.Resolve("logs"), "rsm-.log"),
                rollingInterval: RollingInterval.Day,
                fileSizeLimitBytes: 10 * 1024 * 1024,
                rollOnFileSizeLimit: true,
                retainedFileCountLimit: 10,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext} {Message:lj}{NewLine}{Exception}")
            .CreateLogger();
    }
}
