namespace RSM.Collectors.Database;

/// <summary>One backup set (media families of a striped backup collapsed into one row).</summary>
public sealed record BackupSet(
    int BackupSetId,
    string Database,
    char Type,            // D = full, I = differential, L = log
    DateTime Start,
    DateTime Finish,
    long SizeBytes,
    bool CopyOnly,
    bool HasChecksums,
    decimal FirstLsn,
    decimal LastLsn,
    IReadOnlyList<string> Devices);

public sealed record BackupFacts(
    bool HasFull,
    double? HoursSinceFull,
    double? HoursSinceDiff,
    double? HoursSinceLog,
    bool LogChainBroken,
    int NonCopyOnlyFullsLast24h,
    int FullBackupDevicesLast24h,
    int WithoutChecksum30d,
    bool ThroughputDropped,
    bool UsesUncDestination,
    int? TypicalFullHour);

/// <summary>Pure analysis of msdb backup history for one database (spec §8.3).</summary>
public static class BackupAnalysis
{
    public static BackupFacts Analyze(IReadOnlyList<BackupSet> sets, DateTime now, DateTime? lastFullEver, DateTime? lastDiffEver, DateTime? lastLogEver)
    {
        var fulls = sets.Where(s => s.Type == 'D').OrderBy(s => s.Finish).ToList();
        var lastFull = lastFullEver ?? fulls.LastOrDefault()?.Finish;

        return new BackupFacts(
            HasFull: lastFull is not null,
            HoursSinceFull: Hours(now, lastFull),
            HoursSinceDiff: Hours(now, lastDiffEver ?? sets.Where(s => s.Type == 'I').Select(s => (DateTime?)s.Finish).Max()),
            HoursSinceLog: Hours(now, lastLogEver ?? sets.Where(s => s.Type == 'L').Select(s => (DateTime?)s.Finish).Max()),
            LogChainBroken: IsLogChainBroken(sets),
            NonCopyOnlyFullsLast24h: fulls.Count(s => !s.CopyOnly && s.Finish >= now.AddHours(-24)),
            FullBackupDevicesLast24h: fulls.Where(s => !s.CopyOnly && s.Finish >= now.AddHours(-24)).SelectMany(s => s.Devices).Select(DeviceFolder).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            WithoutChecksum30d: sets.Count(s => !s.HasChecksums && s.Finish >= now.AddDays(-30)),
            ThroughputDropped: ThroughputDropped(fulls),
            UsesUncDestination: sets.Any(s => s.Devices.Any(d => d.StartsWith(@"\\", StringComparison.Ordinal))),
            TypicalFullHour: fulls.Count == 0 ? null : Median(fulls.TakeLast(7).Select(s => s.Finish.Hour).ToList()));
    }

    /// <summary>
    /// Since the last non-copy-only full: each log backup must start where the previous one ended
    /// (first_lsn ≤ previous last_lsn). Copy-only log backups don't break or continue the chain.
    /// </summary>
    public static bool IsLogChainBroken(IReadOnlyList<BackupSet> sets)
    {
        var lastFull = sets.Where(s => s.Type == 'D' && !s.CopyOnly).MaxBy(s => s.Finish);
        if (lastFull is null)
        {
            return false;
        }

        var logs = sets.Where(s => s.Type == 'L' && !s.CopyOnly && s.Finish > lastFull.Finish).OrderBy(s => s.FirstLsn).ToList();
        if (logs.Count == 0)
        {
            return false;
        }

        if (logs[0].FirstLsn > lastFull.LastLsn)
        {
            return true; // the first log after the full does not cover it
        }

        for (var i = 1; i < logs.Count; i++)
        {
            if (logs[i].FirstLsn > logs[i - 1].LastLsn)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Last full throughput below 50 % of the median of earlier fulls (≥ 3 samples).</summary>
    private static bool ThroughputDropped(IReadOnlyList<BackupSet> fulls)
    {
        static double Throughput(BackupSet s) => s.SizeBytes / Math.Max(1, (s.Finish - s.Start).TotalSeconds);

        var nonTrivial = fulls.Where(f => (f.Finish - f.Start).TotalSeconds >= 30).ToList();
        if (nonTrivial.Count < 4)
        {
            return false;
        }

        var earlier = nonTrivial.Take(nonTrivial.Count - 1).Select(Throughput).Order().ToList();
        var median = earlier[earlier.Count / 2];
        return Throughput(nonTrivial[^1]) < median * 0.5;
    }

    private static string DeviceFolder(string device)
    {
        var slash = Math.Max(device.LastIndexOf('\\'), device.LastIndexOf('/'));
        return slash > 0 ? device[..slash] : device;
    }

    private static double? Hours(DateTime now, DateTime? at) => at is { } t ? (now - t).TotalHours : null;

    private static int Median(List<int> values)
    {
        values.Sort();
        return values[values.Count / 2];
    }
}
