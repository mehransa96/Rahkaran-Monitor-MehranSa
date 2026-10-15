using System.Globalization;
using System.Text.RegularExpressions;
using RSM.Core.Model;
using RSM.Safety.Sql;

namespace RSM.Collectors.Sql.Instance;

/// <summary>
/// §7.16 SQL Agent: jobs, failures, running jobs vs history, shrink steps, UNC backup destinations, maintenance plans,
/// CDC job registration. ستاد فنی tool job detection uses site-configured name patterns (unverified until set).
/// </summary>
public sealed partial class SqlAgentCollector(SafeSqlExecutor executor) : SqlCollectorBase(executor)
{
    public const int HistoryDays = 30;

    public override string Id => "sql.agent";

    protected override async Task<IReadOnlyDictionary<string, object?>> CollectCoreAsync(INodeContext node, CollectContext context, SqlQueryRunner runner, List<Fact> facts, List<UnifiedEvent> events)
    {
        var now = DateTime.Now;
        var sinceDate = int.Parse(now.AddDays(-HistoryDays).ToString("yyyyMMdd", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
        var jobRows = await runner.QueryAsync<JobRow>("sql.agent.jobs").ConfigureAwait(false);
        var steps = await runner.QueryAsync<StepRow>("sql.agent.job_steps").ConfigureAwait(false);
        var history = await runner.QueryAsync<HistoryRow>("sql.agent.history", new { SinceDate = sinceDate }).ConfigureAwait(false);
        var running = await runner.QueryAsync<RunningRow>("sql.agent.running").ConfigureAwait(false);
        var plans = await runner.QueryAsync<MaintPlanRow>("sql.agent.maint_plans").ConfigureAwait(false);
        var cdc = await runner.QueryAsync<CdcJobRow>("sql.agent.cdc_jobs").ConfigureAwait(false);

        var names = jobRows?.GroupBy(j => j.JobId).ToDictionary(g => g.Key, g => g.First().Name ?? g.Key.ToString()) ?? [];
        if (jobRows is not null)
        {
            Add(facts, "agent.jobs.count", names.Count);
            Add(facts, "agent.jobs.enabled_count", jobRows.Where(j => j.Enabled == 1).Select(j => j.JobId).Distinct().Count());
        }

        if (history is not null)
        {
            var weekAgo = now.AddDays(-7);
            var failed = history.Where(h => h.StepId == 0 && h.RunStatus == 0 && h.RunAt >= weekAgo).Select(h => h.JobId).Distinct().ToList();
            Add(facts, "agent.failed_jobs_7d", failed.Count);
            Add(facts, "agent.failed_job_names", failed.Select(id => (object?)names.GetValueOrDefault(id, id.ToString())).ToList());
            Add(facts, "agent.history_days", HistoryDays);

            if (running is not null)
            {
                var longRunning = new List<object?>();
                foreach (var r in running.Where(r => r.StartExecutionDate is not null))
                {
                    var durations = history.Where(h => h.JobId == r.JobId && h.StepId == 0 && h.RunStatus == 1).Select(h => h.DurationSeconds).Order().ToList();
                    if (durations.Count >= 5)
                    {
                        var p95 = durations[(int)Math.Ceiling(durations.Count * 0.95) - 1];
                        if ((now - r.StartExecutionDate!.Value).TotalSeconds > Math.Max(60, p95 * 2))
                        {
                            longRunning.Add(names.GetValueOrDefault(r.JobId, r.JobId.ToString()));
                        }
                    }
                }

                Add(facts, "agent.long_running_jobs", longRunning.Count);
                Add(facts, "agent.long_running_job_names", longRunning);
            }
        }

        Add(facts, "agent.running_jobs", running?.Count);

        if (steps is not null)
        {
            var shrink = steps.Where(s => s.Subsystem == "TSQL" && IsDataShrink(s.Command)).ToList();
            Add(facts, "agent.shrink_steps", shrink.Count);
            Add(facts, "agent.shrink_job_names", shrink.Select(s => (object?)names.GetValueOrDefault(s.JobId, s.JobId.ToString())).Distinct().ToList());
            var unc = steps.Where(s => s.Command is not null && UncBackup().IsMatch(s.Command)).ToList();
            Add(facts, "agent.unc_backup_steps", unc.Count);
            Add(facts, "agent.unc_backup_job_names", unc.Select(s => (object?)names.GetValueOrDefault(s.JobId, s.JobId.ToString())).Distinct().ToList());
        }

        if (plans is not null)
        {
            var planNames = plans.Select(p => p.PlanName).Distinct().ToList();
            Add(facts, "agent.maint_plan_count", planNames.Count);
            Add(facts, "agent.maint_plan_names", planNames.Select(n => (object?)n).ToList());
            Add(facts, "agent.maint_plan_mentions_backup", plans.Any(p => (p.PlanName + " " + p.SubplanName).Contains("backup", StringComparison.OrdinalIgnoreCase)));
        }

        // Unverified, product-specific: ستاد فنی database tool job names come from site settings only.
        var patterns = node.Site.Settings.StadFanniToolJobPatterns;
        if (patterns.Count > 0 && jobRows is not null)
        {
            var matches = names.Values.Where(n => patterns.Any(p => Regex.IsMatch(n, p, RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100)))).ToList();
            Add(facts, "agent.stadfanni_tool_jobs", matches.Count);
        }

        if (cdc is not null)
        {
            var runningIds = running?.Select(r => r.JobId).ToHashSet() ?? [];
            Add(facts, "agent.cdc_capture_db_ids", cdc.Where(c => c.JobType == "capture").Select(c => (object?)c.DatabaseId).ToList());
            Add(facts, "agent.cdc_cleanup_db_ids", cdc.Where(c => c.JobType == "cleanup").Select(c => (object?)c.DatabaseId).ToList());
            Add(facts, "agent.cdc_capture_running_db_ids", cdc.Where(c => c.JobType == "capture" && c.JobId is { } id && runningIds.Contains(id)).Select(c => (object?)c.DatabaseId).ToList());
        }

        return new Dictionary<string, object?>
        {
            ["jobs"] = jobRows,
            ["steps"] = steps?.Select(s => new { s.JobId, s.StepId, s.StepName, s.Subsystem, s.DatabaseName }).ToList(), // commands stay out of reports
            ["failures"] = history?.Where(h => h.RunStatus == 0 && h.StepId == 0).Take(200).ToList(),
            ["running"] = running,
            ["maintenancePlans"] = plans,
            ["cdcJobs"] = cdc,
        };
    }

    /// <summary>SHRINKDATABASE always; SHRINKFILE unless the file name looks like a log file (spec: shrink of data files).</summary>
    internal static bool IsDataShrink(string? command)
    {
        if (string.IsNullOrEmpty(command))
        {
            return false;
        }

        if (ShrinkDatabase().IsMatch(command))
        {
            return true;
        }

        return ShrinkFile().Matches(command).Any(m => !m.Groups["file"].Value.Contains("log", StringComparison.OrdinalIgnoreCase));
    }

    [GeneratedRegex(@"\bSHRINKDATABASE\b", RegexOptions.IgnoreCase)]
    private static partial Regex ShrinkDatabase();

    [GeneratedRegex(@"\bSHRINKFILE\s*\(\s*N?'?(?<file>[^,')]+)", RegexOptions.IgnoreCase)]
    private static partial Regex ShrinkFile();

    [GeneratedRegex(@"\bBACKUP\s+(DATABASE|LOG)\b[\s\S]*?\bTO\s+DISK\s*=\s*N?'\\\\", RegexOptions.IgnoreCase)]
    private static partial Regex UncBackup();

    public sealed class JobRow
    {
        public Guid JobId { get; set; }

        public string? Name { get; set; }

        public int Enabled { get; set; }

        public string? Owner { get; set; }

        public string? Category { get; set; }

        public string? ScheduleName { get; set; }

        public int? ScheduleEnabled { get; set; }

        public int? FreqType { get; set; }

        public int? FreqInterval { get; set; }

        public int? FreqSubdayType { get; set; }

        public int? FreqSubdayInterval { get; set; }

        public int? ActiveStartTime { get; set; }
    }

    public sealed class StepRow
    {
        public Guid JobId { get; set; }

        public int StepId { get; set; }

        public string? StepName { get; set; }

        public string? Subsystem { get; set; }

        public string? DatabaseName { get; set; }

        public string? Command { get; set; }
    }

    public sealed class HistoryRow
    {
        public Guid JobId { get; set; }

        public int StepId { get; set; }

        public int RunStatus { get; set; }

        public int RunDate { get; set; }

        public int RunTime { get; set; }

        public int RunDuration { get; set; }

        public string? Message { get; set; }

        /// <summary>run_date yyyymmdd + run_time hhmmss.</summary>
        public DateTime RunAt => DateTime.TryParseExact($"{RunDate:00000000}{RunTime:000000}", "yyyyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t : DateTime.MinValue;

        /// <summary>run_duration is encoded as hhmmss.</summary>
        public int DurationSeconds => (RunDuration / 10000 * 3600) + (RunDuration / 100 % 100 * 60) + (RunDuration % 100);
    }

    public sealed class RunningRow
    {
        public Guid JobId { get; set; }

        public DateTime? StartExecutionDate { get; set; }
    }

    public sealed class MaintPlanRow
    {
        public Guid PlanId { get; set; }

        public string? PlanName { get; set; }

        public Guid? SubplanId { get; set; }

        public string? SubplanName { get; set; }

        public Guid? JobId { get; set; }
    }

    public sealed class CdcJobRow
    {
        public int? DatabaseId { get; set; }

        public string? JobType { get; set; }

        public Guid? JobId { get; set; }
    }
}
