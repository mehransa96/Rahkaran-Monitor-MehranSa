using RSM.Parsers.Iis;
using RSM.Safety.Files;

namespace RSM.Collectors.Iis;

/// <summary>What an on-demand analysis will read, shown in the confirmation before it starts.</summary>
public sealed record LogAnalysisEstimate(string Site, IReadOnlyList<ServerFileInfo> Files, long Bytes, TimeSpan Estimated);

/// <summary>
/// On-demand W3C analysis of one site over a chosen time range (spec §10.5): a heavy operation, so the UI shows
/// <see cref="EstimateAsync"/> (files, size, time) and asks before calling <see cref="AnalyzeAsync"/>. Files are streamed
/// line by line with FileShare.ReadWrite; nothing is copied or changed.
/// </summary>
public static class IisLogAnalysis
{
    /// <summary>Conservative streaming rate used for the time estimate (admin shares are slower than local disks).</summary>
    public const double LocalBytesPerSecond = 60d * 1024 * 1024;
    public const double RemoteBytesPerSecond = 15d * 1024 * 1024;

    public static async Task<LogAnalysisEstimate?> EstimateAsync(IisNodeTransport transport, string siteName, DateTimeOffset fromUtc, CancellationToken cancellationToken)
    {
        var path = transport.Files.Map(IisNodeTransport.ApplicationHostPath);
        if (path is null)
        {
            return null;
        }

        var config = ApplicationHostConfigParser.Parse(await transport.Files.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false));
        var site = config.Sites.FirstOrDefault(s => string.Equals(s.Name, siteName, StringComparison.OrdinalIgnoreCase));
        var dir = site is null ? null : transport.Files.Map(ServerPaths.Combine(site.LogDirectory ?? @"%SystemDrive%\inetpub\logs\LogFiles", $"W3SVC{site.Id}"));
        if (dir is null)
        {
            return null;
        }

        // A daily file can hold requests from its whole UTC day: keep files written after the range started.
        var files = transport.Files.List(dir, "u_ex*.log").Where(f => f.LastWriteUtc >= fromUtc).OrderBy(f => f.LastWriteUtc).ToList();
        var bytes = files.Sum(f => f.Length);
        var rate = transport.IsLocal ? LocalBytesPerSecond : RemoteBytesPerSecond;
        return new LogAnalysisEstimate(siteName, files, bytes, TimeSpan.FromSeconds(Math.Ceiling(bytes / rate)));
    }

    /// <summary>Streams the estimated files through <see cref="W3CAnalyzer"/> for [from, to]; reports progress in bytes.</summary>
    public static async Task<W3CSummary> AnalyzeAsync(IisNodeTransport transport, LogAnalysisEstimate estimate, DateTimeOffset fromUtc, DateTimeOffset toUtc,
        IProgress<long>? progress, CancellationToken cancellationToken)
    {
        var analyzer = new W3CAnalyzer(fromUtc, toUtc);
        long done = 0;
        foreach (var file in estimate.Files)
        {
            long offset = 0;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var (text, next) = await transport.Files.ReadFromAsync(file.Path, offset, 8 * 1024 * 1024, cancellationToken).ConfigureAwait(false);
                if (next <= offset)
                {
                    break;
                }

                foreach (var line in text.Split('\n'))
                {
                    analyzer.AddLine(line.TrimEnd('\r'));
                }

                done += next - offset;
                offset = next;
                progress?.Report(done);
            }
        }

        return analyzer.Summarize();
    }
}
