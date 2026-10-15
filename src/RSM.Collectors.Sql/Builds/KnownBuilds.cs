using System.Text.Json;
using System.Text.Json.Serialization;
using RSM.Safety.Sql;

namespace RSM.Collectors.Sql.Builds;

public sealed record BuildBranch(string Name, string MinBuild, string LatestBuild, string Label, string? LatestCuBuild = null);

public sealed record MajorVersionInfo(int Major, string Name, DateOnly? ExtendedSupportEnd, IReadOnlyList<BuildBranch> Branches, string? BaselineBuild = null, string? BaselineLabel = null);

public sealed record KnownBuildCatalog(DateOnly AsOf, string Source, IReadOnlyList<MajorVersionInfo> Majors);

/// <summary>Where a server build stands against the offline catalogue.</summary>
public sealed record BuildAssessment(
    string? Branch,
    string? LatestBuild,
    string? LatestLabel,
    bool IsLatest,
    bool IsBehind,
    bool BelowBaseline,
    bool NewerThanCatalog,
    bool MissingSecurityUpdateOnly,
    DateOnly? ExtendedSupportEnd,
    string MajorName,
    DateOnly CatalogAsOf);

/// <summary>CU check against <c>known-builds.json</c> (spec §7.1) — offline, no internet.</summary>
public static class KnownBuilds
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static KnownBuildCatalog Embedded { get; } = Parse(ReadEmbedded());

    public static KnownBuildCatalog Parse(string json) =>
        JsonSerializer.Deserialize<KnownBuildCatalog>(json, Options) ?? throw new InvalidDataException("known-builds.json is empty");

    public static BuildAssessment Assess(SqlServerVersion build, KnownBuildCatalog catalog)
    {
        var major = catalog.Majors.FirstOrDefault(m => m.Major == build.Major);
        if (major is null)
        {
            var newest = catalog.Majors.Max(m => m.Major);
            return new BuildAssessment(null, null, null, false, false, build.Major < catalog.Majors.Min(m => m.Major), build.Major > newest, false, null, build.ProductName, catalog.AsOf);
        }

        var branch = major.Branches
            .Where(b => build >= SqlServerVersion.Parse(b.MinBuild))
            .OrderByDescending(b => SqlServerVersion.Parse(b.MinBuild))
            .FirstOrDefault();
        var belowBaseline = major.BaselineBuild is { } baseline && build < SqlServerVersion.Parse(baseline);
        if (branch is null)
        {
            // Older than every known branch (e.g. RTM with no updates at all).
            var best = major.Branches.OrderByDescending(b => SqlServerVersion.Parse(b.LatestBuild)).First();
            return new BuildAssessment(null, best.LatestBuild, best.Label, false, true, belowBaseline || major.BaselineBuild is not null, false, false, major.ExtendedSupportEnd, major.Name, catalog.AsOf);
        }

        var latest = SqlServerVersion.Parse(branch.LatestBuild);
        var isLatest = build >= latest;
        var securityOnly = !isLatest && branch.LatestCuBuild is { } cu && build >= SqlServerVersion.Parse(cu);
        return new BuildAssessment(branch.Name, branch.LatestBuild, branch.Label, isLatest, !isLatest, belowBaseline, build > latest, securityOnly,
            major.ExtendedSupportEnd, major.Name, catalog.AsOf);
    }

    private static string ReadEmbedded()
    {
        var assembly = typeof(KnownBuilds).Assembly;
        var name = assembly.GetManifestResourceNames().Single(n => n.EndsWith("known-builds.json", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
