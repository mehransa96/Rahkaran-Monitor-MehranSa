using RSM.Safety.Files;

namespace RSM.Safety.Tests;

public sealed class DataFolderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rsm-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task Writes_reads_and_deletes_inside_the_root()
    {
        var folder = new DataFolder(_root, isPortable: true);

        await folder.WriteAllTextAsync(Path.Combine("sites", "a.json"), "{}", CancellationToken.None);

        folder.Exists(Path.Combine("sites", "a.json")).Should().BeTrue();
        (await folder.ReadAllTextAsync(Path.Combine("sites", "a.json"), CancellationToken.None)).Should().Be("{}");
        folder.ListFiles("sites", "*.json").Should().ContainSingle();
        folder.Delete(Path.Combine("sites", "a.json"));
        folder.Exists(Path.Combine("sites", "a.json")).Should().BeFalse();
    }

    [Theory]
    [InlineData("../escape.txt")]
    [InlineData("sites/../../escape.txt")]
    [InlineData("/etc/passwd")]
    [InlineData("C:\\Windows\\win.ini")]
    [InlineData("C:relative.txt")]
    [InlineData("")]
    public void Refuses_paths_outside_the_root(string path)
    {
        var folder = new DataFolder(_root, isPortable: true);

        FluentActions.Invoking(() => folder.Resolve(path)).Should().Throw<DataFolderEscapeException>();
    }

    [Fact]
    public async Task Server_files_are_opened_read_only_and_shared()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "u_ex.log");
        await File.WriteAllTextAsync(path, "line");

        // Another process keeps writing the log: RSM must not block it.
        await using var writer = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
        await using var reader = ReadOnlyFile.OpenRead(path);

        reader.CanWrite.Should().BeFalse();
        reader.CanRead.Should().BeTrue();
    }
}
