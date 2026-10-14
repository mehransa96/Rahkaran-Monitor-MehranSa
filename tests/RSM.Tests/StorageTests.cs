using System.Text;
using RSM.Core.Model;
using RSM.Core.Profiles;
using RSM.Safety.Files;
using RSM.Safety.Secrets;
using RSM.Storage.Profiles;
using RSM.Storage.Settings;

namespace RSM.Tests;

public sealed class StorageTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rsm-store-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static SiteProfile Site() => new()
    {
        Id = "site1",
        Name = "مشتری نمونه",
        Nodes =
        [
            new NodeProfile
            {
                Id = "node1",
                Name = "DB01",
                Host = "10.0.0.5",
                Roles = NodeRoles.Data | NodeRoles.Report,
                Sql = new SqlEndpoint { Server = "10.0.0.5,1433", UserName = "rsm_monitor", TrustServerCertificate = true },
                Redis = new RedisEndpoint { Host = "10.0.0.6", HasPassword = true },
            },
        ],
    };

    [Fact]
    public async Task Profiles_round_trip_as_readable_json_without_secrets()
    {
        var folder = new DataFolder(_root, isPortable: true);
        var store = new ProfileStore(folder);

        await store.SaveAsync(Site(), CancellationToken.None);
        var loaded = (await store.LoadAllAsync(CancellationToken.None)).Single();

        loaded.Name.Should().Be("مشتری نمونه");
        loaded.Nodes.Single().Roles.Should().Be(NodeRoles.Data | NodeRoles.Report);
        loaded.Nodes.Single().Sql!.Server.Should().Be("10.0.0.5,1433");
        var json = await folder.ReadAllTextAsync(Path.Combine("sites", "site1.json"), CancellationToken.None);
        json.Should().Contain("\"roles\": \"Data, Report\"");
        System.Text.RegularExpressions.Regex.IsMatch(json, "\"(password|pwd|secret|sqlPassword|redisPassword)\"\\s*:", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
            .Should().BeFalse("profile JSON must never carry secret values");
    }

    [Fact]
    public async Task Remembered_secrets_are_encrypted_at_rest()
    {
        var folder = new DataFolder(_root, isPortable: true);
        var secrets = new SecretStore(folder, new ReversingProtector());

        await secrets.SetAsync("site1", "node1", SecretKind.SqlPassword, "P@ssw0rd!", remember: true, CancellationToken.None);

        var file = folder.ListFiles("secrets", "*.bin").Single();
        Encoding.UTF8.GetString(await folder.ReadAllBytesAsync(file, CancellationToken.None)).Should().NotContain("P@ssw0rd!");
        (await secrets.GetAsync("site1", "node1", SecretKind.SqlPassword, CancellationToken.None)).Should().Be("P@ssw0rd!");
    }

    [Fact]
    public async Task Not_remembered_secrets_never_touch_disk()
    {
        var folder = new DataFolder(_root, isPortable: true);
        var secrets = new SecretStore(folder, new ReversingProtector());

        await secrets.SetAsync("site1", "node1", SecretKind.RedisPassword, "redis-pw", remember: false, CancellationToken.None);

        folder.ListFiles("secrets", "*").Should().BeEmpty();
        (await secrets.GetAsync("site1", "node1", SecretKind.RedisPassword, CancellationToken.None)).Should().Be("redis-pw");
        (await new SecretStore(folder, new ReversingProtector()).GetAsync("site1", "node1", SecretKind.RedisPassword, CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task Platform_without_dpapi_keeps_secrets_in_memory_only()
    {
        var folder = new DataFolder(_root, isPortable: true);
        var secrets = new SecretStore(folder, new NoPersistenceSecretProtector());

        await secrets.SetAsync("site1", "node1", SecretKind.SqlPassword, "pw", remember: true, CancellationToken.None);

        folder.ListFiles("secrets", "*").Should().BeEmpty();
        (await secrets.GetAsync("site1", "node1", SecretKind.SqlPassword, CancellationToken.None)).Should().Be("pw");
    }

    [Fact]
    public async Task Deleting_a_site_removes_its_secrets()
    {
        var folder = new DataFolder(_root, isPortable: true);
        var secrets = new SecretStore(folder, new ReversingProtector());
        await secrets.SetAsync("site1", "node1", SecretKind.SqlPassword, "a", true, CancellationToken.None);
        await secrets.SetAsync("site2", "node1", SecretKind.SqlPassword, "b", true, CancellationToken.None);

        secrets.DeleteSite("site1");

        folder.ListFiles("secrets", "*.bin").Should().ContainSingle().Which.Should().Contain("site2");
    }

    [Theory]
    [InlineData("../evil")]
    [InlineData("a/b")]
    [InlineData("")]
    public async Task Ids_cannot_escape_their_folder(string id)
    {
        var store = new ProfileStore(new DataFolder(_root, isPortable: true));

        var act = () => store.SaveAsync(Site() with { Id = id }, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Settings_round_trip_and_survive_corruption()
    {
        var folder = new DataFolder(_root, isPortable: true);
        var store = new AppSettingsStore(folder);

        (await store.LoadAsync(CancellationToken.None)).Language.Should().Be(UiLanguage.Fa);
        await store.SaveAsync(new AppSettings { Language = UiLanguage.En, Theme = "Dark" }, CancellationToken.None);
        (await store.LoadAsync(CancellationToken.None)).Theme.Should().Be("Dark");

        await folder.WriteAllTextAsync("settings.json", "{ not json", CancellationToken.None);
        (await store.LoadAsync(CancellationToken.None)).Should().Be(new AppSettings());
    }

    /// <summary>Stand-in for DPAPI on Linux CI: reversible but not plain text.</summary>
    private sealed class ReversingProtector : ISecretProtector
    {
        public bool CanPersist => true;

        public byte[] Protect(string secret, string purpose) => Encoding.UTF8.GetBytes(new string(secret.Reverse().ToArray()) + "|" + purpose).Select(b => (byte)(b ^ 0x5A)).ToArray();

        public string Unprotect(byte[] protectedBytes, string purpose)
        {
            var text = Encoding.UTF8.GetString(protectedBytes.Select(b => (byte)(b ^ 0x5A)).ToArray());
            text.Should().EndWith("|" + purpose);
            return new string(text[..^(purpose.Length + 1)].Reverse().ToArray());
        }
    }
}
