using Microsoft.Extensions.Logging;
using RSM.Core.Rules;
using RSM.Reporting.Bundles;
using RSM.Safety.Files;
using RSM.Storage.Snapshots;

namespace RSM.Hosting;

/// <summary>
/// Imports one or more JSON bundles (read-only from wherever the user picked them), merges them into one site
/// snapshot with the current rules, and saves it with the other snapshots so it can be viewed and exported.
/// </summary>
public sealed class BundleImportService(RuleRepository ruleRepository, SnapshotService snapshots, IEnumerable<ISiteAnalyzer> analyzers, SnapshotStore store,
    ILogger<BundleImportService> logger)
{
    /// <summary>Largest bundle file accepted (zip size).</summary>
    public const long MaxBundleBytes = 128L * 1024 * 1024;

    public async Task<MergeResult> ImportAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken)
    {
        var bundles = new List<ImportedBundle>();
        foreach (var path in paths)
        {
            await using var stream = ReadOnlyFile.OpenRead(path);
            if (stream.Length > MaxBundleBytes)
            {
                throw new BundleFormatException($"{Path.GetFileName(path)} is larger than {MaxBundleBytes / 1048576} MB.");
            }

            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
            bundles.Add(BundleReader.Read(Path.GetFileName(path), buffer.ToArray()));
        }

        var engine = new RulesEngine(ruleRepository.Load(snapshots.RuleOverrideDirectory).Rules);
        var result = BundleMerger.Merge(bundles, engine, analyzers, DateTimeOffset.UtcNow);
        await store.SaveAsync(result.Snapshot, cancellationToken).ConfigureAwait(false);
        logger.LogInformation("Imported {Count} bundle(s) into snapshot {Id}: {Nodes} nodes, {Warnings} warnings", bundles.Count, result.Snapshot.Id, result.Nodes.Count, result.Warnings.Count);
        return result;
    }
}
