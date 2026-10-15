using System;

namespace SlKit.Core;

/// <summary>
/// Failure handling of ONE clock job, given at registration
/// (<c>SlClock.Every(..., options)</c>). Copied when the job is registered, so changing the object
/// afterwards does nothing, and one object can be reused for several registrations.
/// </summary>
/// <remarks>
/// Why per job and not a static setting: SlKit is one DLL shared by every plugin on the server, so
/// a static "threshold" or "default policy" would let one plugin change how every other plugin's
/// jobs are disabled. A job's failure handling belongs to the plugin that registered it.
/// Invalid values throw <see cref="ArgumentOutOfRangeException"/> at registration, like
/// <c>SlHttpOptions</c> at <c>CreateClient</c>.
/// Thread affinity: plain data; build it on any thread, register on the main thread.
/// </remarks>
public sealed class ClockJobOptions
{
    /// <summary>Default <see cref="MaxConsecutiveFailures"/>: 5.</summary>
    public const int DefaultMaxConsecutiveFailures = 5;

    /// <summary>Default <see cref="ReenableBaseDelay"/>: 30 seconds.</summary>
    public static readonly TimeSpan DefaultReenableBaseDelay = TimeSpan.FromSeconds(30);

    /// <summary>Default <see cref="ReenableMaxDelay"/>: 30 minutes.</summary>
    public static readonly TimeSpan DefaultReenableMaxDelay = TimeSpan.FromMinutes(30);

    /// <summary>Smallest allowed backoff delay: 1 second (anything shorter is a busy retry loop).</summary>
    public static readonly TimeSpan MinimumReenableDelay = TimeSpan.FromSeconds(1);

    /// <summary>Failures in a row after which the job is disabled. At least 1. Default 5.</summary>
    public int MaxConsecutiveFailures { get; set; } = DefaultMaxConsecutiveFailures;

    /// <summary>
    /// What happens at the threshold: <see cref="JobFailurePolicy.Backoff"/> (default; re-enabled
    /// after <see cref="ReenableBaseDelay"/>, doubling up to <see cref="ReenableMaxDelay"/>) or
    /// <see cref="JobFailurePolicy.DisablePermanently"/>.
    /// </summary>
    public JobFailurePolicy FailurePolicy { get; set; } = JobFailurePolicy.Backoff;

    /// <summary>Delay before the first automatic re-enable. At least 1 s. Default 30 s.</summary>
    public TimeSpan ReenableBaseDelay { get; set; } = DefaultReenableBaseDelay;

    /// <summary>Longest backoff delay. At least <see cref="ReenableBaseDelay"/>. Default 30 min.</summary>
    public TimeSpan ReenableMaxDelay { get; set; } = DefaultReenableMaxDelay;
}
