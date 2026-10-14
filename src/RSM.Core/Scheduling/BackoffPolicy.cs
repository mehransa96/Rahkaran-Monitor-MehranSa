namespace RSM.Core.Scheduling;

/// <summary>
/// Adaptive back-off for one (node, collector) pair (ARCHITECTURE.md §5): if a run exceeds 2× its budget
/// three times in a row the interval doubles (up to 8×); after ten fast runs it steps back down.
/// </summary>
public sealed class BackoffPolicy
{
    private readonly TimeSpan _baseInterval;
    private readonly TimeSpan _budget;
    private int _slowStreak;
    private int _fastStreak;

    public BackoffPolicy(TimeSpan baseInterval, TimeSpan budget, int maxMultiplier = 8, int slowRunsToBackOff = 3, int fastRunsToRecover = 10)
    {
        if (baseInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(baseInterval));
        }

        _baseInterval = baseInterval;
        _budget = budget;
        MaxMultiplier = maxMultiplier;
        SlowRunsToBackOff = slowRunsToBackOff;
        FastRunsToRecover = fastRunsToRecover;
    }

    public int MaxMultiplier { get; }

    public int SlowRunsToBackOff { get; }

    public int FastRunsToRecover { get; }

    public int Multiplier { get; private set; } = 1;

    public TimeSpan CurrentInterval => _baseInterval * Multiplier;

    public bool IsBackedOff => Multiplier > 1;

    /// <summary>Records a run duration and returns the interval to wait before the next run.</summary>
    public TimeSpan Record(TimeSpan duration)
    {
        if (duration > _budget * 2)
        {
            _fastStreak = 0;
            if (++_slowStreak >= SlowRunsToBackOff)
            {
                _slowStreak = 0;
                Multiplier = Math.Min(Multiplier * 2, MaxMultiplier);
            }
        }
        else
        {
            _slowStreak = 0;
            if (Multiplier > 1 && ++_fastStreak >= FastRunsToRecover)
            {
                _fastStreak = 0;
                Multiplier /= 2;
            }
        }

        return CurrentInterval;
    }
}
