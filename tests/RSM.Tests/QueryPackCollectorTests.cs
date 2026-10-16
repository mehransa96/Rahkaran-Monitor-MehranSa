using RSM.Collectors.Database;
using RSM.Core.Model;
using RSM.Safety.Sql;

namespace RSM.Tests;

public class QueryPackCollectorTests
{
    private static QueryPackQuery Query()
    {
        const string manifest = """
            { "id": "team", "title": { "fa": "بسته تیم", "en": "Team pack" }, "database": "rk",
              "queries": [ { "key": "users", "file": "users.sql", "title": { "fa": "کاربران", "en": "Users" },
                "thresholds": [
                  { "column": "ActiveUsers", "op": ">", "value": 500, "severity": "Warning", "message": { "fa": "کاربر زیاد", "en": "Many users" } },
                  { "column": "ActiveUsers", "op": ">", "value": 900, "severity": "Critical", "message": { "fa": "کاربر خیلی زیاد", "en": "Far too many users" }, "source": "stad-fanni:doc§2" } ] } ] }
            """;
        return QueryPackCatalog.LoadPack("team", manifest, new Dictionary<string, string> { ["users.sql"] = "SELECT ActiveUsers FROM dbo.T;" }).Accepted.Single();
    }

    private static SqlQueryResult<IReadOnlyDictionary<string, object?>> Rows(params object?[] values) =>
        new(SqlQueryStatus.Ok, values.Select(v => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?> { ["ActiveUsers"] = v }).ToList(), TimeSpan.Zero);

    [Fact]
    public void Breaches_become_facts_with_the_worst_severity_and_its_source()
    {
        var result = QueryPackCollector.Evaluate(Query(), "RK", Rows(120, 950, null));
        var facts = new List<Fact>();
        QueryPackCollector.Apply(result, perDatabase: false, facts);
        var f = facts.ToDictionary(x => x.Key, x => x.Value);

        result.Breaches.Should().HaveCount(2, "950 breaches both thresholds; null never breaches");
        f["pack[team.users].breached"].Should().Be(true);
        f["pack[team.users].breach_severity"].Should().Be("Critical");
        f["pack[team.users].breach_message_en"].Should().Be("Far too many users (ActiveUsers = 950)");
        f["pack[team.users].source"].Should().Be("stad-fanni:doc§2");
        f["pack[team.users].title_en"].Should().Be("Team pack — Users");
        f["pack[team.users].rows"].Should().Be(3);
    }

    [Fact]
    public void A_failed_query_is_reported_per_database_without_breaches()
    {
        var failed = new SqlQueryResult<IReadOnlyDictionary<string, object?>>(SqlQueryStatus.ObjectMissing, [], TimeSpan.Zero, "Invalid object name 'dbo.T'.", 208);
        var facts = new List<Fact>();
        QueryPackCollector.Apply(QueryPackCollector.Evaluate(Query(), "RK2", failed), perDatabase: true, facts);
        var f = facts.ToDictionary(x => x.Key, x => x.Value);

        f["pack[team.users@RK2].ok"].Should().Be(false);
        f["pack[team.users@RK2].status"].Should().Be("ObjectMissing");
        f["pack[team.users@RK2].breached"].Should().Be(false);
        f.Should().NotContainKey("pack[team.users@RK2].breach_severity");
    }
}
