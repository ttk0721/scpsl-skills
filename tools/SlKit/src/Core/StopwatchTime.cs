using System;
using System.Diagnostics;

namespace SlKit.Core;

/// <summary>
/// Conversions between <see cref="TimeSpan"/>, milliseconds, microseconds and raw
/// <see cref="Stopwatch.GetTimestamp"/> ticks.
/// </summary>
/// <remarks>
/// Every SlKit deadline is an absolute <see cref="Stopwatch"/> timestamp. Unity's <c>Time.*</c>
/// is scaled by <c>timeScale</c> (0.01 in idle mode) and clamped at <c>maximumDeltaTime</c>, so
/// it cannot drive wall-clock work; <see cref="Stopwatch"/> is monotonic, unscaled and readable
/// from any thread. Stopwatch ticks are NOT <see cref="TimeSpan"/> ticks: their rate is
/// <see cref="Stopwatch.Frequency"/>.
/// Thread affinity: all members are pure and thread-safe.
/// </remarks>
public static class StopwatchTime
{
    /// <summary>Stopwatch ticks per second (<see cref="Stopwatch.Frequency"/>), cached.</summary>
    public static readonly long Frequency = Stopwatch.Frequency;

    /// <summary>The current monotonic timestamp in Stopwatch ticks. Thread-safe.</summary>
    public static long Now => Stopwatch.GetTimestamp();

    /// <summary>
    /// Converts a <see cref="TimeSpan"/> to Stopwatch ticks, rounding to the nearest tick.
    /// Negative spans become 0. Thread-safe.
    /// </summary>
    /// <param name="span">The duration to convert.</param>
    /// <returns>The duration in Stopwatch ticks.</returns>
    public static long FromTimeSpan(TimeSpan span)
    {
        if (span <= TimeSpan.Zero)
        {
            return 0;
        }

        // Seconds as double keeps 15+ significant digits: sub-tick exact for any realistic period.
        return Clamp(span.TotalSeconds * Frequency);
    }

    /// <summary>Converts milliseconds to Stopwatch ticks. Thread-safe.</summary>
    /// <param name="milliseconds">Duration in milliseconds; negative values become 0.</param>
    /// <returns>The duration in Stopwatch ticks.</returns>
    public static long FromMilliseconds(double milliseconds)
    {
        return milliseconds <= 0 ? 0 : Clamp(milliseconds * Frequency / 1000.0);
    }

    /// <summary>
    /// Largest duration these conversions return: a quarter of <see cref="long.MaxValue"/>, so that
    /// <c>timestamp + duration</c> and <c>2 * duration</c> stay representable. Out of range, the
    /// cast <c>(long)double</c> is unspecified (it yields <see cref="long.MinValue"/> on x86), which
    /// would turn <c>TimeSpan.MaxValue</c> ("never") into a deadline in the distant past.
    /// </summary>
    public const long MaxDurationTicks = long.MaxValue / 4;

    private static long Clamp(double ticks)
    {
        if (!(ticks < MaxDurationTicks)) // also catches NaN
        {
            return MaxDurationTicks;
        }

        return (long)Math.Round(ticks);
    }

    /// <summary>Converts Stopwatch ticks to a <see cref="TimeSpan"/>. Thread-safe.</summary>
    /// <param name="ticks">Duration in Stopwatch ticks.</param>
    /// <returns>The equivalent <see cref="TimeSpan"/>.</returns>
    public static TimeSpan ToTimeSpan(long ticks)
    {
        return TimeSpan.FromTicks((long)Math.Round(ticks * (double)TimeSpan.TicksPerSecond / Frequency));
    }

    /// <summary>Converts Stopwatch ticks to milliseconds. Thread-safe.</summary>
    /// <param name="ticks">Duration in Stopwatch ticks.</param>
    /// <returns>The duration in milliseconds.</returns>
    public static double ToMilliseconds(long ticks) => ticks * 1000.0 / Frequency;

    /// <summary>Converts Stopwatch ticks to microseconds. Thread-safe.</summary>
    /// <param name="ticks">Duration in Stopwatch ticks.</param>
    /// <returns>The duration in microseconds.</returns>
    public static double ToMicroseconds(long ticks) => ticks * 1000000.0 / Frequency;
}
