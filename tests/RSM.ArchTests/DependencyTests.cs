using System.Reflection;
using NetArchTest.Rules;
using RSM.Collectors.Database;
using RSM.Collectors.Redis;
using RSM.Collectors.Sql.Capabilities;
using RSM.Core.Model;
using RSM.Hosting;
using RSM.Parsers.ErrorLog;
using RSM.Reporting;
using RSM.Safety.Sql;
using RSM.Storage.Profiles;

namespace RSM.ArchTests;

/// <summary>Architecture rules from ARCHITECTURE.md §6.6, checked on the compiled assemblies.</summary>
public class DependencyTests
{
    private static readonly Assembly Core = typeof(Finding).Assembly;
    private static readonly Assembly Safety = typeof(SqlGuard).Assembly;
    private static readonly Assembly Storage = typeof(ProfileStore).Assembly;
    private static readonly Assembly Hosting = typeof(RsmHost).Assembly;
    private static readonly Assembly Parsers = typeof(ErrorLogParser).Assembly;
    private static readonly Assembly Reporting = typeof(ReportExporter).Assembly;
    private static readonly Assembly[] Collectors = [typeof(SqlCapabilityProbe).Assembly, typeof(RedisCapabilityProbe).Assembly, typeof(DatabaseCollector).Assembly];

    private static readonly string[] TransportNamespaces =
    [
        "Microsoft.Data.SqlClient", "System.Data.SqlClient", "Dapper", "System.Net.Sockets", "System.Net.Http",
        "System.Management.Automation", "System.Diagnostics.Process", "Microsoft.SqlServer.TransactSql.ScriptDom",
    ];

    [Fact]
    public void Collectors_never_touch_transports_directly()
    {
        foreach (var assembly in Collectors)
        {
            var result = Types.InAssembly(assembly).ShouldNot().HaveDependencyOnAny(TransportNamespaces).GetResult();

            result.IsSuccessful.Should().BeTrue($"{assembly.GetName().Name} must use RSM.Safety transports: {string.Join(", ", result.FailingTypeNames ?? [])}");
        }
    }

    [Fact]
    public void Storage_and_hosting_never_touch_transports_directly()
    {
        foreach (var assembly in new[] { Storage, Hosting, Parsers, Reporting })
        {
            var result = Types.InAssembly(assembly).ShouldNot().HaveDependencyOnAny(TransportNamespaces).GetResult();

            result.IsSuccessful.Should().BeTrue($"{assembly.GetName().Name}: {string.Join(", ", result.FailingTypeNames ?? [])}");
        }
    }

    [Fact]
    public void Core_depends_on_the_base_class_library_only()
    {
        var references = Core.GetReferencedAssemblies().Select(a => a.Name!).ToList();

        references.Should().OnlyContain(n => n.StartsWith("System", StringComparison.Ordinal) || n == "netstandard" || n.StartsWith("Microsoft.Extensions.", StringComparison.Ordinal) && n.EndsWith(".Abstractions", StringComparison.Ordinal),
            "RSM.Core must stay free of I/O and third-party dependencies");
    }

    [Fact]
    public void Only_safety_references_scriptdom_sqlclient_and_sockets()
    {
        var others = Collectors.Append(Core).Append(Storage).Append(Hosting).Append(Parsers).Append(Reporting);
        foreach (var assembly in others)
        {
            assembly.GetReferencedAssemblies().Select(a => a.Name).Should().NotContain(
                ["Microsoft.SqlServer.TransactSql.ScriptDom", "Microsoft.Data.SqlClient", "Dapper", "System.Net.Sockets", "System.Net.Http"],
                $"{assembly.GetName().Name} must go through RSM.Safety");
        }
    }

    [Fact]
    public void Safety_does_not_depend_on_higher_layers()
    {
        var result = Types.InAssembly(Safety).ShouldNot().HaveDependencyOnAny("RSM.Storage", "RSM.Hosting", "RSM.Collectors", "RSM.App").GetResult();

        result.IsSuccessful.Should().BeTrue();
    }

    [Fact]
    public void Reporting_works_on_snapshots_only()
    {
        var result = Types.InAssembly(Reporting).ShouldNot().HaveDependencyOnAny("RSM.Collectors", "RSM.Storage", "RSM.Hosting", "RSM.App", "RSM.Safety.Sql", "RSM.Safety.Redis").GetResult();

        result.IsSuccessful.Should().BeTrue($"reports are built from SiteSnapshot data, never from live connections: {string.Join(", ", result.FailingTypeNames ?? [])}");
    }

    [Fact]
    public void The_app_does_not_reference_collectors_directly()
    {
        var csproj = File.ReadAllText(Path.Combine(Repo.Src, "RSM.App", "RSM.App.csproj"));

        csproj.Should().NotContain("RSM.Collectors", "the UI reaches collectors only through RSM.Hosting");
        csproj.Should().NotContain("RSM.Safety.csproj");
    }
}
