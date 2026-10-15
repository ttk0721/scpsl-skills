using System;
using System.Globalization;
using SlKit.Core;

namespace SlKit;

/// <summary>
/// The log lines for a failing <see cref="SlClock"/> job: what happened, what happens next, and the
/// last error, rate-limited for a job that fails every frame.
/// </summary>
/// <remarks>Thread affinity: MAIN THREAD, like the clock that calls it.</remarks>
internal static class JobFailureReporter
{
    /// <summary>Logs the outcome of one failed run, according to <paramref name="verdict"/>.</summary>
    /// <param name="job">The job that threw.</param>
    /// <param name="e">What it threw.</param>
    /// <param name="now">Current Stopwatch timestamp.</param>
    /// <param name="verdict">What <see cref="FailureBackoff.RecordFailure"/> decided.</param>
    internal static void ReportFailure(ClockJob job, Exception e, long now, FailureVerdict verdict)
    {
        if (verdict == FailureVerdict.DisabledWithBackoff)
        {
            SlLog.Error(
                "Job '" + job.Name + "' disabled after " + job.Backoff.ConsecutiveFailures
                + " consecutive failures (" + job.Failures + " total); it is re-enabled automatically in "
                + FormatDelay(job.Backoff.LastDelay) + " (disable " + job.Backoff.DisableStreak
                + " in a row; the delay doubles each time, up to " + FormatDelay(job.Settings.MaxDelayTicks)
                + "). Last error: " + e);
            return;
        }

        if (verdict == FailureVerdict.DisabledPermanently)
        {
            SlLog.Error(
                "Job '" + job.Name + "' disabled after " + job.Backoff.ConsecutiveFailures
                + " consecutive failures (" + job.Failures + " total) until it is re-enabled by hand "
                + "(ClockJobHandle.Reenable or SlClock.Reenable(\"" + job.Name + "\")). Last error: " + e);
            return;
        }

        // Rate-limited: a job failing every frame must not turn into a log storm.
        if (!ErrorLogThrottle.TryAcquire(ref job.ErrorLogBucket, 2, 1.0 / 30.0, now, out long suppressed))
        {
            return;
        }

        SlLog.Warn(
            "Job '" + job.Name + "' threw (failure " + job.Backoff.ConsecutiveFailures + "/" + job.Settings.MaxConsecutiveFailures
            + (suppressed > 0 ? ", " + suppressed + " earlier errors suppressed" : string.Empty)
            + "): " + e);
    }

    /// <summary>Logs that the backoff of a disabled job is over and the job runs again.</summary>
    /// <param name="job">The job, already re-enabled.</param>
    /// <param name="waited">The backoff delay that has just elapsed, in Stopwatch ticks.</param>
    internal static void ReportReenabled(ClockJob job, long waited)
    {
        SlLog.Warn(
            "Job '" + job.Name + "' re-enabled after a " + FormatDelay(waited) + " backoff (disable "
            + job.Backoff.DisableStreak + " in a row; the next one waits " + FormatDelay(NextDelay(job)) + ").");
    }

    private static long NextDelay(ClockJob job)
    {
        return FailureBackoff.DelayFor(
            job.Backoff.DisableStreak + 1,
            job.Settings.BaseDelayTicks,
            job.Settings.MaxDelayTicks);
    }

    private static string FormatDelay(long ticks)
    {
        double seconds = StopwatchTime.ToMilliseconds(ticks) / 1000.0;
        return seconds < 120
            ? seconds.ToString("0", CultureInfo.InvariantCulture) + " s"
            : (seconds / 60.0).ToString("0.#", CultureInfo.InvariantCulture) + " min";
    }
}
