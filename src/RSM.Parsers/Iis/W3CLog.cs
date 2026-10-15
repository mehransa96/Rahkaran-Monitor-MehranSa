using System.Globalization;
using RSM.Core.Model;

namespace RSM.Parsers.Iis;

/// <summary>One W3C log line (fields that RSM uses; missing fields are null).</summary>
public sealed record W3CRecord(
    DateTimeOffset AtUtc,
    string? Method,
    string UriStem,
    int Status,
    int SubStatus,
    long Win32Status,
    int? TimeTakenMs,
    string? ClientIp,
    string? ForwardedFor,
    int? ServerPort);

/// <summary>Latency of one URL stem (nearest-rank percentiles of time-taken, in ms).</summary>
public sealed record UrlStemStats(string UriStem, long Requests, int P50Ms, int P95Ms, int P99Ms, int MaxMs, long Errors5xx, bool Sampled);

public sealed record StatusCount(string Status, long Count, LocalizedText? Meaning);

public sealed record MinuteBucket(DateTimeOffset Minute, long Requests, long Errors5xx, long Status503);

public sealed record ClientCount(string Client, long Requests);

/// <summary>Result of analysing one or more W3C log files over a time range.</summary>
public sealed record W3CSummary(
    DateTimeOffset? FromUtc,
    DateTimeOffset? ToUtc,
    long Requests,
    long Errors4xx,
    long Errors5xx,
    long ClientDisconnects,
    bool HasTimeTaken,
    IReadOnlyList<UrlStemStats> UrlStems,
    IReadOnlyList<StatusCount> Statuses,
    IReadOnlyList<MinuteBucket> Minutes,
    IReadOnlyList<ClientCount> TopClients,
    IReadOnlyList<MinuteBucket> Bursts,
    long LinesSkipped)
{
    public double ErrorRate5xx => Requests == 0 ? 0 : 100.0 * Errors5xx / Requests;

    /// <summary>Overall percentile across all URL stems is not stored; p95 of the busiest stems is shown per stem.</summary>
    public UrlStemStats? Slowest => UrlStems.OrderByDescending(u => u.P95Ms).FirstOrDefault();
}

/// <summary>
/// Streaming W3C extended-log analyser (spec §10.5): read-only, line by line, honours <c>#Fields:</c> changes (IIS
/// rewrites the header on restart). Percentiles are nearest-rank over every time-taken value of a stem; past
/// <see cref="MaxValuesPerStem"/> values a stem switches to reservoir sampling and is marked <c>Sampled</c>.
/// IIS writes W3C times in UTC.
/// </summary>
public sealed class W3CAnalyzer(DateTimeOffset? fromUtc = null, DateTimeOffset? toUtc = null)
{
    public const int MaxValuesPerStem = 200_000;
    public const int BurstMin5xx = 10;

    private readonly Dictionary<string, StemAccumulator> _stems = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> _statuses = new(StringComparer.Ordinal);
    private readonly Dictionary<DateTimeOffset, (long Requests, long E5xx, long S503)> _minutes = [];
    private readonly Dictionary<string, long> _clients = new(StringComparer.OrdinalIgnoreCase);
    private string[] _fields = [];
    private long _requests, _e4xx, _e5xx, _disconnects, _skipped;
    private bool _hasTimeTaken;
    private DateTimeOffset? _first, _last;

    public void AddLine(string line)
    {
        if (line.Length == 0)
        {
            return;
        }

        if (line[0] == '#')
        {
            if (line.StartsWith("#Fields:", StringComparison.Ordinal))
            {
                _fields = line[8..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                _hasTimeTaken |= _fields.Contains("time-taken", StringComparer.OrdinalIgnoreCase);
            }

            return;
        }

        if (Parse(line, _fields) is not { } record)
        {
            _skipped++;
            return;
        }

        Add(record);
    }

    public void Add(W3CRecord r)
    {
        if ((fromUtc is { } from && r.AtUtc < from) || (toUtc is { } to && r.AtUtc > to))
        {
            return;
        }

        _requests++;
        _first = _first is null || r.AtUtc < _first ? r.AtUtc : _first;
        _last = _last is null || r.AtUtc > _last ? r.AtUtc : _last;
        var is5xx = r.Status >= 500;
        if (r.Status is >= 400 and < 500)
        {
            _e4xx++;
        }

        if (is5xx)
        {
            _e5xx++;
        }

        if (r.Win32Status is 64 or 1236)
        {
            _disconnects++;
        }

        var key = r.SubStatus > 0 ? $"{r.Status}.{r.SubStatus}" : r.Status.ToString(CultureInfo.InvariantCulture);
        _statuses[key] = _statuses.GetValueOrDefault(key) + 1;

        var minute = new DateTimeOffset(r.AtUtc.Year, r.AtUtc.Month, r.AtUtc.Day, r.AtUtc.Hour, r.AtUtc.Minute, 0, TimeSpan.Zero);
        var m = _minutes.GetValueOrDefault(minute);
        _minutes[minute] = (m.Requests + 1, m.E5xx + (is5xx ? 1 : 0), m.S503 + (r.Status == 503 ? 1 : 0));

        var client = r.ForwardedFor is { Length: > 0 } fwd ? fwd.Split(',')[0].Trim() : r.ClientIp ?? "?";
        _clients[client] = _clients.GetValueOrDefault(client) + 1;

        if (!_stems.TryGetValue(r.UriStem, out var stem))
        {
            _stems[r.UriStem] = stem = new StemAccumulator(r.UriStem);
        }

        stem.Add(r.TimeTakenMs, is5xx);
    }

    public W3CSummary Summarize(int topStems = 50, int topClients = 20)
    {
        var bursts = _minutes.Where(m => m.Value.E5xx >= BurstMin5xx).OrderBy(m => m.Key)
            .Select(m => new MinuteBucket(m.Key, m.Value.Requests, m.Value.E5xx, m.Value.S503)).ToList();
        return new W3CSummary(
            _first,
            _last,
            _requests,
            _e4xx,
            _e5xx,
            _disconnects,
            _hasTimeTaken,
            _stems.Values.OrderByDescending(s => s.Count).Take(topStems).Select(s => s.ToStats()).OrderByDescending(s => s.P95Ms).ToList(),
            _statuses.OrderByDescending(s => s.Value).Select(s => new StatusCount(s.Key, s.Value, StatusMeaning(s.Key))).ToList(),
            _minutes.OrderBy(m => m.Key).Select(m => new MinuteBucket(m.Key, m.Value.Requests, m.Value.E5xx, m.Value.S503)).ToList(),
            _clients.OrderByDescending(c => c.Value).Take(topClients).Select(c => new ClientCount(c.Key, c.Value)).ToList(),
            bursts,
            _skipped);
    }

    /// <summary>Parses one data line with the current <c>#Fields</c>; returns null for malformed lines.</summary>
    public static W3CRecord? Parse(string line, IReadOnlyList<string> fields)
    {
        if (fields.Count == 0)
        {
            return null;
        }

        var values = line.Split(' ');
        if (values.Length != fields.Count)
        {
            return null;
        }

        string? Get(string name)
        {
            for (var i = 0; i < fields.Count; i++)
            {
                if (string.Equals(fields[i], name, StringComparison.OrdinalIgnoreCase))
                {
                    return values[i] == "-" ? null : values[i];
                }
            }

            return null;
        }

        if (!DateTime.TryParseExact($"{Get("date")} {Get("time")}", "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var at)
            || !int.TryParse(Get("sc-status"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var status))
        {
            return null;
        }

        return new W3CRecord(
            new DateTimeOffset(at, TimeSpan.Zero),
            Get("cs-method"),
            Get("cs-uri-stem") ?? "/",
            status,
            int.TryParse(Get("sc-substatus"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var sub) ? sub : 0,
            long.TryParse(Get("sc-win32-status"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var w32) ? w32 : 0,
            int.TryParse(Get("time-taken"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var ms) ? ms : null,
            Get("c-ip"),
            Get("X-Forwarded-For") ?? Get("cs(X-Forwarded-For)"),
            int.TryParse(Get("s-port"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var port) ? port : null);
    }

    /// <summary>Nearest-rank percentile: the smallest value with at least p % of the values ≤ it.</summary>
    public static int Percentile(IReadOnlyList<int> sorted, double p)
    {
        if (sorted.Count == 0)
        {
            return 0;
        }

        var rank = (int)Math.Ceiling(p / 100.0 * sorted.Count);
        return sorted[Math.Clamp(rank, 1, sorted.Count) - 1];
    }

    /// <summary>What an IIS / ASP.NET Core Module status means (spec §10.5 special decoding).</summary>
    public static LocalizedText? StatusMeaning(string status) => status switch
    {
        "503" or "503.0" => new("سرویس در دسترس نیست: Application Pool متوقف یا صف HTTP.sys پر است (HTTPERR را ببینید).", "Service unavailable: the app pool is stopped or the HTTP.sys queue is full (see HTTPERR)."),
        "503.2" => new("حد درخواست‌های هم‌زمان (appConcurrentRequestLimit) پر شده است.", "Concurrent request limit (appConcurrentRequestLimit) reached."),
        "502.3" => new("ANCM: خطای اتصال یا Timeout در ارتباط با فرایند برنامه (Out-of-process).", "ANCM: connection error or timeout talking to the app process (out-of-process)."),
        "502.5" => new("ANCM: فرایند برنامه اجرا نشد (Process failure) — stdout و Event Log را ببینید.", "ANCM: the app process failed to start — check stdout and the event log."),
        "500.30" => new("ANCM: برنامه در حالت In-process هنگام شروع خطا داد.", "ANCM: the in-process app failed to start."),
        "500.31" => new("ANCM: Runtime مورد نیاز .NET پیدا نشد.", "ANCM: the required .NET runtime was not found."),
        "500.32" => new("ANCM: معماری (۳۲/۶۴ بیت) DLL با Application Pool سازگار نیست.", "ANCM: the app DLL bitness does not match the app pool."),
        "500.33" => new("ANCM: برنامه برای In-process ساخته نشده است.", "ANCM: the app is not built for in-process hosting."),
        "500.34" => new("ANCM: مدل میزبانی (Hosting model) متناقض است.", "ANCM: mixed hosting models."),
        "500.35" => new("ANCM: چند برنامهٔ In-process در یک Application Pool.", "ANCM: multiple in-process apps in the same app pool."),
        "500.36" => new("ANCM: Handler خارج از فرایند بارگذاری نشد.", "ANCM: the out-of-process handler failed to load."),
        "500.37" => new("ANCM: برنامه در زمان مجاز (startupTimeLimit) شروع نشد.", "ANCM: the app did not start within startupTimeLimit."),
        "500.19" => new("پیکربندی (web.config) نامعتبر است یا خوانده نمی‌شود.", "Invalid or unreadable configuration (web.config)."),
        "500" or "500.0" => new("خطای داخلی برنامه.", "Internal application error."),
        _ => null,
    };

    private sealed class StemAccumulator(string stem)
    {
        private readonly List<int> _values = [];
        private readonly Random _random = new(stem.GetHashCode(StringComparison.Ordinal));
        private long _seen;
        private int _max;

        public long Count { get; private set; }

        public long Errors { get; private set; }

        public bool Sampled => _seen > MaxValuesPerStem;

        public void Add(int? ms, bool error)
        {
            Count++;
            if (error)
            {
                Errors++;
            }

            if (ms is not { } v)
            {
                return;
            }

            _max = Math.Max(_max, v);
            _seen++;
            if (_values.Count < MaxValuesPerStem)
            {
                _values.Add(v);
            }
            else
            {
                // Reservoir sampling keeps memory bounded on very large logs.
                var j = _random.NextInt64(_seen);
                if (j < MaxValuesPerStem)
                {
                    _values[(int)j] = v;
                }
            }
        }

        public UrlStemStats ToStats()
        {
            var sorted = _values.Order().ToList();
            return new UrlStemStats(stem, Count, Percentile(sorted, 50), Percentile(sorted, 95), Percentile(sorted, 99), _max, Errors, Sampled);
        }
    }
}
