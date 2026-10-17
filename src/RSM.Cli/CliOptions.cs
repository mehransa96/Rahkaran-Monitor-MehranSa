using System.Globalization;
using System.Text.RegularExpressions;

namespace RSM.Cli;

public enum CliCommand
{
    Help,
    Snapshot,
    Collect,
    Investigate,
    Export,
    Import,
    List,
    Compare,
}

/// <summary>Parsed command line. <see cref="Error"/> is set (and <see cref="Command"/> = Help) when the input is invalid.</summary>
public sealed partial record CliOptions(
    CliCommand Command,
    string? Site = null,
    TimeSpan? Duration = null,
    bool Heavy = false,
    string? SnapshotId = null,
    string Format = "html",
    string Language = "fa",
    bool Mask = false,
    IReadOnlyList<string>? Files = null,
    bool Portable = false,
    string? Error = null,
    string? CompareWith = null)
{
    /// <summary>Longest headless collection accepted (it still runs only while the console stays open).</summary>
    public static readonly TimeSpan MaxDuration = TimeSpan.FromDays(7);

    public const string Usage = """
        RSM.Cli — Rahkaran Server Monitor, headless and strictly read-only.

          --snapshot --site <name|id> [--heavy]           full diagnosis, saved with the app's snapshots
          --collect --site <name|id> --duration <8h>      live collection into local history while this runs
          --investigate --site <name|id> [--duration 120s] "Why is Rahkaran slow?" capture and ranked causes
          --export <snapshot-id|latest> [--format html|pdf|bundle] [--lang fa|en] [--mask]
          --import <bundle.zip> [<bundle.zip> ...]          import and merge bundles into one site snapshot
          --compare <before-id> [<after-id>|latest]         what changed between two diagnoses (before vs after)
          --list                                           list sites and recent snapshots
          --portable                                       use the data folder next to the exe

        Durations: 90s, 30m, 8h, 2h30m, 1d. RSM.Cli never installs a service or a scheduled task.
        """;

    public static CliOptions Parse(IReadOnlyList<string> args)
    {
        var commands = new List<CliCommand>();
        var o = new CliOptions(CliCommand.Help);
        var files = new List<string>();
        for (var i = 0; i < args.Count; i++)
        {
            var a = args[i];
            string? Next() => i + 1 < args.Count && !args[i + 1].StartsWith("--", StringComparison.Ordinal) ? args[++i] : null;
            CliOptions Fail(string message) => new(CliCommand.Help, Error: message);
            switch (a.ToLowerInvariant())
            {
                case "--help" or "-h" or "/?":
                    return new CliOptions(CliCommand.Help);
                case "--snapshot":
                    commands.Add(CliCommand.Snapshot);
                    break;
                case "--collect":
                    commands.Add(CliCommand.Collect);
                    break;
                case "--investigate":
                    commands.Add(CliCommand.Investigate);
                    break;
                case "--list":
                    commands.Add(CliCommand.List);
                    break;
                case "--export":
                    commands.Add(CliCommand.Export);
                    o = o with { SnapshotId = Next() ?? "latest" };
                    break;
                case "--compare":
                    commands.Add(CliCommand.Compare);
                    o = o with { SnapshotId = Next(), CompareWith = Next() ?? "latest" };
                    if (o.SnapshotId is null)
                    {
                        return Fail("--compare needs the id of the earlier diagnosis (see --list).");
                    }

                    break;
                case "--import":
                    commands.Add(CliCommand.Import);
                    while (i + 1 < args.Count && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                    {
                        files.Add(args[++i]);
                    }

                    break;
                case "--site":
                    o = o with { Site = Next() };
                    if (o.Site is null)
                    {
                        return Fail("--site needs a site name or id.");
                    }

                    break;
                case "--duration":
                    if (ParseDuration(Next()) is not { } d)
                    {
                        return Fail("--duration needs a value like 120s, 30m, 8h or 2h30m.");
                    }

                    if (d <= TimeSpan.Zero || d > MaxDuration)
                    {
                        return Fail($"--duration must be between 1 s and {MaxDuration.TotalDays} days.");
                    }

                    o = o with { Duration = d };
                    break;
                case "--heavy":
                    o = o with { Heavy = true };
                    break;
                case "--mask":
                    o = o with { Mask = true };
                    break;
                case "--portable":
                    o = o with { Portable = true };
                    break;
                case "--format":
                    var format = Next()?.ToLowerInvariant();
                    if (format is not ("html" or "pdf" or "bundle"))
                    {
                        return Fail("--format is html, pdf or bundle.");
                    }

                    o = o with { Format = format };
                    break;
                case "--lang":
                    var lang = Next()?.ToLowerInvariant();
                    if (lang is not ("fa" or "en"))
                    {
                        return Fail("--lang is fa or en.");
                    }

                    o = o with { Language = lang };
                    break;
                default:
                    return Fail($"Unknown argument: {a}");
            }
        }

        if (commands.Count > 1)
        {
            return new CliOptions(CliCommand.Help, Error: "Give one command only.");
        }

        o = o with { Command = commands.Count == 1 ? commands[0] : CliCommand.Help, Files = files };
        return o.Command switch
        {
            CliCommand.Snapshot or CliCommand.Investigate when o.Site is null => o with { Command = CliCommand.Help, Error = "--site is required." },
            CliCommand.Collect when o.Site is null || o.Duration is null => o with { Command = CliCommand.Help, Error = "--collect needs --site and --duration." },
            CliCommand.Import when files.Count == 0 => o with { Command = CliCommand.Help, Error = "--import needs at least one bundle file." },
            _ => o,
        };
    }

    /// <summary>"90s", "30m", "8h", "2h30m", "1d" or a TimeSpan ("01:30:00"); null when unreadable.</summary>
    public static TimeSpan? ParseDuration(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var m = DurationRegex().Match(text.Trim().ToLowerInvariant());
        if (m.Success && m.Length > 0)
        {
            double Part(string g) => m.Groups[g].Success ? double.Parse(m.Groups[g].Value, CultureInfo.InvariantCulture) : 0;
            return TimeSpan.FromDays(Part("d")) + TimeSpan.FromHours(Part("h")) + TimeSpan.FromMinutes(Part("m")) + TimeSpan.FromSeconds(Part("s"));
        }

        return TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out var ts) ? ts : null;
    }

    [GeneratedRegex(@"^(?=\d)(?:(?<d>\d+)d)?(?:(?<h>\d+)h)?(?:(?<m>\d+)m)?(?:(?<s>\d+)s)?$")]
    private static partial Regex DurationRegex();
}
