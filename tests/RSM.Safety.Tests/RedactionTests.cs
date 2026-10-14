using RSM.Safety.Logging;
using Serilog;

namespace RSM.Safety.Tests;

public sealed class RedactionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rsm-log-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Theory]
    [InlineData("Server=db;User ID=rsm_monitor;Password=Sup3r$ecret;Encrypt=True", "Sup3r$ecret")]
    [InlineData("Data Source=db;uid=sa;pwd=hunter2", "hunter2")]
    [InlineData("Password=\"quoted;secret\";Server=x", "quoted;secret")]
    [InlineData("sending AUTH s3cret to redis", "s3cret")]
    [InlineData("Authorization: Basic dXNlcjpwYXNzd29yZA==", "dXNlcjpwYXNzd29yZA==")]
    public void Redacts_secrets(string text, string secret) => Redactor.Redact(text).Should().NotContain(secret);

    [Theory]
    [InlineData("Password", true)]
    [InlineData("SqlPassword", true)]
    [InlineData("ConnectionString", true)]
    [InlineData("ApiKey", true)]
    [InlineData("NodeName", false)]
    [InlineData("Host", false)]
    public void Recognises_secret_property_names(string name, bool secret) => Redactor.IsSecretName(name).Should().Be(secret);

    [Fact]
    public void Secrets_never_reach_the_log_file()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "rsm.log");
        using (var logger = new LoggerConfiguration().Enrich.With<RedactionEnricher>().WriteTo.File(path).CreateLogger())
        {
            logger.Information("Connecting with {ConnStr}", "Server=db;User ID=rsm;Password=TopSecret1");
            logger.Information("Login {Password} for {Node}", "TopSecret2", "db01");
            logger.Information("Profile {@Profile}", new { Host = "db01", SqlPassword = "TopSecret3", Nested = new { Token = "TopSecret4" } });
            logger.Information("Redis {Command}", "AUTH TopSecret5");
        }

        var content = File.ReadAllText(path);
        content.Should().Contain("db01");
        foreach (var secret in new[] { "TopSecret1", "TopSecret2", "TopSecret3", "TopSecret4", "TopSecret5" })
        {
            content.Should().NotContain(secret);
        }
    }
}
