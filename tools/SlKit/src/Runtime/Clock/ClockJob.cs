using System;
using System.Reflection;
using SlKit.Core;

namespace SlKit;

/// <summary>
/// Base of every job driven by <see cref="SlClock"/>. The instance doubles as the
/// <see cref="ClockJobHandle"/> returned to the caller.
/// </summary>
/// <remarks>Thread affinity: as <see cref="ClockJobHandle"/>; everything added here is main-thread only.</remarks>
internal abstract class ClockJob : ClockJobHandle
{
    protected ClockJob(string name, ClockScope scope, JobFailureSettings settings)
        : base(name, scope, settings)
    {
    }

    internal abstract string Kind { get; }

    /// <summary>
    /// Unity's <c>Time.frameCount</c> at registration. A round restart cancels only Round jobs from
    /// earlier frames, so a job registered by another RoundRestarted handler (same frame) is kept
    /// whatever the handler order.
    /// </summary>
    internal int RegisteredFrame;

    /// <summary>The assembly that registered the job (the caller of Every/After/EveryStaggered); scopes <see cref="SlClock.Reenable"/>.</summary>
    internal Assembly Owner;

    internal long Failures { get; set; }

    internal RunningStats Stats;

    internal TokenBucket ErrorLogBucket;

    internal virtual long SkippedPeriods => 0;

    /// <summary>Checks the job's deadline and runs user code if due. Main thread only.</summary>
    /// <param name="now">Frame timestamp (Stopwatch ticks).</param>
    /// <returns>True when user code ran (successfully); exceptions propagate to the clock.</returns>
    internal abstract bool Tick(long now);

    internal ClockJobStats Snapshot(long now)
    {
        long reenableAt = Backoff.ReenableAt;
        TimeSpan? reenableIn = Backoff.IsDisabled && reenableAt != FailureBackoff.Never
            ? StopwatchTime.ToTimeSpan(Math.Max(0, reenableAt - now))
            : (TimeSpan?)null;

        return new ClockJobStats(
            Name,
            Kind,
            Scope,
            Stats.Count,
            Failures,
            Backoff.ConsecutiveFailures,
            Backoff.IsDisabled,
            Stats.LastMicroseconds,
            Stats.AverageMicroseconds,
            Stats.MaxMicroseconds,
            SkippedPeriods,
            Backoff.TimesDisabled,
            reenableIn,
            FailurePolicy);
    }
}
