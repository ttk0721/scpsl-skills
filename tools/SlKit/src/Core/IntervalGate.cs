using System;
using System.Threading;

namespace SlKit.Core;

/// <summary>
/// Lets one caller through per interval, from any number of threads: the first call ever passes,
/// later calls pass once <see cref="Interval"/> has elapsed since the last pass.
/// </summary>
/// <remarks>
/// Used for warnings that are raised from many threads at once (for example
/// <c>MainThread.Post</c> shedding actions from the worker, the thread pool and the main thread
/// together): the first occurrence must be visible immediately, and a sustained condition must be
/// reported periodically, but never as a log storm. A token bucket would need a lock here; this is
/// one compare-and-swap on the rare path and allocates nothing.
/// The caller passes "now", which keeps the type pure and testable.
/// Thread affinity: every member is thread-safe.
/// </remarks>
public sealed class IntervalGate
{
    private long _nextAllowed = long.MinValue;
    private long _blocked;

    /// <summary>Creates a gate that is open for the first call.</summary>
    /// <param name="interval">Minimum distance between passes, in Stopwatch ticks; must not be negative.</param>
    public IntervalGate(long interval)
    {
        if (interval < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(interval), "Interval must not be negative.");
        }

        Interval = interval;
    }

    /// <summary>Minimum distance between passes, in Stopwatch ticks.</summary>
    public long Interval { get; }

    /// <summary>Calls refused since the last successful <see cref="TryPass"/> (approximate under concurrency).</summary>
    public long BlockedSinceLastPass => Interlocked.Read(ref _blocked);

    /// <summary>
    /// Returns true for exactly one caller per interval. When it does, <paramref name="blockedBefore"/>
    /// is the number of calls refused since the previous pass, so the message can say "N more since".
    /// </summary>
    /// <param name="now">Current Stopwatch timestamp.</param>
    /// <param name="blockedBefore">Calls refused since the previous pass; 0 when refused.</param>
    /// <returns>True when this caller should act (log, report).</returns>
    public bool TryPass(long now, out long blockedBefore)
    {
        long next = Interlocked.Read(ref _nextAllowed);
        if (now >= next)
        {
            long newNext = AddSaturating(now, Interval);
            if (Interlocked.CompareExchange(ref _nextAllowed, newNext, next) == next)
            {
                blockedBefore = Interlocked.Exchange(ref _blocked, 0);
                return true;
            }
        }

        Interlocked.Increment(ref _blocked);
        blockedBefore = 0;
        return false;
    }

    private static long AddSaturating(long a, long b)
    {
        long sum = a + b;

        // b is never negative, so only an overflow past MaxValue can wrap.
        return sum < a ? long.MaxValue : sum;
    }
}
