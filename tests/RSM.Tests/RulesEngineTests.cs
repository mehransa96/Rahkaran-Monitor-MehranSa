using RSM.Core.Model;
using RSM.Core.Rules;

namespace RSM.Tests;

public class RulesEngineTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);

    private static CompiledRule Fixture(string name) =>
        RuleLoader.Load(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "rules", name)), name);

    private static FactStore Facts(params (string Key, object? Value)[] facts)
    {
        var store = new FactStore();
        foreach (var (key, value) in facts)
        {
            store.Set(key, value);
        }

        return store;
    }

    [Fact]
    public void Fires_with_formatted_text_evidence_and_source()
    {
        var engine = new RulesEngine([Fixture("maxdop.json")]);

        var result = engine.Evaluate("n1", NodeRoles.Data, Facts(("sql.config.maxdop", 8), ("sql.cpu.logical", 16)), Now);

        var finding = result.Findings.Should().ContainSingle().Subject;
        finding.Severity.Should().Be(Severity.Warning);
        finding.Title.En.Should().Be("MAXDOP is 8");
        finding.Title.Fa.Should().Be("MAXDOP برابر 8 است");
        finding.Explanation.En.Should().Be("Expected value is 4.");
        finding.Evidence.Select(e => e.FactKey).Should().BeEquivalentTo(["sql.config.maxdop", "sql.cpu.logical"]);
        finding.Source.Kind.Should().Be(SourceKind.StadFanni);
        finding.Source.IsGeneralGuidance.Should().BeFalse();
    }

    [Fact]
    public void Does_not_fire_when_compliant()
    {
        var engine = new RulesEngine([Fixture("maxdop.json")]);

        engine.Evaluate("n1", NodeRoles.Data, Facts(("sql.config.maxdop", 4), ("sql.cpu.logical", 16)), Now).Findings.Should().BeEmpty();
    }

    [Fact]
    public void Escalates_severity()
    {
        var engine = new RulesEngine([Fixture("maxdop.json")]);

        var result = engine.Evaluate("n1", NodeRoles.Data, Facts(("sql.config.maxdop", 0), ("sql.cpu.logical", 32)), Now);

        result.Findings.Single().Severity.Should().Be(Severity.Critical);
    }

    [Fact]
    public void Missing_facts_make_the_rule_indeterminate_not_silent()
    {
        var engine = new RulesEngine([Fixture("maxdop.json")]);

        var result = engine.Evaluate("n1", NodeRoles.Data, Facts(), Now);

        result.Findings.Should().BeEmpty();
        result.Indeterminate.Should().ContainSingle().Which.MissingFact.Should().Be("sql.config.maxdop");
    }

    [Fact]
    public void Skips_nodes_without_matching_roles()
    {
        var engine = new RulesEngine([Fixture("maxdop.json")]);

        var result = engine.Evaluate("n1", NodeRoles.App | NodeRoles.Arr, Facts(("sql.config.maxdop", 8)), Now);

        result.Findings.Should().BeEmpty();
        result.Indeterminate.Should().BeEmpty();
    }

    [Fact]
    public void For_each_rules_run_once_per_instance()
    {
        var engine = new RulesEngine([Fixture("recovery.json")]);
        var facts = Facts(
            ("db[Rahkaran].is_rahkaran", true), ("db[Rahkaran].recovery_model", "SIMPLE"),
            ("db[ReportServer].is_rahkaran", false), ("db[ReportServer].recovery_model", "SIMPLE"),
            ("db[Rahkaran_Old].is_rahkaran", true), ("db[Rahkaran_Old].recovery_model", "FULL"));

        var result = engine.Evaluate("n1", NodeRoles.Data, facts, Now);

        var finding = result.Findings.Should().ContainSingle().Subject;
        finding.Instance.Should().Be("Rahkaran");
        finding.Title.En.Should().Be("Database Rahkaran uses SIMPLE recovery");
        finding.Evidence.Select(e => e.FactKey).Should().Contain("db[Rahkaran].recovery_model");
    }

    [Theory]
    [InlineData("""{ "id": "x", "layer": "Sql", "when": "a ==", "severity": "Info", "title": {"fa":"ا","en":"a"}, "explanation": {"fa":"ا","en":"a"}, "recommendation": {"fa":"ا","en":"a"}, "source": {"kind":"General","citation":"c"} }""", "condition")]
    [InlineData("""{ "id": "x", "layer": "Sql", "when": "a == 1", "severity": "Info", "title": {"fa":"","en":"a"}, "explanation": {"fa":"ا","en":"a"}, "recommendation": {"fa":"ا","en":"a"}, "source": {"kind":"General","citation":"c"} }""", "fa and en")]
    [InlineData("""{ "id": "x", "layer": "Sql", "when": "a == 1", "severity": "Info", "title": {"fa":"ا","en":"a"}, "explanation": {"fa":"ا","en":"a"}, "recommendation": {"fa":"ا","en":"a"}, "source": {"kind":"General","citation":""} }""", "citation")]
    [InlineData("""{ "id": "x", "layer": "Nope" }""", "invalid JSON")]
    public void Invalid_rules_are_refused_with_a_reason(string json, string reason) =>
        FluentActions.Invoking(() => RuleLoader.Load(json, "inline")).Should().Throw<RuleValidationException>().WithMessage($"*{reason}*");

    [Fact]
    public void Fact_store_lists_instances() =>
        Facts(("db[a].x", 1), ("db[B].y", 2), ("db[a].z", 3), ("iis.pool[p1].q", 0)).Instances("db").Should().Equal("a", "B");
}
