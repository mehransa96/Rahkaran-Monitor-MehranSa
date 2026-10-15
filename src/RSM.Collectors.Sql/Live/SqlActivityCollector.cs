using System.Globalization;
using RSM.Core.Model;
using RSM.Safety.Sql;

namespace RSM.Collectors.Sql.Live;

/// <summary>One session in a blocking chain (level 0 = head blocker).</summary>
public sealed record BlockedSession(
    int SessionId,
    int BlockingSessionId,
    int Level,
    string? Login,
    string? Host,
    string? Program,
    string? Database,
    string? Status,
    string? WaitType,
    long WaitMs,
    string? WaitResource,
    int OpenTranCount,
    long? TranAgeS,
    string? Statement);

/// <summary>A head blocker and everything waiting behind it.</summary>
public sealed record BlockingChain(BlockedSession Head, IReadOnlyList<BlockedSession> Blocked)
{
    /// <summary>Longest wait of any session behind this head = how long the head has been blocking (at least).</summary>
    public long MaxWaitMs => Blocked.Count == 0 ? 0 : Blocked.Max(b => b.WaitMs);

    /// <summary>A sleeping head holds locks from an open transaction nobody is running — the typical "forgot to commit".</summary>
    public bool HeadIsIdle => string.Equals(Head.Status, "sleeping", StringComparison.OrdinalIgnoreCase);

    public string Summary => string.Create(CultureInfo.InvariantCulture,
        $"session {Head.SessionId} ({Head.Login}@{Head.Host}, {Head.Program}, {Head.Status}{(Head.TranAgeS is { } age ? $", transaction open {age} s" : string.Empty)}) blocks {Blocked.Count}: {Truncate(Head.Statement, 160)}");

    internal static string Truncate(string? text, int max)
    {
        var t = (text ?? string.Empty).ReplaceLineEndings(" ").Trim();
        return t.Length > max ? t[..max] + "…" : t;
    }
}

/// <summary>
/// §7.6–7.8 live activity: running requests, blocking chains (head blocker, chain, statement, open-transaction age),
/// sleeping sessions holding open transactions, open transactions and log use, sessions by host/program.
/// Read-only: RSM shows the head blocker; it never offers to kill anything.
/// </summary>
public sealed class SqlActivityCollector(SafeSqlExecutor executor) : SqlLiveCollectorBase<NoState>(executor)
{
    public override string Id => "sql.activity";

    public override TimeSpan DefaultInterval => TimeSpan.FromSeconds(15);

    protected override async Task CollectCoreAsync(INodeContext node, CollectContext context, SqlQueryRunner runner, NoState state, LiveOutput output)
    {
        var requests = await runner.QueryAsync<RequestRow>("sql.req.active").ConfigureAwait(false);
        var chains = await runner.QueryAsync<ChainRow>("sql.block.chains").ConfigureAwait(false);
        var idle = await runner.QueryAsync<IdleTranRow>("sql.req.idle_open_tran").ConfigureAwait(false);
        var transactions = await runner.QueryAsync<TranRow>("sql.tran.active").ConfigureAwait(false);
        var sessions = await runner.QueryAsync<SessionGroupRow>("sql.req.sessions_summary").ConfigureAwait(false);
        Apply(Clock.GetUtcNow(), requests, chains, idle, transactions, sessions, output);
    }

    internal static void Apply(
        DateTimeOffset at,
        IReadOnlyList<RequestRow>? requests,
        IReadOnlyList<ChainRow>? chainRows,
        IReadOnlyList<IdleTranRow>? idle,
        IReadOnlyList<TranRow>? transactions,
        IReadOnlyList<SessionGroupRow>? sessions,
        LiveOutput output)
    {
        if (requests is not null)
        {
            output.Fact("sql.req.active_count", requests.Count);
            output.Fact("sql.req.blocked_count", requests.Count(r => r.BlockingSessionId > 0));
            output.Fact("sql.req.longest_running_s", requests.Count == 0 ? 0d : requests.Max(r => r.ElapsedMs) / 1000d);
            output.Sample("sql.req.active", null, requests.Count, at);
            output.Sample("sql.req.blocked", null, requests.Count(r => r.BlockingSessionId > 0), at);
            output.Payload["requests"] = requests;
        }

        if (chainRows is not null)
        {
            var chains = BuildChains(chainRows);
            var worst = chains.OrderByDescending(c => c.MaxWaitMs).FirstOrDefault();
            output.Fact("sql.blocking.chains", chains.Count);
            output.Fact("sql.blocking.blocked_sessions", chains.Sum(c => c.Blocked.Count));
            output.Fact("sql.blocking.max_wait_s", worst is null ? 0d : worst.MaxWaitMs / 1000d);
            output.Sample("sql.blocking.max_wait_s", null, worst is null ? 0d : worst.MaxWaitMs / 1000d, at);
            if (worst is not null)
            {
                output.Fact("sql.blocking.head_session", worst.Head.SessionId);
                output.Fact("sql.blocking.head_idle", worst.HeadIsIdle);
                output.Fact("sql.blocking.head_summary", worst.Summary);
            }

            output.Payload["blockingChains"] = chains;
        }

        if (idle is not null)
        {
            var oldest = idle.OrderByDescending(i => i.IdleTranAgeS ?? 0).FirstOrDefault();
            output.Fact("sql.idle_open_tran.count", idle.Count);
            output.Fact("sql.idle_open_tran.max_age_s", oldest?.IdleTranAgeS ?? 0);
            if (oldest is not null)
            {
                output.Fact("sql.idle_open_tran.oldest", string.Create(CultureInfo.InvariantCulture,
                    $"session {oldest.SessionId} ({oldest.LoginName}@{oldest.HostName}, {oldest.ProgramName}, {oldest.DatabaseName}): {BlockingChain.Truncate(oldest.LastStatement, 160)}"));
            }

            output.Payload["idleOpenTransactions"] = idle;
        }

        if (transactions is not null)
        {
            var perTransaction = transactions
                .GroupBy(t => (t.SessionId, t.TransactionId))
                .Select(g => (Row: g.First(), AgeS: g.Max(t => t.AgeS ?? 0), LogMb: g.Sum(t => (t.LogBytesUsed ?? 0) + (t.LogBytesReserved ?? 0)) / 1048576d))
                .ToList();
            var oldest = perTransaction.OrderByDescending(t => t.AgeS).FirstOrDefault();
            var largest = perTransaction.OrderByDescending(t => t.LogMb).FirstOrDefault();
            output.Fact("sql.tran.open_count", perTransaction.Count);
            output.Fact("sql.tran.max_age_s", perTransaction.Count == 0 ? 0L : oldest.AgeS);
            output.Fact("sql.tran.max_log_mb", perTransaction.Count == 0 ? 0d : largest.LogMb);
            if (perTransaction.Count > 0)
            {
                var r = oldest.Row;
                output.Fact("sql.tran.oldest", string.Create(CultureInfo.InvariantCulture,
                    $"session {r.SessionId} ({r.LoginName}@{r.HostName}, {r.ProgramName}, {r.SessionStatus}) '{r.TransactionName}' open {oldest.AgeS} s, log {largest.LogMb:0} MB (largest)"));
            }

            output.Sample("sql.tran.max_age_s", null, perTransaction.Count == 0 ? 0 : oldest.AgeS, at);
            output.Payload["transactions"] = transactions;
        }

        if (sessions is not null)
        {
            output.Fact("sql.sessions.user", sessions.Sum(s => s.Sessions));
            output.Sample("sql.sessions.user", null, sessions.Sum(s => s.Sessions), at);
            foreach (var host in sessions.GroupBy(s => s.HostName ?? "?", StringComparer.OrdinalIgnoreCase))
            {
                output.Sample("sql.sessions.by_host", host.Key, host.Sum(s => s.Sessions), at);
            }

            output.Payload["sessionsByHost"] = sessions;
        }
    }

    internal static IReadOnlyList<BlockingChain> BuildChains(IEnumerable<ChainRow> rows) =>
        rows.GroupBy(r => r.HeadSessionId)
            .Select(g =>
            {
                var members = g.Select(ToSession).ToList();
                var head = members.FirstOrDefault(m => m.Level == 0) ?? members[0];
                return new BlockingChain(head, members.Where(m => m.Level > 0).OrderBy(m => m.Level).ThenBy(m => m.SessionId).ToList());
            })
            .Where(c => c.Blocked.Count > 0)
            .OrderByDescending(c => c.MaxWaitMs)
            .ToList();

    private static BlockedSession ToSession(ChainRow r) => new(
        r.SessionId, r.BlockingSessionId, r.ChainLevel, r.LoginName, r.HostName, r.ProgramName, r.DatabaseName,
        r.RequestStatus ?? r.SessionStatus, r.WaitType, r.WaitMs, r.WaitResource, r.OpenTranCount, r.TranAgeS, r.StatementText);

    public sealed class RequestRow
    {
        public int SessionId { get; set; }

        public string? LoginName { get; set; }

        public string? HostName { get; set; }

        public string? ProgramName { get; set; }

        public string? DatabaseName { get; set; }

        public string? Status { get; set; }

        public string? Command { get; set; }

        public string? WaitType { get; set; }

        public long WaitMs { get; set; }

        public string? WaitResource { get; set; }

        public int BlockingSessionId { get; set; }

        public long CpuMs { get; set; }

        public long Reads { get; set; }

        public long Writes { get; set; }

        public long LogicalReads { get; set; }

        public long ElapsedMs { get; set; }

        public long GrantedKb { get; set; }

        public int Dop { get; set; }

        public double PercentComplete { get; set; }

        public int OpenTranCount { get; set; }

        public DateTime? StartTime { get; set; }

        public byte[]? PlanHandle { get; set; }

        public int StatementStart { get; set; }

        public int StatementEnd { get; set; }

        public string? StatementText { get; set; }
    }

    public sealed class ChainRow
    {
        public int HeadSessionId { get; set; }

        public int SessionId { get; set; }

        public int BlockingSessionId { get; set; }

        public int ChainLevel { get; set; }

        public string? LoginName { get; set; }

        public string? HostName { get; set; }

        public string? ProgramName { get; set; }

        public string? DatabaseName { get; set; }

        public string? SessionStatus { get; set; }

        public string? RequestStatus { get; set; }

        public string? Command { get; set; }

        public string? WaitType { get; set; }

        public long WaitMs { get; set; }

        public string? WaitResource { get; set; }

        public int OpenTranCount { get; set; }

        public DateTime? TranBegin { get; set; }

        public DateTime? LastRequestEnd { get; set; }

        public long? TranAgeS { get; set; }

        public string? StatementText { get; set; }
    }

    public sealed class IdleTranRow
    {
        public int SessionId { get; set; }

        public string? LoginName { get; set; }

        public string? HostName { get; set; }

        public string? ProgramName { get; set; }

        public string? DatabaseName { get; set; }

        public int OpenTranCount { get; set; }

        public DateTime? LastRequestEnd { get; set; }

        public DateTime? TranBegin { get; set; }

        public long? IdleTranAgeS { get; set; }

        public string? LastStatement { get; set; }
    }

    public sealed class TranRow
    {
        public int SessionId { get; set; }

        public string? LoginName { get; set; }

        public string? HostName { get; set; }

        public string? ProgramName { get; set; }

        public string? SessionStatus { get; set; }

        public long TransactionId { get; set; }

        public string? TransactionName { get; set; }

        public DateTime? BeginTime { get; set; }

        public long? AgeS { get; set; }

        public int TransactionType { get; set; }

        public int TransactionState { get; set; }

        public string? DatabaseName { get; set; }

        public long? LogBytesUsed { get; set; }

        public long? LogBytesReserved { get; set; }
    }

    public sealed class SessionGroupRow
    {
        public string? HostName { get; set; }

        public string? ProgramName { get; set; }

        public string? LoginName { get; set; }

        public string? DatabaseName { get; set; }

        public int Sessions { get; set; }

        public int Running { get; set; }

        public int WithOpenTran { get; set; }
    }
}
