using System.Text;

namespace RSM.Safety.Files;

/// <summary>
/// Read access to files that belong to a monitored server (IIS logs, ERRORLOG, web.config, rules shipped next to
/// the exe). Opened with FileAccess.Read and FileShare.ReadWrite|Delete so RSM never locks or blocks the owner.
/// </summary>
public static class ReadOnlyFile
{
    public static Stream OpenRead(string path, bool sequential = true) =>
        new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize: 64 * 1024,
            sequential ? FileOptions.SequentialScan | FileOptions.Asynchronous : FileOptions.Asynchronous);

    public static async Task<string> ReadAllTextAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = OpenRead(path);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
    }

    public static bool Exists(string path) => File.Exists(path);

    public static IReadOnlyList<string> List(string directory, string searchPattern) =>
        Directory.Exists(directory) ? Directory.EnumerateFiles(directory, searchPattern).Order(StringComparer.OrdinalIgnoreCase).ToList() : [];
}
