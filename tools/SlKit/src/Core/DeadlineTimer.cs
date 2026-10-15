using System;

namespace SlKit.Core;

/// <summary>
/// One absolute-deadline timer on the <see cref="StopwatchTime"/> clock: the deadline-scheduler
/// primitive behind <c>SlClock</c>. Periodic or one-shot.
/// </summary>
/// <remarks>
/// Policy, and why:
/// <list type="bullet">
/// <item>Deadlines are absolute (<c>next = previous + period</c>), never "now + period", so a
/// timer polled once per frame does not drift by up to a frame each period. MEC's
/// <c>CallPeriodically</c> re-arms from the resume frame and accumulates exactly that drift.</item>
/// <item><see cref="Poll"/> reports "due" at most once per call. The caller polls once per
/// frame, so a job runs at most once per frame.</item>
/// <item>Periods missed during a hitch are skipped, not replayed: after a 2 s freeze a 100 ms
/// timer fires once, not 20 times in a row on the frame that is already late. The phase is kept
/// (the next deadline stays on the original grid) and the skipped count is recorded.</item>
/// </list>
/// This is a mutable struct: store it in a field or array element and call members on that
/// storage, never on a copy. Thread affinity: none (pure), but an instance is not thread-safe.
/// </remarks>
public struct DeadlineTimer
{
    private long _deadline;
    private long _period;
    private bool _armed;
    private long _skipped;

    /// <summary>The next absolute deadline in Stopwatch ticks.</summary>
    public long Deadline => _deadline;

    /// <summary>The period in Stopwatch ticks; 0 for a one-shot timer.</summary>
    public long Period => _period;

    /// <summary>True while the timer can still fire (periodic timers stay armed until <see cref="Disarm"/>).</summary>
    public bool IsArmed => _armed;

    /// <summary>True for a repeating timer.</summary>
    public bool IsPeriodic => _period > 0;

    /// <summary>Total number of periods skipped because the poller was late by more than one period.</summary>
    public long SkippedPeriods => _skipped;

    /// <summary>
    /// Creates a periodic timer whose first deadline is <paramref name="now"/> + <paramref name="period"/>.
    /// </summary>
    /// <param name="now">Current Stopwatch timestamp.</param>
    /// <param name="period">Period in Stopwatch ticks; values below 1 become 1.</param>
    /// <returns>An armed periodic timer.</returns>
    public static DeadlineTimer Periodic(long now, long period)
    {
        return PeriodicFrom(now + Math.Max(1, period), period);
    }

    /// <summary>Creates a periodic timer with an explicit first deadline.</summary>
    /// <param name="firstDeadline">Absolute Stopwatch timestamp of the first firing.</param>
    /// <param name="period">Period in Stopwatch ticks; values below 1 become 1.</param>
    /// <returns>An armed periodic timer.</returns>
    public static DeadlineTimer PeriodicFrom(long firstDeadline, long period)
    {
        if (period < 1)
        {
            period = 1;
        }

        return new DeadlineTimer { _deadline = firstDeadline, _period = period, _armed = true };
    }

    /// <summary>Creates a one-shot timer firing at <paramref name="now"/> + <paramref name="delay"/>.</summary>
    /// <param name="now">Current Stopwatch timestamp.</param>
    /// <param name="delay">Delay in Stopwatch ticks; negative values become 0 (due immediately).</param>
    /// <returns>An armed one-shot timer.</returns>
    public static DeadlineTimer OneShot(long now, long delay)
    {
        return new DeadlineTimer { _deadline = now + Math.Max(0, delay), _period = 0, _armed = true };
    }

    /// <summary>
    /// Checks the timer against <paramref name="now"/>. Returns true at most once per call when
    /// the deadline has passed, then re-arms a periodic timer on the next deadline of its grid
    /// that is strictly after <paramref name="now"/> (skipping missed periods) or disarms a
    /// one-shot timer.
    /// </summary>
    /// <param name="now">Current Stopwatch timestamp.</param>
    /// <returns>True when the caller should run the timer's work now.</returns>
    public bool Poll(long now)
    {
        if (!_armed || now < _deadline)
        {
            return false;
        }

        if (!IsPeriodic)
        {
            _armed = false;
            return true;
        }

        long skipped;
        _deadline = NextDeadline(_deadline, _period, now, out skipped);
        _skipped += skipped;
        return true;
    }

    /// <summary>Stops the timer; <see cref="Poll"/> returns false from now on.</summary>
    public void Disarm() => _armed = false;

    /// <summary>
    /// Computes the deadline that follows <paramref name="deadline"/> (which has just fired) on the
    /// grid <c>deadline + k * period</c>, choosing the first one strictly after <paramref name="now"/>.
    /// </summary>
    /// <param name="deadline">The deadline that fired.</param>
    /// <param name="period">Grid period in Stopwatch ticks (must be positive).</param>
    /// <param name="now">Current Stopwatch timestamp (at or after <paramref name="deadline"/>).</param>
    /// <param name="skipped">How many grid points between the two deadlines were skipped.</param>
    /// <returns>The next deadline.</returns>
    public static long NextDeadline(long deadline, long period, long now, out long skipped)
    {
        if (period < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(period), "Period must be at least one tick.");
        }

        long next = deadline + period;
        if (next > now)
        {
            skipped = 0;
            return next;
        }

        // Late by at least one whole period: jump to the first grid point after now.
        skipped = (now - deadline) / period;
        return deadline + (skipped + 1) * period;
    }
}
