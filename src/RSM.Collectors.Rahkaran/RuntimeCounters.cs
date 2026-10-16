using System.Collections.Concurrent;
using Microsoft.Diagnostics.Tracing;
using RSM.Safety.DotNet;

namespace RSM.Collectors.Rahkaran;

/// <summary>One EventCounter value: the mean of a gauge, or the increment of a rate counter over the interval.</summary>
public sealed record RuntimeCounterValue(string Provider, string Name, double Value, bool IsRate, string? Units, double IntervalSeconds);

/// <summary>Source of counter values for one local process (EventPipe in production, a fake in tests).</summary>
public interface IRuntimeCounterProvider
{
    IReadOnlyList<int> PublishedProcesses();

    Task<IReadOnlyDictionary<string, RuntimeCounterValue>> ReadAsync(int processId, TimeSpan window, CancellationToken cancellationToken);
}

/// <summary>
/// Reads EventCounters through <see cref="RuntimeCounterSource"/> (counters-only session) and parses the nettrace
/// stream with TraceEvent. The session lasts <c>window</c>; the last value of every counter is kept.
/// </summary>
public sealed class EventPipeCounterProvider : IRuntimeCounterProvider
{
    public const int IntervalSeconds = 1;

    public IReadOnlyList<int> PublishedProcesses() => RuntimeCounterSource.PublishedProcesses().Where(p => p != Environment.ProcessId).ToList();

    public async Task<IReadOnlyDictionary<string, RuntimeCounterValue>> ReadAsync(int processId, TimeSpan window, CancellationToken cancellationToken)
    {
        var values = new ConcurrentDictionary<string, RuntimeCounterValue>(StringComparer.Ordinal);
        using var session = RuntimeCounterSource.Start(processId, IntervalSeconds);
        var processing = Task.Run(() =>
        {
            using var source = new EventPipeEventSource(session.EventStream);
            source.Dynamic.All += e =>
            {
                if (Parse(e) is { } value)
                {
                    values[$"{value.Provider}/{value.Name}"] = value;
                }
            };
            source.Process();
        }, CancellationToken.None);

        try
        {
            await Task.Delay(window, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            session.Stop();
            await Task.WhenAny(processing, Task.Delay(TimeSpan.FromSeconds(5), CancellationToken.None)).ConfigureAwait(false);
        }

        return values;
    }

    /// <summary>EventCounters payload: { Name, Mean | Increment, CounterType ("Mean" | "Sum"), DisplayUnits, IntervalSec }.</summary>
    internal static RuntimeCounterValue? Parse(TraceEvent e)
    {
        if (e.EventName != "EventCounters" || e.PayloadValue(0) is not IDictionary<string, object> outer || Get(outer, "Payload") is not IDictionary<string, object> payload)
        {
            return null;
        }

        return FromPayload(e.ProviderName, payload);
    }

    private static object? Get(IDictionary<string, object> d, string key) => d.TryGetValue(key, out var v) ? v : null;

    internal static RuntimeCounterValue? FromPayload(string provider, IDictionary<string, object> payload)
    {
        if (Get(payload, "Name") is not string name)
        {
            return null;
        }

        var isRate = string.Equals(Get(payload, "CounterType") as string, "Sum", StringComparison.Ordinal);
        var raw = Get(payload, isRate ? "Increment" : "Mean");
        var interval = Get(payload, "IntervalSec") is { } i ? Convert.ToDouble(i, System.Globalization.CultureInfo.InvariantCulture) : IntervalSeconds;
        return raw is null ? null : new RuntimeCounterValue(provider, name, Convert.ToDouble(raw, System.Globalization.CultureInfo.InvariantCulture), isRate,
            Get(payload, "DisplayUnits") as string, interval <= 0 ? IntervalSeconds : interval);
    }
}
