using System.Collections.Immutable;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace RSM.ArchTests;

/// <summary>
/// Runs the real BannedApiAnalyzer in-process against snippets, with the repository's BannedSymbols files.
/// Proves that a deliberately added Process.Start (and friends) is a build error, and that the banned-symbol
/// entries actually resolve (a typo in a signature would otherwise silently ban nothing).
/// </summary>
public class AnalyzerCanaryTests
{
    private static readonly Lazy<ImmutableArray<DiagnosticAnalyzer>> Analyzers = new(LoadAnalyzers);

    public static TheoryData<string, string> BannedEverywhere => new()
    {
        { "System.Diagnostics.Process.Start(\"cmd\");", "Process.Start" },
        { "System.Diagnostics.Process.Start(\"cmd\", \"/c dir\");", "Process.Start" },
        { "System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(\"cmd\"));", "ProcessStartInfo" },
        { "System.Diagnostics.Process.GetCurrentProcess().Kill();", "Kill" },
        { "Microsoft.Win32.Registry.LocalMachine.SetValue(\"x\", 1);", "SetValue" },
        { "Microsoft.Win32.Registry.LocalMachine.OpenSubKey(\"x\", true);", "OpenSubKey" },
        { "Microsoft.Win32.Registry.LocalMachine.CreateSubKey(\"x\");", "CreateSubKey" },
        { "Microsoft.Win32.Registry.LocalMachine.DeleteSubKeyTree(\"x\");", "DeleteSubKeyTree" },
        { "new System.Net.WebClient();", "WebClient" },
        { "System.Xml.Linq.XDocument.Parse(\"<a/>\");", "Parse" },
        { "System.Xml.Linq.XElement.Parse(\"<a/>\");", "Parse" },
        { "new System.Xml.XmlDocument().LoadXml(\"<a/>\");", "LoadXml" },
        { "Microsoft.Management.Infrastructure.CimSession.Create(null).InvokeMethod(\"root\", \"Win32_Process\", \"Create\", null);", "InvokeMethod" },
        { "Microsoft.Management.Infrastructure.CimSession.Create(null).DeleteInstance(null!);", "DeleteInstance" },
        { "new System.Diagnostics.PerformanceCounter(\"a\", \"b\", false).Increment();", "Increment" },
        { "new System.Diagnostics.PerformanceCounter(\"a\", \"b\", false).RawValue = 1;", "RawValue" },
        { "System.Diagnostics.PerformanceCounterCategory.Delete(\"a\");", "Delete" },
        { "System.Diagnostics.Process.GetCurrentProcess().PriorityClass = System.Diagnostics.ProcessPriorityClass.High;", "PriorityClass" },
        { "new System.Diagnostics.Eventing.Reader.EventLogSession().ClearLog(\"System\");", "ClearLog" },
        { "new System.Diagnostics.Eventing.Reader.EventLogSession().ExportLog(\"System\", System.Diagnostics.Eventing.Reader.PathType.LogName, \"*\", \"x.evtx\");", "ExportLog" },
        { "System.Diagnostics.EventLog.WriteEntry(\"a\", \"b\");", "EventLog" },
        { "new System.Diagnostics.Eventing.Reader.EventLogConfiguration(\"System\").MaximumSizeInBytes = 1;", "EventLogConfiguration" },
        { "new Microsoft.Web.Administration.ServerManager();", "ServerManager" },
        { "new Microsoft.Web.Administration.ServerManager(\"applicationHost.config\");", "ServerManager" },
        { "Microsoft.Web.Administration.ServerManager.OpenRemote(\"srv\");", "OpenRemote" },
        { "new Microsoft.Web.Administration.ServerManager(true, \"a\").CommitChanges();", "CommitChanges" },
        { "new Microsoft.Web.Administration.ServerManager(true, \"a\").ApplicationPools[0].Recycle();", "Recycle" },
        { "new Microsoft.Web.Administration.ServerManager(true, \"a\").Sites[0].Stop();", "Stop" },
        { "new System.Security.Cryptography.X509Certificates.X509Store().Add(null!);", "Add" },
        { "new System.Security.Cryptography.X509Certificates.X509Store().Remove(null!);", "Remove" },
        { "System.Security.Cryptography.X509Certificates.RSACertificateExtensions.GetRSAPrivateKey(null!);", "GetRSAPrivateKey" },
        { "_ = new System.Security.Cryptography.X509Certificates.X509Certificate2(new byte[0]).PrivateKey;", "PrivateKey" },
    };

    public static TheoryData<string, string> BannedOutsideSafety => new()
    {
        { "System.IO.File.Delete(\"x\");", "File" },
        { "System.IO.File.WriteAllText(\"x\", \"y\");", "File" },
        { "System.IO.Directory.Delete(\"x\", true);", "Directory" },
        { "new System.IO.FileStream(\"x\", System.IO.FileMode.Create);", "FileStream" },
        { "new System.IO.StreamWriter(\"x\");", "StreamWriter" },
        { "new System.IO.FileInfo(\"x\").IsReadOnly = true;", "IsReadOnly" },
        { "new System.IO.FileInfo(\"x\").LastWriteTime = System.DateTime.Now;", "LastWriteTime" },
        { "new System.IO.FileInfo(\"x\").Delete();", "Delete" },
        { "new System.Net.Http.HttpClient();", "HttpClient" },
        { "new System.Net.Sockets.TcpClient();", "TcpClient" },
        { "System.Diagnostics.Process.GetProcesses();", "Process" },
        { "new Microsoft.Data.SqlClient.SqlConnection(\"x\");", "SqlConnection" },
        { "new Microsoft.Data.SqlClient.SqlCommand(\"DELETE FROM t\");", "SqlCommand" },
        { "Dapper.SqlMapper.Execute(null!, \"DELETE FROM t\");", "SqlMapper" },
    };

    [Theory]
    [MemberData(nameof(BannedEverywhere))]
    public async Task Banned_everywhere_including_safety(string statement, string symbol)
    {
        (await Analyze(statement, includeNonSafety: true)).Should().Contain(d => d.Id == "RS0030" && d.GetMessage(null).Contains(symbol, StringComparison.Ordinal));
        (await Analyze(statement, includeNonSafety: false)).Should().Contain(d => d.Id == "RS0030", "RSM.Safety is not exempt from BannedSymbols.txt");
    }

    [Theory]
    [MemberData(nameof(BannedOutsideSafety))]
    public async Task Banned_in_every_project_except_the_safety_boundary(string statement, string symbol) =>
        (await Analyze(statement, includeNonSafety: true)).Should().Contain(d => d.Id == "RS0030" && d.GetMessage(null).Contains(symbol, StringComparison.Ordinal));

    [Theory]
    [MemberData(nameof(BannedEverywhere))]
    [MemberData(nameof(BannedOutsideSafety))]
    public void Canary_statements_compile(string statement, string symbol)
    {
        // A canary that does not compile could never produce RS0030; make such a mistake visible as such.
        var errors = CreateCompilation($"static class Canary {{ static void Run() {{ {statement} }} }}").GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => d.ToString());
        errors.Should().BeEmpty($"the canary for {symbol} must compile");
    }

    [Fact]
    public async Task Read_only_apis_stay_allowed()
    {
        var diagnostics = await Analyze(
            "using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(\"SOFTWARE\"); var s = System.IO.Path.Combine(\"a\", \"b\");",
            includeNonSafety: true);

        diagnostics.Where(d => d.Id == "RS0030").Should().BeEmpty();
    }

    [Theory]
    [InlineData("BannedSymbols.txt")]
    [InlineData("BannedSymbols.NonSafety.txt")]
    public void Every_banned_member_resolves_when_its_type_is_available(string file)
    {
        var compilation = CreateCompilation("class C {}");
        var unresolved = new List<string>();
        foreach (var id in ReadIds(file))
        {
            if (DocumentationCommentId.GetSymbolsForDeclarationId(id, compilation).Any())
            {
                continue;
            }

            // Members of types from packages that arrive in later phases (IIS, CIM, ServiceController…) cannot be
            // resolved yet; but if the declaring type IS available, the entry must resolve (catches signature typos).
            var typeId = TypeIdOf(id);
            if (typeId is not null && DocumentationCommentId.GetSymbolsForDeclarationId(typeId, compilation).Any())
            {
                unresolved.Add(id);
            }
        }

        unresolved.Should().BeEmpty($"these {file} entries do not match any API (typo in the signature?)");
    }

    [Theory]
    [InlineData("BannedSymbols.txt")]
    [InlineData("BannedSymbols.NonSafety.txt")]
    public void Property_setters_are_banned_as_properties(string file) =>
        ReadIds(file).Where(id => id.StartsWith("M:", StringComparison.Ordinal) && id.Contains(".set_", StringComparison.Ordinal))
            .Should().BeEmpty("the analyzer ignores M:…set_X entries for property assignments; use P:Type.Property");

    private static IEnumerable<string> ReadIds(string file) =>
        File.ReadAllLines(Path.Combine(Repo.Root, file))
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith("//", StringComparison.Ordinal))
            .Select(l => l.Split(';')[0]);

    private static string? TypeIdOf(string id)
    {
        if (id.StartsWith("T:", StringComparison.Ordinal))
        {
            return null;
        }

        var name = id[2..];
        var paren = name.IndexOf('(', StringComparison.Ordinal);
        if (paren >= 0)
        {
            name = name[..paren];
        }

        var lastDot = name.LastIndexOf('.');
        return lastDot > 0 ? "T:" + name[..lastDot] : null;
    }

    private static async Task<ImmutableArray<Diagnostic>> Analyze(string statement, bool includeNonSafety)
    {
        var compilation = CreateCompilation($"static class Canary {{ static void Run() {{ {statement} }} }}");
        var files = new List<AdditionalText> { new FileText(Path.Combine(Repo.Root, "BannedSymbols.txt")) };
        if (includeNonSafety)
        {
            files.Add(new FileText(Path.Combine(Repo.Root, "BannedSymbols.NonSafety.txt")));
        }

        var withAnalyzers = compilation.WithAnalyzers(Analyzers.Value, new AnalyzerOptions([.. files]));
        return await withAnalyzers.GetAnalyzerDiagnosticsAsync();
    }

    private static CSharpCompilation CreateCompilation(string source)
    {
        var trusted = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator);
        var references = trusted
            .Concat([typeof(Microsoft.Data.SqlClient.SqlConnection).Assembly.Location, LocateDapper(),
                Path.Combine(AppContext.BaseDirectory, "System.Diagnostics.PerformanceCounter.dll"),
                Path.Combine(AppContext.BaseDirectory, "System.Diagnostics.EventLog.dll"),
                Path.Combine(AppContext.BaseDirectory, "Microsoft.Web.Administration.dll")])
            .Where(p => !string.IsNullOrEmpty(p))
            .Distinct()
            .Select(p => MetadataReference.CreateFromFile(p));
        return CSharpCompilation.Create("Canary", [CSharpSyntaxTree.ParseText(source)], references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
    }

    private static string LocateDapper() => Path.Combine(AppContext.BaseDirectory, "Dapper.dll");

    private static ImmutableArray<DiagnosticAnalyzer> LoadAnalyzers()
    {
        var root = typeof(AnalyzerCanaryTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(a => a.Key == "BannedApiAnalyzersPath").Value!;
        var folder = Path.Combine(root, "analyzers", "dotnet", "cs");
        var analyzers = new List<DiagnosticAnalyzer>();
        foreach (var dll in new[] { "Microsoft.CodeAnalysis.BannedApiAnalyzers.dll", "Microsoft.CodeAnalysis.CSharp.BannedApiAnalyzers.dll" })
        {
            var assembly = Assembly.LoadFrom(Path.Combine(folder, dll));
            analyzers.AddRange(assembly.GetTypes()
                .Where(t => !t.IsAbstract && typeof(DiagnosticAnalyzer).IsAssignableFrom(t)
                            && t.GetCustomAttributes<DiagnosticAnalyzerAttribute>().Any(a => a.Languages.Contains(LanguageNames.CSharp)))
                .Select(t => (DiagnosticAnalyzer)Activator.CreateInstance(t)!));
        }

        analyzers.Should().NotBeEmpty();
        return [.. analyzers];
    }

    private sealed class FileText(string path) : AdditionalText
    {
        public override string Path { get; } = path;

        public override SourceText GetText(CancellationToken cancellationToken = default) => SourceText.From(File.ReadAllText(Path));
    }
}
