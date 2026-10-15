using RSM.Collectors.Database;

namespace RSM.Tests;

public class DatabaseLogicTests
{
    private static readonly DateTime Now = new(2026, 10, 14, 12, 0, 0);

    private static BackupSet Set(int id, char type, DateTime finish, decimal first, decimal last, bool copyOnly = false, long size = 10_000_000_000, int seconds = 600, string device = @"E:\Backup\Rk.bak", bool checksum = true) =>
        new(id, "Rahkaran", type, finish.AddSeconds(-seconds), finish, size, copyOnly, checksum, first, last, [device]);

    [Fact]
    public void Healthy_chain_is_not_broken()
    {
        var sets = new[]
        {
            Set(1, 'D', Now.AddHours(-14), 1000, 1100),
            Set(2, 'L', Now.AddHours(-12), 900, 1200),
            Set(3, 'L', Now.AddHours(-10), 1200, 1300),
            Set(4, 'L', Now.AddHours(-8), 1300, 1400),
        };

        BackupAnalysis.IsLogChainBroken(sets).Should().BeFalse();
    }

    [Fact]
    public void Gap_between_log_backups_breaks_the_chain()
    {
        var sets = new[]
        {
            Set(1, 'D', Now.AddHours(-14), 1000, 1100),
            Set(2, 'L', Now.AddHours(-12), 900, 1200),
            Set(3, 'L', Now.AddHours(-8), 1350, 1400), // 1200..1350 missing
        };

        BackupAnalysis.IsLogChainBroken(sets).Should().BeTrue();
    }

    [Fact]
    public void Copy_only_log_backups_are_ignored_for_the_chain()
    {
        var sets = new[]
        {
            Set(1, 'D', Now.AddHours(-14), 1000, 1100),
            Set(2, 'L', Now.AddHours(-12), 900, 1200),
            Set(3, 'L', Now.AddHours(-11), 1200, 1250, copyOnly: true),
            Set(4, 'L', Now.AddHours(-10), 1200, 1300),
        };

        BackupAnalysis.IsLogChainBroken(sets).Should().BeFalse();
    }

    [Fact]
    public void Facts_cover_rpo_duplicates_checksums_unc_and_typical_hour()
    {
        var sets = new[]
        {
            Set(1, 'D', Now.AddHours(-20), 1000, 1100),
            Set(2, 'D', Now.AddHours(-3), 2000, 2100, device: @"\\nas\sql\Rk.bak", checksum: false),
            Set(3, 'L', Now.AddHours(-1), 1100, 2200),
        };

        var f = BackupAnalysis.Analyze(sets, Now, null, null, null);

        f.HasFull.Should().BeTrue();
        f.HoursSinceFull.Should().BeApproximately(3, 0.01);
        f.HoursSinceLog.Should().BeApproximately(1, 0.01);
        f.HoursSinceDiff.Should().BeNull();
        f.NonCopyOnlyFullsLast24h.Should().Be(2);
        f.FullBackupDevicesLast24h.Should().Be(2);
        f.WithoutChecksum30d.Should().Be(1);
        f.UsesUncDestination.Should().BeTrue();
        f.TypicalFullHour.Should().NotBeNull();
    }

    [Fact]
    public void Detects_throughput_drop()
    {
        var sets = Enumerable.Range(1, 5).Select(i => Set(i, 'D', Now.AddDays(-6 + i), i * 100, i * 100 + 10, seconds: 600)).ToList();
        sets.Add(Set(9, 'D', Now.AddHours(-1), 9000, 9010, seconds: 3600)); // same size, 6× slower

        BackupAnalysis.Analyze(sets, Now, null, null, null).ThroughputDropped.Should().BeTrue();
    }

    [Fact]
    public void Never_backed_up_has_no_full()
    {
        var f = BackupAnalysis.Analyze([], Now, null, null, null);

        f.HasFull.Should().BeFalse();
        f.HoursSinceFull.Should().BeNull();
    }

    [Fact]
    public void Striped_backups_collapse_into_one_set()
    {
        var rows = new[]
        {
            new DatabaseCollector.BackupHistoryRow { BackupSetId = 7, DatabaseName = "Rk", BackupType = "D", FinishDate = Now, DeviceName = @"E:\a.bak" },
            new DatabaseCollector.BackupHistoryRow { BackupSetId = 7, DatabaseName = "Rk", BackupType = "D", FinishDate = Now, DeviceName = @"F:\b.bak" },
        };

        DatabaseCollector.ToBackupSets(rows).Should().ContainSingle().Which.Devices.Should().HaveCount(2);
    }

    private static readonly DatabaseCandidate[] Dbs =
    [
        new("master", false, true, 10, 3),
        new("ReportServer", true, true, 500, 20),
        new("Rahkaran_Test", true, true, 9000, 2),
        new("RahkaranDB", true, true, 80000, 120),
        new("Archive", true, false, 900000, 0),
    ];

    [Fact]
    public void Setting_wins_over_detection() =>
        RahkaranDatabaseDetector.Detect(Dbs, ["rahkaran_test"]).Should().BeEquivalentTo(new RahkaranDetection(["rahkaran_test"], "setting"));

    [Fact]
    public void Auto_detection_prefers_the_busiest_online_user_database_and_skips_ssrs() =>
        RahkaranDatabaseDetector.Detect(Dbs, []).Should().BeEquivalentTo(new RahkaranDetection(["RahkaranDB"], "auto"));

    [Fact]
    public void Idle_server_falls_back_to_the_largest_online_user_database() =>
        RahkaranDatabaseDetector.Detect([new("A", true, true, 100, 0), new("B", true, true, 900, 0)], []).Databases.Should().Equal("B");
}
