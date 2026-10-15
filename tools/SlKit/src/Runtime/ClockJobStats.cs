using System;
using System.Globalization;
using SlKit.Core;

namespace SlKit;

/// <summary>Point-in-time statistics of one <see cref="SlClock"/> job.</summary>
/// <remarks>
/// A plugin's own cost should be observable from inside the server: these numbers come from
/// <see cref="System.Diagnostics.Stopwatch"/> measurements around each run, so they are wall-clock
/// microseconds spent on the main thread. Thread affinity: immutable snapshot, any thread.
/// </remarks>
public sealed class ClockJobStats
{
    internal ClockJobStats(
        string name,
        string kind,
        ClockScope scope,
        long runs,
        long failures,
        int consecutiveFailures,
        bool isDisabled,
        double lastMicroseconds,
        double averageMicroseconds,
        double maxMicroseconds,
        long skippedPeriods,
        long timesDisabled,
        TimeSpan? reenableIn,
        JobFailurePolicy failurePolicy)
    {
        Name = name;
        Kind = kind;
        Scope = scope;
        Runs = runs;
        Failures = failures;
        ConsecutiveFailures = consecutiveFailures;
        IsDisabled = isDisabled;
        LastMicroseconds = lastMicroseconds;
        AverageMicroseconds = averageMicroseconds;
        MaxMicroseconds = maxMicroseconds;
        SkippedPeriods = skippedPeriods;
        TimesDisabled = timesDisabled;
        ReenableIn = reenableIn;
        FailurePolicy = failurePolicy;
    }

    /// <summary>The name given at registration.</summary>
    public string Name { get; }

    /// <summary>"every", "after" or "staggered".</summary>
    public string Kind { get; }

    /// <summary>The job's scope.</summary>
    public ClockScope Scope { get; }

    /// <summary>Number of runs (for staggered jobs: frames on which items were processed).</summary>
    public long Runs { get; }

    /// <summary>Total number of runs that threw.</summary>
    public long Failures { get; }

    /// <summary>Failures since the last successful run or re-enable.</summary>
    public int ConsecutiveFailures { get; }

    /// <summary>True while the job is disabled after its <see cref="ClockJobHandle.MaxConsecutiveFailures"/> failures in a row.</summary>
    public bool IsDisabled { get; }

    /// <summary>Duration of the most recent run, in microseconds.</summary>
    public double LastMicroseconds { get; }

    /// <summary>Mean run duration, in microseconds.</summary>
    public double AverageMicroseconds { get; }

    /// <summary>Longest run, in microseconds.</summary>
    public double MaxMicroseconds { get; }

    /// <summary>
    /// Periods skipped because the server was late by more than one whole period (hitch, idle mode),
    /// including the periods that passed while the job was disabled.
    /// </summary>
    public long SkippedPeriods { get; }

    /// <summary>How often the job was disabled since it was registered.</summary>
    public long TimesDisabled { get; }

    /// <summary>Time left until the automatic re-enable, or null when the job is enabled or disabled permanently.</summary>
    public TimeSpan? ReenableIn { get; }

    /// <summary>The job's failure policy at the time of the snapshot.</summary>
    public JobFailurePolicy FailurePolicy { get; }

    /// <summary>One-line summary with invariant culture.</summary>
    /// <returns>The formatted statistics.</returns>
    public override string ToString()
    {
        string state = string.Empty;
        if (IsDisabled)
        {
            state = ReenableIn.HasValue
                ? string.Format(CultureInfo.InvariantCulture, " DISABLED (re-enable in {0:0}s)", ReenableIn.Value.TotalSeconds)
                : " DISABLED (until re-enabled)";
        }

        return string.Format(
            CultureInfo.InvariantCulture,
            "{0} [{1},{2}] runs={3} fail={4}{5} last={6:0.0}us avg={7:0.0}us max={8:0.0}us skipped={9} disabled={10}x",
            Name,
            Kind,
            Scope,
            Runs,
            Failures,
            state,
            LastMicroseconds,
            AverageMicroseconds,
            MaxMicroseconds,
            SkippedPeriods,
            TimesDisabled);
    }
}
