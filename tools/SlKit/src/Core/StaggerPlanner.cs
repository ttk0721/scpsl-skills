using System;

namespace SlKit.Core;

/// <summary>
/// Decides how many items to process on the current frame so that <c>N</c> items are spread
/// evenly over one period instead of being processed in a burst on one frame.
/// </summary>
/// <remarks>
/// The plan is driven by elapsed wall-clock time, not by an assumed frame rate: by time
/// <c>t</c> into a period of length <c>P</c>, <c>round(N * t / P)</c> items must be done. At 60 fps
/// with 60 items over 1 s that is one item per frame; at 120 fps it alternates 1/0; if the
/// server drops to 20 fps it becomes 3 per frame, and the period still completes on time. The
/// server's real frame rate is not a constant (Mirror's <c>sendRate</c> overrides
/// <c>server_tickrate</c>, idle mode drops to <c>idle_mode_tickrate</c>), so a fixed
/// "items per frame" setting would be wrong in at least one of those states.
/// Once the period has elapsed every remaining item is due, so nothing is left behind.
/// Mutable struct: keep it in a field. Thread affinity: none (pure), instance not thread-safe.
/// </remarks>
public struct StaggerPlanner
{
    private long _start;
    private long _period;
    private int _total;
    private int _done;
    private bool _active;

    /// <summary>True between <see cref="Begin"/> and the moment every item has been handed out.</summary>
    public bool IsActive => _active && _done < _total;

    /// <summary>Number of items in the current period.</summary>
    public int ItemCount => _total;

    /// <summary>Number of items already handed out in the current period.</summary>
    public int Processed => _done;

    /// <summary>Absolute Stopwatch timestamp at which the current period started.</summary>
    public long PeriodStart => _start;

    /// <summary>Absolute Stopwatch timestamp at which the current period ends.</summary>
    public long PeriodEnd => _start + _period;

    /// <summary>Starts a new period, discarding any items not yet handed out from the previous one.</summary>
    /// <param name="start">Absolute Stopwatch timestamp of the period start.</param>
    /// <param name="period">Period length in Stopwatch ticks; values below 1 become 1.</param>
    /// <param name="itemCount">Number of items to spread; negative values become 0.</param>
    public void Begin(long start, long period, int itemCount)
    {
        _start = start;
        _period = Math.Max(1, period);
        _total = Math.Max(0, itemCount);
        _done = 0;
        _active = true;
    }

    /// <summary>
    /// Returns how many further items should be processed now (possibly 0) and counts them as
    /// processed. Call once per frame; process items <c>[Processed - result, Processed)</c>.
    /// </summary>
    /// <param name="now">Current Stopwatch timestamp.</param>
    /// <returns>The number of items to process on this frame.</returns>
    public int Take(long now)
    {
        if (!IsActive)
        {
            return 0;
        }

        int target = TargetProcessed(_total, now - _start, _period);
        int take = target - _done;
        if (take <= 0)
        {
            return 0;
        }

        _done = target;
        return take;
    }

    /// <summary>Ends the current period without handing out the remaining items.</summary>
    public void Cancel() => _active = false;

    /// <summary>
    /// How many of <paramref name="total"/> items should be finished after
    /// <paramref name="elapsed"/> of a <paramref name="period"/>: <c>round(total * elapsed / period)</c> (half rounds up),
    /// clamped to <c>[0, total]</c>.
    /// </summary>
    /// <param name="total">Items in the period.</param>
    /// <param name="elapsed">Time since the period started, in Stopwatch ticks.</param>
    /// <param name="period">Period length in Stopwatch ticks.</param>
    /// <returns>The cumulative number of items that should be done.</returns>
    public static int TargetProcessed(int total, long elapsed, long period)
    {
        if (total <= 0 || elapsed <= 0)
        {
            return 0;
        }

        if (period <= 0 || elapsed >= period)
        {
            return total;
        }

        // double: total * elapsed can overflow long for long periods on high-frequency clocks.
        double exact = total * (double)elapsed / period;
        // Rounding, not ceiling: frame timestamps jitter around the exact item boundaries (60 items at
        // 60 fps land on k + 0.0001), and ceil would turn that noise into a 2/0 pattern.
        int target = (int)Math.Floor(exact + 0.5);
        return target < 0 ? 0 : target > total ? total : target;
    }
}
