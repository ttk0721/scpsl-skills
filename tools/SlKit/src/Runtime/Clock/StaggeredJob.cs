using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using SlKit.Core;

namespace SlKit;

/// <summary>Spreads per-item work over the frames of each period (see <see cref="StaggerPlanner"/>).</summary>
/// <typeparam name="T">Item type.</typeparam>
internal sealed class StaggeredJob<T> : ClockJob
{
    private readonly Func<IReadOnlyList<T>> _source;
    private readonly Action<T> _perItem;
    private readonly long _period;
    private DeadlineTimer _timer;
    private StaggerPlanner _planner;
    private IReadOnlyList<T> _items;

    internal StaggeredJob(string name, ClockScope scope, JobFailureSettings settings, Func<IReadOnlyList<T>> source, Action<T> perItem, long period, long now)
        : base(name, scope, settings)
    {
        _source = source;
        _perItem = perItem;
        _period = Math.Max(1, period);

        // First period starts on the next frame; later ones stay on this grid.
        _timer = DeadlineTimer.PeriodicFrom(now, _period);
    }

    internal override string Kind => "staggered";

    internal override long SkippedPeriods => _timer.SkippedPeriods;

    internal override bool Tick(long now)
    {
        Exception firstFailure = null;
        bool ran = false;

        // Leftovers of the previous period are overdue once a new one starts: finish them first.
        if (now >= _timer.Deadline && _planner.IsActive)
        {
            ran |= ProcessDue(now, ref firstFailure);
        }

        if (_timer.Poll(now))
        {
            ran = true;
            _items = null;
            _planner.Cancel();

            // The period starts on the grid point just passed, so a late frame catches up
            // proportionally instead of shifting the whole period.
            long start = _timer.Deadline - _period;
            IReadOnlyList<T> items = _source();
            _items = items;
            _planner.Begin(start, _period, items?.Count ?? 0);
        }

        if (_planner.IsActive)
        {
            ran |= ProcessDue(now, ref firstFailure);
        }

        if (firstFailure != null)
        {
            // Rethrow so the clock counts this frame as a failure; the trace stays intact.
            ExceptionDispatchInfo.Capture(firstFailure).Throw();
        }

        return ran;
    }

    private bool ProcessDue(long now, ref Exception firstFailure)
    {
        int take = _planner.Take(now);
        if (take <= 0)
        {
            return false;
        }

        IReadOnlyList<T> items = _items;
        int end = _planner.Processed;
        int begin = end - take;

        // The list may shrink mid-period if the source hands out a live collection.
        int count = items?.Count ?? 0;
        if (end > count)
        {
            end = count;
        }

        for (int i = begin; i < end; i++)
        {
            if (IsDisposed)
            {
                break;
            }

            try
            {
                _perItem(items[i]);
            }
            catch (Exception e)
            {
                if (firstFailure == null)
                {
                    firstFailure = e;
                }
            }
        }

        return true;
    }
}
