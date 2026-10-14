using RSM.Core.Rules.Expressions;

namespace RSM.Tests;

public class ExpressionTests
{
    private sealed class Scope(Dictionary<string, object?> facts) : IEvaluationScope
    {
        public DateTimeOffset Now { get; } = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);

        public bool TryResolve(string path, out object? value) => facts.TryGetValue(path, out value);
    }

    private static readonly Scope Facts = new(new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
    {
        ["sql.config.maxdop"] = 8,
        ["sql.cpu.logical"] = 16L,
        ["os.power.plan"] = "Balanced",
        ["db[Rahkaran].recovery_model"] = "SIMPLE",
        ["sql.version"] = "16.0.4135.4",
        ["db.last_full"] = new DateTimeOffset(2026, 10, 12, 12, 0, 0, TimeSpan.Zero),
        ["waits"] = new List<object?> { 1, 2, 3 },
        ["nothing"] = null,
    });

    [Theory]
    [InlineData("sql.config.maxdop != 4", true)]
    [InlineData("sql.config.maxdop == 8 && sql.cpu.logical > 8", true)]
    [InlineData("sql.config.maxdop * 2 == sql.cpu.logical", true)]
    [InlineData("(1 + 2) * 3 == 9", true)]
    [InlineData("10 % 3 == 1", true)]
    [InlineData("-sql.config.maxdop < 0", true)]
    [InlineData("!(sql.config.maxdop == 4)", true)]
    [InlineData("os.power.plan == 'balanced'", true)]
    [InlineData("os.power.plan in ['High performance', 'Ultimate Performance']", false)]
    [InlineData("db[Rahkaran].recovery_model == \"SIMPLE\"", true)]
    [InlineData("pct(sql.config.maxdop, sql.cpu.logical) == 50", true)]
    [InlineData("max(1, 5, 3) == 5 && min(waits) == 1 && sum(waits) == 6 && avg(waits) == 2 && count(waits) == 3", true)]
    [InlineData("exists(sql.config.maxdop) && !exists(sql.config.unknown)", true)]
    [InlineData("coalesce(sql.config.unknown, nothing, 7) == 7", true)]
    [InlineData("daysSince(db.last_full) == 2 && hoursSince(db.last_full) == 48", true)]
    [InlineData("version_ge(sql.version, '13.0.5026')", true)]
    [InlineData("matches(os.power.plan, '^bal')", true)]
    [InlineData("contains(waits, 2) && startsWith(os.power.plan, 'Bal') && len('abc') == 3", true)]
    [InlineData("lower('ABC') == 'abc' && upper('abc') == 'ABC'", true)]
    [InlineData("sql.config.maxdop > 4 ? true : false", true)]
    [InlineData("nothing == null", true)]
    [InlineData("1_000 == 1000", true)]
    public void Evaluates(string text, bool expected) => Expression.Parse(text).EvaluateBool(Facts).Should().Be(expected);

    [Fact]
    public void Missing_fact_is_not_false_but_indeterminate()
    {
        var act = () => Expression.Parse("sql.config.not_collected > 0").EvaluateBool(Facts);

        act.Should().Throw<MissingFactException>().Which.Path.Should().Be("sql.config.not_collected");
    }

    [Fact]
    public void Short_circuit_avoids_missing_facts() =>
        Expression.Parse("false && sql.config.not_collected > 0").EvaluateBool(Facts).Should().BeFalse();

    [Theory]
    [InlineData("System.IO.File.Delete('x')")]
    [InlineData("Process.Start('cmd')")]
    [InlineData("eval('1')")]
    [InlineData("sql.config.maxdop ==")]
    [InlineData("(1 + 2")]
    [InlineData("'unterminated")]
    [InlineData("a ; b")]
    [InlineData("a = 1")]
    public void Rejects_anything_that_is_not_the_tiny_language(string text) =>
        FluentActions.Invoking(() => Expression.Parse(text).Evaluate(Facts)).Should().Throw<Exception>().Where(e => e is ExpressionException || e is MissingFactException);

    [Fact]
    public void Reports_referenced_paths() =>
        Expression.Parse("sql.config.maxdop != 4 && exists(db[x].y)").ReferencedPaths.Should().BeEquivalentTo(["sql.config.maxdop", "db[x].y"]);

    [Fact]
    public void Regex_has_a_timeout_against_catastrophic_patterns()
    {
        var scope = new Scope(new Dictionary<string, object?> { ["s"] = new string('a', 30) + "!" });

        var act = () => Expression.Parse("matches(s, '^(a+)+$')").EvaluateBool(scope);

        act.Should().Throw<System.Text.RegularExpressions.RegexMatchTimeoutException>();
    }
}
