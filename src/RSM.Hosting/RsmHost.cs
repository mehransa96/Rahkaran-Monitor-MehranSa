using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RSM.Collectors.Redis;
using RSM.Collectors.Sql.Capabilities;
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
        services.AddSingleton(_ => QueryCatalog.Load(typeof(SqlCapabilityProbe).Assembly));
        services.AddSingleton<SafeSqlExecutor>();
        services.AddSingleton<SqlCapabilityProbe>();
        services.AddSingleton<RedisCapabilityProbe>();
        services.AddSingleton<ConnectionTester>();
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
