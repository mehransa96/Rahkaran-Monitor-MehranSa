using System.Text.Json;
using RSM.Core.Model;
using RSM.Core.Rules;

namespace RSM.Tests;

/// <summary>Every shipped rule (rules/*.json) is valid and behaves exactly as its own examples say.</summary>
public class RuleCatalogTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 10, 0, 0, TimeSpan.Zero);

    private static string RulesDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "RSM.sln")))
        {
            dir = dir.Parent;
        }

        return Path.Combine(dir!.FullName, "rules");
    }

    public static TheoryData<string> RuleFiles()
    {
        var data = new TheoryData<string>();
        foreach (var file in Directory.EnumerateFiles(RulesDir(), "*.json").Order(StringComparer.Ordinal))
        {
            data.Add(Path.GetFileName(file));
        }

        return data;
    }

    private static CompiledRule Load(string file) => RuleLoader.Load(File.ReadAllText(Path.Combine(RulesDir(), file)), file);

    [Theory]
    [MemberData(nameof(RuleFiles))]
    public void Rule_is_valid_and_named_after_its_id(string file)
    {
        var rule = Load(file);

        Path.GetFileNameWithoutExtension(file).Should().Be(rule.Id);
        rule.Definition.Examples.Should().Contain(e => e.Expect != null, "every rule needs an example that fires");
        rule.Definition.Examples.Should().Contain(e => e.Expect == null, "every rule needs an example that stays quiet");
        if (rule.Definition.Source.Kind == SourceKind.General)
        {
            rule.Definition.Source.Citation.Should().StartWith("General guidance");
        }
    }

    [Theory]
    [MemberData(nameof(RuleFiles))]
    public void Baseline_actual_value_is_a_fact_a_collector_emits(string file)
    {
        var baseline = Load(file).Definition.Baseline;
        if (baseline is null)
        {
            return;
        }

        var sources = string.Concat(Directory.EnumerateFiles(Path.Combine(RulesDir(), "..", "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => f.Contains("RSM.Collectors.", StringComparison.Ordinal))
            .Select(File.ReadAllText));
        var key = baseline.Actual;
        var needle = key switch
        {
            _ when key.StartsWith("sql.config.", StringComparison.Ordinal) => "$\"sql.config.{key}\"",   // one fact per sys.configurations row
            _ when key.StartsWith("db.", StringComparison.Ordinal) => $".{key[3..]}\"",                 // $"{key}.recovery_model"
            _ when key.StartsWith("volume.", StringComparison.Ordinal) => $".{key[7..]}\"",            // $"{key}.block_size_kb"
            _ => $"\"{key}\"",
        };
        sources.Should().Contain(needle, $"baseline.actual '{key}' of {file} must be produced by a collector");
    }

    [Fact]
    public void Compliance_table_covers_the_spec_configuration_items()
    {
        // docs/SPEC.md §7.2: the configuration-compliance checks the report must list.
        var items = Directory.EnumerateFiles(RulesDir(), "*.json")
            .Select(f => Load(Path.GetFileName(f)).Definition.Baseline?.Actual)
            .OfType<string>()
            .ToHashSet(StringComparer.Ordinal);
        foreach (var expected in new[]
        {
            "sql.config.max_degree_of_parallelism", "sql.config.cost_threshold_for_parallelism", "sql.config.optimize_for_ad_hoc_workloads",
            "sql.config.backup_compression_default", "sql.config.priority_boost", "sql.config.max_worker_threads", "sql.config.lightweight_pooling",
            "sql.config.xp_cmdshell", "sql.config.ole_automation_procedures", "sql.config.clr_enabled", "sql.config.remote_admin_connections",
            "sql.config.cross_db_ownership_chaining", "sql.config.max_server_memory_pct", "sql.config.min_server_memory_pct",
        })
        {
            items.Should().Contain(expected);
        }
    }

    [Theory]
    [MemberData(nameof(RuleFiles))]
    public void Rule_examples_behave_as_declared(string file)
    {
        var rule = Load(file);
        var engine = new RulesEngine([rule]);

        foreach (var (example, index) in rule.Definition.Examples.Select((e, i) => (e, i)))
        {
            var facts = new FactStore();
            foreach (var (key, value) in example.Facts)
            {
                facts.Set(key, FromJson(value));
            }

            var roles = example.Roles is { Count: > 0 } r ? r.Aggregate(NodeRoles.None, (a, b) => a | b) : NodeRoles.Data;
            var result = engine.Evaluate("n1", roles, facts, Now);

            result.Errors.Should().BeEmpty($"{file} example {index}");
            result.Indeterminate.Should().BeEmpty($"{file} example {index} must reference only facts it provides");
            if (example.Expect is { } expected)
            {
                result.Findings.Should().ContainSingle($"{file} example {index} should fire").Which.Severity.Should().Be(expected, $"{file} example {index}");
                var finding = result.Findings[0];
                finding.Title.Fa.Should().NotContain("{", $"{file}: unresolved placeholder in fa title");
                finding.Title.En.Should().NotContain("{", $"{file}: unresolved placeholder in en title");
            }
            else
            {
                result.Findings.Should().BeEmpty($"{file} example {index} should stay quiet");
            }
        }
    }

    [Fact]
    public void Rule_ids_are_unique_and_documented_in_RULES_md()
    {
        var ids = Directory.EnumerateFiles(RulesDir(), "*.json").Select(f => Load(Path.GetFileName(f)).Id).ToList();
        var doc = File.ReadAllText(Path.Combine(RulesDir(), "..", "RULES.md"));

        ids.Should().OnlyHaveUniqueItems();
        ids.Should().HaveCountGreaterThan(60);
        foreach (var id in ids)
        {
            doc.Should().Contain($"| {id} |", $"rule {id} must have a row in RULES.md");
        }
    }

    private static object? FromJson(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Number => e.GetDouble(),
        JsonValueKind.String => e.GetString(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Array => e.EnumerateArray().Select(FromJson).ToList(),
        _ => null,
    };
}
