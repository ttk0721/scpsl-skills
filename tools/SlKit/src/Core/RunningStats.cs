namespace SlKit.Core;

/// <summary>
/// Running count / last / max / total of durations measured in Stopwatch ticks. O(1) memory,
/// allocation-free.
/// </summary>
/// <remarks>
/// Used to make a plugin's own per-job cost observable (last, average and worst run) without
/// keeping per-run history. Mutable struct: keep it in a field. Thread affinity: none (pure),
/// instance not thread-safe.
/// </remarks>
public struct RunningStats
{
    private long _count;
    private long _last;
    private long _max;
    private long _total;

    /// <summary>Number of recorded durations.</summary>
    public long Count => _count;

    /// <summary>Most recent duration in Stopwatch ticks.</summary>
    public long LastTicks => _last;

    /// <summary>Largest duration in Stopwatch ticks.</summary>
    public long MaxTicks => _max;

    /// <summary>Sum of all durations in Stopwatch ticks.</summary>
    public long TotalTicks => _total;

    /// <summary>Most recent duration in microseconds.</summary>
    public double LastMicroseconds => StopwatchTime.ToMicroseconds(_last);

    /// <summary>Largest duration in microseconds.</summary>
    public double MaxMicroseconds => StopwatchTime.ToMicroseconds(_max);

    /// <summary>Mean duration in microseconds (0 when nothing was recorded).</summary>
    public double AverageMicroseconds => _count == 0 ? 0 : StopwatchTime.ToMicroseconds(_total) / _count;

    /// <summary>Records one duration.</summary>
    /// <param name="ticks">Duration in Stopwatch ticks.</param>
    public void Add(long ticks)
    {
        _count++;
        _last = ticks;
        _total += ticks;
        if (ticks > _max)
        {
            _max = ticks;
        }
    }

    /// <summary>Forgets everything recorded so far.</summary>
    public void Reset() => this = default;
}
