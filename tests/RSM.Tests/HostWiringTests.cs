using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using RSM.Hosting;
using RSM.Safety.Files;
using RSM.Storage.History;

namespace RSM.Tests;

/// <summary>The composition root resolves every P5 service, and the live view really records into history.</summary>
public sealed class HostWiringTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rsm-host-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task Container_is_valid_and_the_live_view_gets_the_history_recorder()
    {
        var services = new ServiceCollection().AddLogging();
        RsmHost.AddRsmServices(services, new DataFolder(_root, isPortable: true));
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

        provider.GetRequiredService<SlownessInvestigationService>().Definitions.Causes.Should().NotBeEmpty();
        provider.GetRequiredService<BundleImportService>().Should().NotBeNull();
        provider.GetRequiredService<ReportService>().Should().NotBeNull();
        provider.GetRequiredService<BaselineStore>().Should().NotBeNull();

        var live = provider.GetRequiredService<LiveMonitorService>();
        var recorder = provider.GetRequiredService<HistoryRecorder>();
        typeof(LiveMonitorService).GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            .Where(f => f.FieldType == typeof(HistoryRecorder))
            .Select(f => f.GetValue(live))
            .Should().ContainSingle().Which.Should().BeSameAs(recorder, "samples of the live view must reach the history store");
    }
}
