namespace RSM.Core.Model;

/// <summary>What a live collector produces in one tick: facts for rules, samples for charts, events, payload rows.</summary>
public sealed class LiveOutput
{
    public List<Fact> Facts { get; } = [];

    public List<MetricSample> Samples { get; } = [];

    public List<UnifiedEvent> Events { get; } = [];

    public Dictionary<string, object?> Payload { get; } = new(StringComparer.Ordinal);

    public void Fact(string key, object? value)
    {
        if (value is not null)
        {
            Facts.Add(new Fact(key, value));
        }
    }

    public void Sample(string key, string? instance, double? value, DateTimeOffset atUtc)
    {
        if (value is { } v && double.IsFinite(v))
        {
            Samples.Add(new MetricSample(key, instance, v, atUtc));
        }
    }
}

/// <summary>Small helpers for windows of recent values ("sustained for N samples").</summary>
public static class Window
{
    /// <summary>Min of the last <paramref name="count"/> values, or null when fewer values exist.</summary>
    public static double? MinOfLast(IReadOnlyList<double> values, int count) =>
        values.Count >= count ? values.Skip(values.Count - count).Min() : null;

    /// <summary>Max of the last <paramref name="count"/> values, or null when fewer values exist.</summary>
    public static double? MaxOfLast(IReadOnlyList<double> values, int count) =>
        values.Count >= count ? values.Skip(values.Count - count).Max() : null;

    public static void Push<T>(List<T> list, T value, int capacity)
    {
        list.Add(value);
        if (list.Count > capacity)
        {
            list.RemoveRange(0, list.Count - capacity);
        }
    }
}
