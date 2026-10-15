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

    /// <summary>
    /// Reads at most the last <paramref name="maxBytes"/> of a text file (e.g. a large ERRORLOG), honouring the
    /// file's BOM (ERRORLOG is UTF-16 LE). Returns the text and whether it was truncated.
    /// </summary>
    public static async Task<(string Text, bool Truncated)> ReadTailTextAsync(string path, long maxBytes, CancellationToken cancellationToken)
    {
        await using var stream = OpenRead(path, sequential: false);
        var bom = new byte[3];
        var read = await stream.ReadAsync(bom, cancellationToken).ConfigureAwait(false);
        Encoding encoding = read >= 2 && bom[0] == 0xFF && bom[1] == 0xFE ? Encoding.Unicode
            : read >= 2 && bom[0] == 0xFE && bom[1] == 0xFF ? Encoding.BigEndianUnicode
            : Encoding.UTF8;
        var truncated = stream.Length > maxBytes;
        var start = truncated ? stream.Length - maxBytes : 0;
        if (encoding != Encoding.UTF8 && start % 2 != 0)
        {
            start++; // keep UTF-16 code units aligned
        }

        stream.Seek(start, SeekOrigin.Begin);
        using var reader = new StreamReader(stream, encoding, detectEncodingFromByteOrderMarks: !truncated);
        return (await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false), truncated);
    }

    public static bool Exists(string path) => File.Exists(path);

    public static IReadOnlyList<string> List(string directory, string searchPattern) =>
        Directory.Exists(directory) ? Directory.EnumerateFiles(directory, searchPattern).Order(StringComparer.OrdinalIgnoreCase).ToList() : [];
}
