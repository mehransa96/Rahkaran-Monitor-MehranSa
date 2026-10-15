using Microsoft.Extensions.Logging;
using RSM.Core.Rules;
using RSM.Safety.Files;

namespace RSM.Hosting;

public sealed record RuleSet(IReadOnlyList<CompiledRule> Rules, IReadOnlyList<string> OverriddenIds, IReadOnlyList<string> Errors);

/// <summary>
/// Default rules are embedded in the exe (from the repository's rules/ folder). A rules/ folder next to RSM.exe may
/// override rules by id (team edits without a rebuild); invalid override files are reported and ignored.
/// </summary>
public sealed class RuleRepository(ILogger<RuleRepository> logger)
{
    public RuleSet Load(string? overrideDirectory)
    {
        var errors = new List<string>();
        var rules = new Dictionary<string, CompiledRule>(StringComparer.OrdinalIgnoreCase);
        var assembly = typeof(RuleRepository).Assembly;
        foreach (var name in assembly.GetManifestResourceNames().Where(n => n.StartsWith("rules/", StringComparison.Ordinal)).Order(StringComparer.Ordinal))
        {
            using var stream = assembly.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream);
            var rule = RuleLoader.Load(reader.ReadToEnd(), name); // embedded rules are tested; a failure here is a build defect
            rules[rule.Id] = rule;
        }

        var overridden = new List<string>();
        if (!string.IsNullOrEmpty(overrideDirectory))
        {
            foreach (var file in ReadOnlyFile.List(overrideDirectory, "*.json"))
            {
                try
                {
                    var rule = RuleLoader.Load(ReadOnlyFile.ReadAllTextAsync(file, CancellationToken.None).GetAwaiter().GetResult(), Path.GetFileName(file));
                    rules[rule.Id] = rule;
                    overridden.Add(rule.Id);
                }
                catch (RuleValidationException ex)
                {
                    errors.Add(ex.Message);
                    logger.LogWarning("Ignoring invalid rule override {File}: {Problem}", Path.GetFileName(file), ex.Message);
                }
            }
        }

        logger.LogInformation("Loaded {Count} rules ({Overridden} overridden, {Errors} invalid overrides)", rules.Count, overridden.Count, errors.Count);
        return new RuleSet(rules.Values.OrderBy(r => r.Id, StringComparer.Ordinal).ToList(), overridden, errors);
    }
}
