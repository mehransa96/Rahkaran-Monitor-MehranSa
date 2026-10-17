using FluentAssertions;
using RSM.Core.Model;
using RSM.Core.Snapshots;

namespace RSM.Tests;

public sealed class SnapshotCompareTests
{
    [Fact]
    public void Before_vs_after_reports_moved_numbers_changed_texts_and_finding_changes()
    {
        var before = LabSnapshot.Build(f =>
        {
            f.Set("site[Rahkaran].slowest_p95_ms", 400.0);
            f.Set("sql.waits.top", new List<object?> { "LCK_M_X 900 ms", "PAGEIOLATCH_SH 300 ms" });
        });
        var after = LabSnapshot.Build(f =>
        {
            f.Set("sql.config.max_degree_of_parallelism", 4);      // fixes SQL-CFG-005
            f.Set("site[Rahkaran].slowest_p95_ms", 1000.0);         // IIS p95 +150 %
            f.Set("sql.waits.top", new List<object?> { "WRITELOG 2000 ms" });
            f.Set("sql.version", "16.0.4135.4");
            f.Set("sql.uptime_hours", 1.0);                         // always changes: ignored
        });

        var result = SnapshotCompare.Compare(before, after);

        result.UnmatchedNodes.Should().BeEmpty();
        result.Numbers.Should().Contain(new FactChange(LabSnapshot.NodeName, "site[Rahkaran].slowest_p95_ms", "400", "1000", 1.5));
        result.Numbers[0].Change.Should().BeNull("a value that was zero before ranks first");
        result.Numbers.Should().Contain(c => c.Key == "sql.config.max_degree_of_parallelism" && c.Before == "0" && c.After == "4" && c.Change == null, "from zero has no ratio");
        result.Numbers.Should().NotContain(c => c.Key.Contains("uptime"));
        result.Texts.Should().Contain(t => t.Key == "sql.waits.top" && t.After == "WRITELOG 2000 ms");
        result.Texts.Should().Contain(t => t.Key == "sql.version" && t.After == "16.0.4135.4");
        result.Findings.Should().Contain(f => f.RuleId == "SQL-CFG-005" && f.Kind == FindingChangeKind.Resolved && f.Before == Severity.Warning);
    }

    [Fact]
    public void Identical_diagnoses_have_no_change_and_small_moves_are_ignored()
    {
        var a = LabSnapshot.Build(f => f.Set("site[Rahkaran].slowest_p95_ms", 400.0));
        var b = LabSnapshot.Build(f => f.Set("site[Rahkaran].slowest_p95_ms", 420.0));

        var same = SnapshotCompare.Compare(a, a);
        same.Numbers.Should().BeEmpty();
        same.Texts.Should().BeEmpty();
        same.Findings.Should().BeEmpty();
        SnapshotCompare.Compare(a, b).Numbers.Should().BeEmpty("+5 % is below the 10 % threshold");
        SnapshotCompare.Compare(a, b, 0.01).Numbers.Should().ContainSingle();
    }

    [Fact]
    public void Nodes_match_by_host_when_ids_differ_and_unmatched_nodes_are_listed()
    {
        var a = LabSnapshot.Build();
        var renamed = a with { Nodes = [a.Nodes[0] with { Node = a.Nodes[0].Node with { Id = "other-id" } }] };
        SnapshotCompare.Compare(a, renamed).UnmatchedNodes.Should().BeEmpty();

        var moved = a with { Nodes = [a.Nodes[0] with { Node = a.Nodes[0].Node with { Id = "x", Host = "elsewhere", Name = "NEW" } }] };
        SnapshotCompare.Compare(a, moved).UnmatchedNodes.Should().BeEquivalentTo(["NEW", LabSnapshot.NodeName]);
    }
}
