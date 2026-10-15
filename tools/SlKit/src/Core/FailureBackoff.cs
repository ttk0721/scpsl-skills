using System;

namespace SlKit.Core;

/// <summary>
/// Consecutive-failure bookkeeping for one scheduled job: counts failures, disables the job after
/// a threshold, and computes when it may run again.
/// </summary>
/// <remarks>
/// The delay after the k-th disable (counted since the last success) is
/// <c>min(baseDelay * 2^(k-1), maxDelay)</c>: 30 s, 1 min, 2 min, ... up to 30 min with the
/// defaults SlClock uses. A success resets both counters, so a job that recovers starts from the
/// short delay again next time.
/// Mutable struct, held in a field of its job. The caller passes "now", so the type is pure.
/// Thread affinity: none (pure), but an instance is NOT thread-safe; SlClock uses it on the main
/// thread only.
/// </remarks>
public struct FailureBackoff
{
    /// <summary>Sentinel for <see cref="ReenableAt"/> while no automatic re-enable is scheduled.</summary>
    public const long Never = long.MaxValue;

    private long _reenableAt;

    /// <summary>Failures since the last success or re-enable.</summary>
    public int ConsecutiveFailures { get; private set; }

    /// <summary>Times the job was disabled since its last successful run.</summary>
    public int DisableStreak { get; private set; }

    /// <summary>Times the job was disabled since it was registered.</summary>
    public long TimesDisabled { get; private set; }

    /// <summary>True while the job is disabled.</summary>
    public bool IsDisabled { get; private set; }

    /// <summary>Stopwatch timestamp of the automatic re-enable, or <see cref="Never"/>.</summary>
    public long ReenableAt => IsDisabled ? _reenableAt : Never;

    /// <summary>Delay applied by the most recent disable, in Stopwatch ticks (0 for a permanent one).</summary>
    public long LastDelay { get; private set; }

    /// <summary>
    /// Delay before the <paramref name="streak"/>-th automatic re-enable:
    /// <c>min(baseDelay * 2^(streak-1), maxDelay)</c>, without overflow.
    /// </summary>
    /// <param name="streak">1 for the first disable since the last success, 2 for the second, ...</param>
    /// <param name="baseDelay">First delay, in Stopwatch ticks; at least 1.</param>
    /// <param name="maxDelay">Upper bound, in Stopwatch ticks; raised to <paramref name="baseDelay"/> if smaller.</param>
    /// <returns>The delay in Stopwatch ticks.</returns>
    public static long DelayFor(int streak, long baseDelay, long maxDelay)
    {
        if (baseDelay < 1)
        {
            baseDelay = 1;
        }

        if (maxDelay < baseDelay)
        {
            maxDelay = baseDelay;
        }

        long delay = baseDelay;
        for (int i = 1; i < streak; i++)
        {
            // Doubling stops at the cap, so the loop cannot overflow however long the streak is.
            if (delay >= maxDelay / 2)
            {
                return maxDelay;
            }

            delay *= 2;
        }

        return Math.Min(delay, maxDelay);
    }

    /// <summary>Records a successful run: clears the failure count and the disable streak.</summary>
    public void RecordSuccess()
    {
        ConsecutiveFailures = 0;
        DisableStreak = 0;
    }

    /// <summary>
    /// Records a failed run. On the <paramref name="maxConsecutiveFailures"/>-th failure in a row the
    /// job becomes disabled, either until <see cref="ReenableAt"/> or permanently.
    /// </summary>
    /// <param name="now">Current Stopwatch timestamp.</param>
    /// <param name="maxConsecutiveFailures">Threshold; values below 1 count as 1.</param>
    /// <param name="policy">What to do at the threshold.</param>
    /// <param name="baseDelay">First backoff delay, in Stopwatch ticks.</param>
    /// <param name="maxDelay">Largest backoff delay, in Stopwatch ticks.</param>
    /// <returns>Whether the job keeps running or was disabled, and how.</returns>
    public FailureVerdict RecordFailure(long now, int maxConsecutiveFailures, JobFailurePolicy policy, long baseDelay, long maxDelay)
    {
        ConsecutiveFailures++;
        if (IsDisabled || ConsecutiveFailures < Math.Max(1, maxConsecutiveFailures))
        {
            return FailureVerdict.Counted;
        }

        IsDisabled = true;
        TimesDisabled++;

        if (policy == JobFailurePolicy.DisablePermanently)
        {
            LastDelay = 0;
            _reenableAt = Never;
            return FailureVerdict.DisabledPermanently;
        }

        DisableStreak++;
        LastDelay = DelayFor(DisableStreak, baseDelay, maxDelay);
        _reenableAt = now > Never - LastDelay ? Never : now + LastDelay;
        return FailureVerdict.DisabledWithBackoff;
    }

    /// <summary>True when the job is disabled and its automatic re-enable time has come.</summary>
    /// <param name="now">Current Stopwatch timestamp.</param>
    /// <returns>True when <see cref="Reenable"/> should be called now.</returns>
    public bool IsReenableDue(long now) => IsDisabled && _reenableAt != Never && now >= _reenableAt;

    /// <summary>
    /// Enables the job again (automatically after the backoff, or by hand). The failure count starts
    /// from zero; the disable streak is kept, so failing again right away backs off longer.
    /// </summary>
    public void Reenable()
    {
        IsDisabled = false;
        ConsecutiveFailures = 0;
        _reenableAt = Never;
    }
}
