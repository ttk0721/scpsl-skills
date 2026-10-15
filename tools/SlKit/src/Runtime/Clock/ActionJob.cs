using System;
using SlKit.Core;

namespace SlKit;

/// <summary>A periodic or one-shot job running one <see cref="Action"/>.</summary>
internal sealed class ActionJob : ClockJob
{
    private readonly Action _action;
    private DeadlineTimer _timer;

    internal ActionJob(string name, ClockScope scope, JobFailureSettings settings, Action action, DeadlineTimer timer)
        : base(name, scope, settings)
    {
        _action = action;
        _timer = timer;
    }

    internal override string Kind => _timer.IsPeriodic ? "every" : "after";

    internal override long SkippedPeriods => _timer.SkippedPeriods;

    internal override bool Tick(long now)
    {
        if (!_timer.Poll(now))
        {
            return false;
        }

        // A one-shot job is finished once it was attempted, whether or not it throws.
        if (!_timer.IsPeriodic)
        {
            Dispose();
        }

        _action();
        return true;
    }
}
