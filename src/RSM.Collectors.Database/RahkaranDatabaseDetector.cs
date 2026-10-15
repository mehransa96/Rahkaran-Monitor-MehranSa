namespace RSM.Collectors.Database;

public sealed record DatabaseCandidate(string Name, bool IsUserDatabase, bool IsOnline, long DataSizeMb, int Sessions);

public sealed record RahkaranDetection(IReadOnlyList<string> Databases, string Method);

/// <summary>
/// Picks the main Rahkaran database(s). The site setting always wins. Otherwise a heuristic chooses the online user
/// database with the most user sessions (ties and idle servers: the largest). The heuristic is *unverified* — no
/// Rahkaran table names are assumed — and the UI/report say so and offer the override.
/// </summary>
public static class RahkaranDatabaseDetector
{
    private static readonly string[] NotRahkaran = ["ReportServer", "distribution", "SSISDB", "DQS_", "MDS"];

    public static RahkaranDetection Detect(IReadOnlyList<DatabaseCandidate> databases, IReadOnlyList<string> configured)
    {
        if (configured.Count > 0)
        {
            var existing = configured.Where(c => databases.Any(d => string.Equals(d.Name, c, StringComparison.OrdinalIgnoreCase))).ToList();
            return new RahkaranDetection(existing, "setting");
        }

        var candidate = databases
            .Where(d => d.IsUserDatabase && d.IsOnline && !NotRahkaran.Any(p => d.Name.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(d => d.Sessions)
            .ThenByDescending(d => d.DataSizeMb)
            .FirstOrDefault();
        return candidate is null
            ? new RahkaranDetection([], "none")
            : new RahkaranDetection([candidate.Name], "auto");
    }
}
