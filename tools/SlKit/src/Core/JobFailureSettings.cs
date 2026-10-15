using System;

namespace SlKit.Core;

/// <summary>
/// Validated, immutable copy of <see cref="ClockJobOptions"/> held by one job, with the delays
/// already in Stopwatch ticks so the clock's failure path converts nothing.
/// </summary>
/// <remarks>Thread affinity: immutable, any thread.</remarks>
internal readonly struct JobFailureSettings
{
    private JobFailureSettings(int maxConsecutiveFailures, JobFailurePolicy policy, long baseDelayTicks, long maxDelayTicks)
    {
        MaxConsecutiveFailures = maxConsecutiveFailures;
        Policy = policy;
        BaseDelayTicks = baseDelayTicks;
        MaxDelayTicks = maxDelayTicks;
    }

    /// <summary>The defaults of a new <see cref="ClockJobOptions"/>; used when no options are given.</summary>
    internal static JobFailureSettings Default { get; } = new JobFailureSettings(
        ClockJobOptions.DefaultMaxConsecutiveFailures,
        JobFailurePolicy.Backoff,
        StopwatchTime.FromTimeSpan(ClockJobOptions.DefaultReenableBaseDelay),
        StopwatchTime.FromTimeSpan(ClockJobOptions.DefaultReenableMaxDelay));

    internal int MaxConsecutiveFailures { get; }

    /// <summary>The policy at registration; the handle may change its own afterwards.</summary>
    internal JobFailurePolicy Policy { get; }

    internal long BaseDelayTicks { get; }

    internal long MaxDelayTicks { get; }

    /// <summary>Validates and copies <paramref name="options"/>; null gives <see cref="Default"/>.</summary>
    /// <param name="options">The caller's options, or null.</param>
    /// <returns>The settings for one job.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A value is out of range; the message names the property.</exception>
    internal static JobFailureSettings From(ClockJobOptions options)
    {
        if (options == null)
        {
            return Default;
        }

        if (options.MaxConsecutiveFailures < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.MaxConsecutiveFailures, "ClockJobOptions.MaxConsecutiveFailures must be at least 1.");
        }

        if (options.FailurePolicy != JobFailurePolicy.Backoff && options.FailurePolicy != JobFailurePolicy.DisablePermanently)
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.FailurePolicy, "ClockJobOptions.FailurePolicy is not a defined JobFailurePolicy.");
        }

        if (options.ReenableBaseDelay < ClockJobOptions.MinimumReenableDelay)
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.ReenableBaseDelay, "ClockJobOptions.ReenableBaseDelay must be at least 1 second.");
        }

        if (options.ReenableMaxDelay < options.ReenableBaseDelay)
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.ReenableMaxDelay, "ClockJobOptions.ReenableMaxDelay must not be shorter than ReenableBaseDelay.");
        }

        return new JobFailureSettings(
            options.MaxConsecutiveFailures,
            options.FailurePolicy,
            StopwatchTime.FromTimeSpan(options.ReenableBaseDelay),
            StopwatchTime.FromTimeSpan(options.ReenableMaxDelay));
    }
}
