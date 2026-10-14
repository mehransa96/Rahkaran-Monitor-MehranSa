using System.Text;

namespace RSM.Safety.Files;

/// <summary>
/// The only component in RSM that creates, writes or deletes files. Every path is relative to the RSM data folder
/// and is resolved and checked so it can never escape it (no "..", no absolute paths, no other drives).
/// </summary>
public interface IDataFolder
{
    /// <summary>Absolute root of the data folder.</summary>
    string Root { get; }

    bool IsPortable { get; }

    /// <summary>Resolves a relative path inside the data folder (throws if it would escape it).</summary>
    string Resolve(string relativePath);

    bool Exists(string relativePath);

    IReadOnlyList<string> ListFiles(string relativeDirectory, string searchPattern);

    Task WriteAllTextAsync(string relativePath, string content, CancellationToken cancellationToken);

    Task WriteAllBytesAsync(string relativePath, byte[] content, CancellationToken cancellationToken);

    Task<string> ReadAllTextAsync(string relativePath, CancellationToken cancellationToken);

    Task<byte[]> ReadAllBytesAsync(string relativePath, CancellationToken cancellationToken);

    void Delete(string relativePath);

    void EnsureDirectory(string relativeDirectory);
}

public sealed class DataFolderEscapeException(string path) : Exception($"Path '{path}' is outside the RSM data folder.");

public sealed class DataFolder : IDataFolder
{
    private readonly string _rootWithSeparator;

    public DataFolder(string root, bool isPortable)
    {
        Root = Path.GetFullPath(root);
        IsPortable = isPortable;
        _rootWithSeparator = Root.EndsWith(Path.DirectorySeparatorChar) ? Root : Root + Path.DirectorySeparatorChar;
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public bool IsPortable { get; }

    /// <summary>
    /// Portable mode when a <c>data</c> folder exists next to the executable (or <paramref name="forcePortable"/>);
    /// otherwise <c>%LOCALAPPDATA%\RSM</c>.
    /// </summary>
    public static DataFolder CreateDefault(string executableDirectory, bool forcePortable = false)
    {
        var portable = Path.Combine(executableDirectory, "data");
        if (forcePortable || Directory.Exists(portable))
        {
            return new DataFolder(portable, isPortable: true);
        }

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create);
        return new DataFolder(Path.Combine(local, "RSM"), isPortable: false);
    }

    public string Resolve(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath) || relativePath.Contains(':'))
        {
            throw new DataFolderEscapeException(relativePath);
        }

        var full = Path.GetFullPath(Path.Combine(Root, relativePath));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!full.StartsWith(_rootWithSeparator, comparison))
        {
            throw new DataFolderEscapeException(relativePath);
        }

        return full;
    }

    public bool Exists(string relativePath) => File.Exists(Resolve(relativePath));

    public IReadOnlyList<string> ListFiles(string relativeDirectory, string searchPattern)
    {
        var dir = Resolve(relativeDirectory);
        return Directory.Exists(dir)
            ? Directory.EnumerateFiles(dir, searchPattern).Select(f => Path.GetRelativePath(Root, f)).Order(StringComparer.Ordinal).ToList()
            : [];
    }

    public async Task WriteAllTextAsync(string relativePath, string content, CancellationToken cancellationToken) =>
        await WriteAllBytesAsync(relativePath, Encoding.UTF8.GetBytes(content), cancellationToken).ConfigureAwait(false);

    public async Task WriteAllBytesAsync(string relativePath, byte[] content, CancellationToken cancellationToken)
    {
        var target = Resolve(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);

        // Write to a temp file in the same folder, then replace: a crash never leaves a half-written profile.
        var temp = target + ".tmp";
        await File.WriteAllBytesAsync(temp, content, cancellationToken).ConfigureAwait(false);
        File.Move(temp, target, overwrite: true);
    }

    public Task<string> ReadAllTextAsync(string relativePath, CancellationToken cancellationToken) =>
        File.ReadAllTextAsync(Resolve(relativePath), Encoding.UTF8, cancellationToken);

    public Task<byte[]> ReadAllBytesAsync(string relativePath, CancellationToken cancellationToken) =>
        File.ReadAllBytesAsync(Resolve(relativePath), cancellationToken);

    public void Delete(string relativePath)
    {
        var target = Resolve(relativePath);
        if (File.Exists(target))
        {
            File.Delete(target);
        }
    }

    public void EnsureDirectory(string relativeDirectory) => Directory.CreateDirectory(Resolve(relativeDirectory));
}
