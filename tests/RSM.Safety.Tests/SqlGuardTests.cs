using RSM.Safety.Sql;

namespace RSM.Safety.Tests;

/// <summary>
/// SqlGuard must reject every statement that could change a server (malicious corpus) and accept the read-only
/// shapes RSM's catalog needs (allowed corpus). Each malicious case names the rule expected to fire first.
/// </summary>
public class SqlGuardTests
{
    public static TheoryData<string, string> Malicious => new()
    {
        // Statement types
        { "SELECT 1; DROP TABLE dbo.x", "SG010" },
        { "SELECT name FROM sys.databases; DROP DATABASE Rahkaran", "SG010" },
        { "UPDATE dbo.t SET a = 1", "SG010" },
        { "DELETE FROM dbo.t", "SG010" },
        { "MERGE dbo.t AS t USING sys.objects AS s ON 1 = 0 WHEN NOT MATCHED THEN INSERT (a) VALUES (1);", "SG010" },
        { "TRUNCATE TABLE dbo.t", "SG010" },
        { "CREATE TABLE #t (a int)", "SG010" },
        { "CREATE LOGIN x WITH PASSWORD = 'p@ssw0rd!'", "SG010" },
        { "GRANT CONTROL SERVER TO x", "SG010" },
        { "ALTER SERVER CONFIGURATION SET PROCESS AFFINITY CPU = AUTO", "SG010" },
        { "ALTER DATABASE x SET QUERY_STORE CLEAR", "SG010" },
        { "CREATE EVENT SESSION s ON SERVER ADD EVENT sqlserver.sql_batch_completed", "SG010" },
        { "DBCC CHECKDB", "SG010" },
        { "DBCC FREEPROCCACHE", "SG010" },
        { "KILL 55", "SG010" },
        { "RECONFIGURE", "SG010" },
        { "BACKUP DATABASE x TO DISK = 'c:\\x.bak'", "SG010" },
        { "CHECKPOINT", "SG010" },
        { "SHUTDOWN", "SG010" },
        { "BEGIN TRAN; SELECT 1; COMMIT", "SG010" },
        { "RAISERROR('x', 10, 1) WITH LOG", "SG010" },
        { "PRINT 'x'", "SG010" },
        { "THROW 50000, 'x', 1", "SG010" },
        { "USE master", "SG010" },
        { "EXECUTE AS LOGIN = 'sa'; SELECT 1", "SG010" },
        { "WAITFOR DELAY '00:00:05'; DELETE FROM t", "SG010" },
        { "DECLARE c CURSOR FOR SELECT 1; OPEN c", "SG010" },
        { "SET CONTEXT_INFO 0x01", "SG033" },
        { "SET IDENTITY_INSERT dbo.t ON", "SG010" },
        { "BULK INSERT dbo.t FROM 'c:\\x.csv'", "SG010" },
        { "WHILE 1 = 1 SELECT 1", "SG010" },
        { "x: SELECT 1; GOTO x", "SG010" },
        { "IF 1 = 1 BEGIN DELETE FROM dbo.t END", "SG010" },
        { "WITH x AS (SELECT 1 AS a) DELETE FROM dbo.t", "SG010" },
        { "DECLARE @t TABLE (a int); INSERT INTO @t SELECT 1; DELETE FROM @t", "SG010" },

        // Comments, batches and hidden statements
        { "SELECT 1 /* harmless */; DELETE FROM dbo.t", "SG010" },
        { "SELECT 1 -- harmless\nDELETE FROM dbo.t", "SG010" },
        { "/* DELETE */ SELECT 1; SHUTDOWN WITH NOWAIT", "SG010" },
        { "SELECT 1\nGO\nDROP TABLE x", "SG002" },
        { "SELECT 'a'; EXEC('DROP TABLE x') --", "SG050" },

        // EXEC
        { "EXEC('DROP TABLE x')", "SG050" },
        { "EXEC sp_executesql N'DELETE FROM t'", "SG050" },
        { "EXECUTE master.dbo.xp_cmdshell 'dir'", "SG050" },
        { "EXEC xp_cmdshell 'whoami'", "SG050" },
        { "EXEC sp_configure 'xp_cmdshell', 1", "SG050" },
        { "DECLARE @t TABLE (a nvarchar(max)); INSERT INTO @t EXEC master.dbo.xp_cmdshell 'dir'", "SG050" },
        { "DECLARE @t TABLE (a int); INSERT INTO @t EXEC (N'SELECT 1')", "SG050" },
        { "DECLARE @p sysname = N'xp_cmdshell'; EXEC @p 'dir'", "SG050" },
        { "EXEC LNK.master.sys.xp_readerrorlog 0, 1", "SG050" },
        { "EXEC ('SELECT 1') AT LNK", "SG050" },
        { "EXEC master.sys.xp_readerrorlog 0, 1 AT LNK", "SG001" },
        { "DECLARE @n int; EXEC master.sys.xp_readerrorlog @n OUTPUT", "SG051" },

        // INSERT targets
        { "INSERT INTO dbo.t VALUES (1)", "SG040" },
        { "INSERT dbo.t SELECT name FROM sys.objects", "SG040" },
        { "INSERT INTO @undeclared SELECT 1", "SG040" },
        { "SELECT name FROM sys.objects; INSERT INTO dbo.audit SELECT name FROM sys.objects", "SG040" },
        { "DECLARE @t TABLE (a int); INSERT INTO @t OUTPUT inserted.a INTO dbo.x SELECT 1", "SG023" },

        // SELECT … INTO, temp tables, linked servers, rowset functions
        { "SELECT * INTO dbo.copy FROM sys.objects", "SG030" },
        { "SELECT * INTO #t FROM sys.objects", "SG030" },
        { "SELECT * FROM #t", "SG065" },
        { "SELECT * FROM ##global", "SG065" },
        { "SELECT * FROM OPENROWSET('SQLNCLI', 'Server=.;Trusted_Connection=yes', 'SELECT 1')", "SG061" },
        { "SELECT * FROM OPENROWSET(BULK 'c:\\x.txt', SINGLE_CLOB) AS x", "SG061" },
        { "SELECT * FROM OPENQUERY(LNK, 'SELECT 1')", "SG061" },
        { "SELECT * FROM OPENDATASOURCE('SQLNCLI', 'Data Source=x').master.sys.objects", "SG061" },
        { "SELECT * FROM LNK.master.sys.objects", "SG064" },

        // Side effects hidden in expressions or hints
        { "SELECT NEXT VALUE FOR dbo.seq", "SG020" },
        { "SELECT * FROM sys.objects WITH (UPDLOCK)", "SG021" },
        { "SELECT * FROM sys.objects WITH (TABLOCKX)", "SG021" },
        { "SELECT * FROM sys.objects WITH (HOLDLOCK)", "SG021" },
        { "SELECT * FROM sys.objects OPTION (QUERYTRACEON 3604)", "SG022" },
        { "DECLARE @c CURSOR", "SG031" },
        { "DECLARE @x xml = N'<a/>'; SET @x.modify('delete /a')", "SG032" },

        // Session settings other than the hardening prefix
        { "SET ANSI_NULLS OFF", "SG033" },
        { "SET NOCOUNT OFF", "SG033" },
        { "SET TRANSACTION ISOLATION LEVEL SERIALIZABLE", "SG033" },
        { "SET DEADLOCK_PRIORITY HIGH", "SG033" },

        // Catalog object allow-list
        { "SELECT dbo.fn_do_something()", "SG067" },
        { "SELECT * FROM dbo.fn_tvf()", "SG062" },
        { "SELECT * FROM dbo.Users", "SG063" },
        { "SELECT * FROM Rahkaran.dbo.Users", "SG063" },
        { "SELECT name FROM sys.objects UNION ALL SELECT name FROM dbo.secret", "SG063" },
        { "SELECT (SELECT TOP (1) name FROM dbo.users)", "SG063" },
        { "SELECT * FROM msdb.dbo.sysmail_profile", "SG063" },
        { "SELECT * FROM tempdb.dbo.anything", "SG063" },
        { "SELECT * FROM Users", "SG063" },

        // Parse failures are rejections
        { "SELECT * FROM sys.objects FOR UPDATE", "SG024" },
        { "SELECT * FROM sys.objects FOR BROWSE", "SG024" },
        { "\u0415XEC xp_cmdshell 'dir'", "SG" }, // Cyrillic 'Е' homoglyph: never allowed, whatever the parse outcome
        { "", "SG" },
    };

    public static TheoryData<string> Allowed => new()
    {
        HardenedBatch.Prefix + "SELECT r.session_id, t.text FROM sys.dm_exec_requests AS r CROSS APPLY sys.dm_exec_sql_text(r.sql_handle) AS t WHERE r.session_id <> @@SPID;",
        """
        WITH chain AS (
            SELECT r.session_id, r.blocking_session_id, 0 AS lvl FROM sys.dm_exec_requests AS r WHERE r.blocking_session_id = 0
            UNION ALL
            SELECT r.session_id, r.blocking_session_id, c.lvl + 1 FROM sys.dm_exec_requests AS r JOIN chain AS c ON r.blocking_session_id = c.session_id
        )
        SELECT * FROM chain OPTION (MAXDOP 1);
        """,
        """
        DECLARE @lognum int = 0;
        DECLARE @t TABLE (LogDate datetime, ProcessInfo nvarchar(100), [Text] nvarchar(max));
        INSERT INTO @t EXEC master.sys.xp_readerrorlog @lognum, 1;
        INSERT INTO @t EXEC xp_readerrorlog 1, 1;
        INSERT INTO @t EXEC master..xp_readerrorlog 2, 1;
        SELECT * FROM @t WHERE LogDate >= DATEADD(day, -1, SYSDATETIME());
        """,
        """
        SELECT x.e.value('(@timestamp)[1]', 'datetime2') AS ts
        FROM (SELECT CAST(t.target_data AS xml) AS d FROM sys.dm_xe_session_targets AS t JOIN sys.dm_xe_sessions AS s ON s.address = t.event_session_address WHERE s.name = N'system_health') AS a
        CROSS APPLY a.d.nodes('//RingBufferTarget/event') AS x(e);
        """,
        "SELECT TOP (10) b.database_name FROM msdb.dbo.backupset AS b WITH (NOLOCK) JOIN msdb.dbo.backupmediafamily AS f ON f.media_set_id = b.media_set_id ORDER BY b.backup_finish_date DESC OPTION (RECOMPILE);",
        "SELECT w.wait_type FROM sys.dm_os_wait_stats AS w WHERE w.wait_type NOT IN (SELECT v.wait_type FROM (VALUES (N'SLEEP_TASK'), (N'LAZYWRITER_SLEEP')) AS v(wait_type));",
        "IF CAST(SERVERPROPERTY('ProductMajorVersion') AS int) >= 14 SELECT host_platform FROM sys.dm_os_host_info ELSE SELECT N'Windows' AS host_platform;",
        "SELECT permission_name FROM fn_my_permissions(NULL, 'SERVER');",
        "SELECT value FROM STRING_SPLIT(N'a,b', N',');",
        "SELECT CAST(event_data AS xml) AS x FROM sys.fn_xe_file_target_read_file(N'system_health*.xel', NULL, NULL, NULL);",
        "DECLARE @since datetime2 = SYSUTCDATETIME(); SET @since = DATEADD(hour, -4, @since); SELECT @since AS since;",
        "SELECT name FROM INFORMATION_SCHEMA.TABLES; SELECT name FROM tempdb.sys.database_files; SELECT name FROM master.sys.databases;",
        "SELECT j.name, h.run_status FROM msdb.dbo.sysjobs AS j JOIN msdb.dbo.sysjobhistory AS h ON h.job_id = j.job_id;",
        "SELECT TOP (20) ItemPath, TimeDataRetrieval FROM dbo.ExecutionLog3 ORDER BY TimeStart DESC;",
        "SELECT sys.fn_varbintohexstr(0x01) AS hex, HAS_PERMS_BY_NAME(NULL, NULL, N'VIEW SERVER STATE') AS vss;",
    };

    [Theory]
    [MemberData(nameof(Malicious))]
    public void Rejects_malicious_or_writing_sql(string sql, string expectedRule)
    {
        var verdict = SqlGuard.Check(sql);

        verdict.IsAllowed.Should().BeFalse($"'{sql}' must not be executable");
        verdict.Violations.Should().Contain(v => v.Rule.StartsWith(expectedRule, StringComparison.Ordinal), verdict.ToString());
    }

    [Theory]
    [MemberData(nameof(Malicious))]
    public void Rejects_malicious_sql_even_after_the_hardening_prefix(string sql, string expectedRule)
    {
        _ = expectedRule;
        if (string.IsNullOrWhiteSpace(sql))
        {
            return; // the prefix alone is harmless
        }

        SqlGuard.Check(HardenedBatch.Prefix + sql).IsAllowed.Should().BeFalse();
    }

    [Theory]
    [MemberData(nameof(Allowed))]
    public void Allows_read_only_catalog_shapes(string sql)
    {
        var verdict = SqlGuard.Check(sql);

        verdict.IsAllowed.Should().BeTrue(verdict.ToString());
    }

    [Fact]
    public void Corpus_is_large_enough() => Malicious.Count.Should().BeGreaterOrEqualTo(60);

    [Fact]
    public void Hardening_prefix_itself_is_allowed() => SqlGuard.Check(HardenedBatch.Prefix).IsAllowed.Should().BeTrue();

    [Theory]
    [InlineData("SELECT TOP (10) * FROM dbo.SomeRahkaranTable;")]
    [InlineData("SELECT c.Name FROM dbo.A AS a JOIN Sales.B AS c ON c.Id = a.Id;")]
    public void Query_pack_profile_allows_tables_of_the_target_database(string sql) =>
        SqlGuard.Check(sql, SqlGuardProfile.QueryPack).IsAllowed.Should().BeTrue();

    [Theory]
    [InlineData("DECLARE @t TABLE (a datetime, b nvarchar(10), c nvarchar(max)); INSERT INTO @t EXEC master.sys.xp_readerrorlog 0, 1;", "SG050")]
    [InlineData("SELECT * FROM OtherDb.dbo.T;", "SG063")]
    [InlineData("SELECT dbo.fn_x(1);", "SG067")]
    [InlineData("SELECT * FROM sys.dm_exec_sql_text(0x01);", "SG062")]
    [InlineData("UPDATE dbo.T SET a = 1;", "SG010")]
    public void Query_pack_profile_is_stricter(string sql, string rule)
    {
        var verdict = SqlGuard.Check(sql, SqlGuardProfile.QueryPack);

        verdict.IsAllowed.Should().BeFalse();
        verdict.Violations.Should().Contain(v => v.Rule.StartsWith(rule, StringComparison.Ordinal), verdict.ToString());
    }

    [Fact]
    public void Violations_carry_position_and_fragment()
    {
        var verdict = SqlGuard.Check("SELECT 1;\nDELETE FROM dbo.t;");

        var violation = verdict.Violations.First(v => v.Rule.StartsWith("SG010", StringComparison.Ordinal));
        violation.Line.Should().Be(2);
        violation.Fragment.Should().StartWith("DELETE");
    }
}
