using RSM.Core.Profiles;
using RSM.Safety.Sql;

namespace RSM.Safety.Tests;

public class SafeSqlExecutorTests
{
    [Theory]
    [InlineData(229, SqlQueryStatus.PermissionDenied)]
    [InlineData(300, SqlQueryStatus.PermissionDenied)]
    [InlineData(4060, SqlQueryStatus.PermissionDenied)]
    [InlineData(18456, SqlQueryStatus.LoginFailed)]
    [InlineData(208, SqlQueryStatus.ObjectMissing)]
    [InlineData(-2, SqlQueryStatus.Timeout)]
    [InlineData(1222, SqlQueryStatus.Timeout)]
    [InlineData(53, SqlQueryStatus.Unreachable)]
    [InlineData(50000, SqlQueryStatus.Error)]
    public void Classifies_sql_errors(int number, SqlQueryStatus expected) => SafeSqlExecutor.Classify(number).Should().Be(expected);

    [Fact]
    public void Scalar_parameters_are_accepted() =>
        FluentActions.Invoking(() => SafeSqlExecutor.ValidateParameters(new { a = 1, b = "x", c = (DateTime?)null, d = new byte[] { 1 } })).Should().NotThrow();

    [Fact]
    public void Enumerable_parameters_are_rejected_because_dapper_would_rewrite_the_sql() =>
        FluentActions.Invoking(() => SafeSqlExecutor.ValidateParameters(new { ids = new[] { 1, 2 } })).Should().Throw<ArgumentException>();

    [Fact]
    public void Connection_string_is_hardened()
    {
        using var session = new SqlNodeSession("n1", new SqlEndpoint { Server = "db01,1433", UserName = "rsm_monitor", Encrypt = SqlEncryptMode.Optional, TrustServerCertificate = true }, "s3cr3t");

        var cs = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(session.BuildConnectionString("msdb"));

        cs.ApplicationName.Should().Be("RSM-ReadOnlyMonitor");
        cs.ApplicationIntent.Should().Be(Microsoft.Data.SqlClient.ApplicationIntent.ReadOnly);
        cs.InitialCatalog.Should().Be("msdb");
        cs.ConnectTimeout.Should().Be(5);
        cs.PersistSecurityInfo.Should().BeFalse();
        cs.MultipleActiveResultSets.Should().BeFalse();
        cs.TrustServerCertificate.Should().BeTrue();
        cs.Encrypt.Should().Be(Microsoft.Data.SqlClient.SqlConnectionEncryptOption.Optional);
    }

    [Fact]
    public void Windows_authentication_carries_no_password()
    {
        using var session = new SqlNodeSession("n1", new SqlEndpoint { Server = "db01", Auth = SqlAuthKind.Windows, UserName = "ignored" }, "ignored");

        var cs = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(session.BuildConnectionString(null));

        cs.IntegratedSecurity.Should().BeTrue();
        cs.Password.Should().BeEmpty();
        cs.InitialCatalog.Should().Be("master");
    }

    [Theory]
    [InlineData("16.0.4135.4", 16, 0, 4135)]
    [InlineData("13.0.5026", 13, 0, 5026)]
    [InlineData("14", 14, 0, 0)]
    public void Parses_versions(string text, int major, int minor, int build) =>
        SqlServerVersion.Parse(text).Should().Be(new SqlServerVersion(major, minor, build));

    [Fact]
    public void Compares_versions()
    {
        (SqlServerVersion.Sql2016Sp2 > SqlServerVersion.Sql2016).Should().BeTrue();
        (new SqlServerVersion(16, 0, 4135) >= SqlServerVersion.Sql2022).Should().BeTrue();
        SqlServerVersion.TryParse("abc", out _).Should().BeFalse();
    }
}
