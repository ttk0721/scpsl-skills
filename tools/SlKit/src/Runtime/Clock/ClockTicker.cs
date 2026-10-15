using System;
using SlKit.Core;
using UnityEngine.LowLevel;

namespace SlKit;

/// <summary>
/// The clock's work in each frame: the PlayerLoop callback that drains <see cref="MainThread"/>, runs every due job and
/// measures its own cost, together with the frame counters and the stall check that tell whether the entry still runs.
/// Main thread only, except reading <see cref="FrameCount"/>.
/// </summary>
internal static class ClockTicker
{
    private static readonly long StallCheckTicks = StopwatchTime.FromTimeSpan(TimeSpan.FromSeconds(10));

    /// <summary>What <see cref="SlClock.Install"/> puts into the PlayerLoop.</summary>
    internal static readonly PlayerLoopSystem.UpdateFunction TickDelegate = Tick;

    private static bool _inTick;
    private static bool _threadConfirmed;
    private static long _lastTick;
    private static long _frameCount;
    private static RunningStats _frameCost;

    internal static bool InTick => _inTick;

    internal static long FrameCount => _frameCount;

    internal static RunningStats FrameCost => _frameCost;

    /// <summary>Starts the stall window over, as if a frame had just run.</summary>
    internal static void RestartStallWindow() => _lastTick = StopwatchTime.Now;

    /// <summary>
    /// True when no frame ran for longer than the stall window (10 s) outside a frame: the signal that another plugin
    /// may have rebuilt the PlayerLoop and dropped the entry.
    /// </summary>
    internal static bool HasStalled() => !_inTick && StopwatchTime.Now - _lastTick > StallCheckTicks;

    private static void Tick()
    {
        long start = StopwatchTime.Now;
        if (!_threadConfirmed)
        {
            // The PlayerLoop callback is by definition on the main thread: authoritative capture.
            MainThread.CaptureCurrentThread();
            _threadConfirmed = true;
        }

        _inTick = true;
        try
        {
            MainThread.Drain(start);

            long now = StopwatchTime.Now;
            int count = ClockJobTable.Jobs.Count; // jobs added during this frame start next frame
            for (int i = 0; i < count; i++)
            {
                ClockJob job = ClockJobTable.Jobs[i];
                if (job.IsDisposed)
                {
                    continue;
                }

                if (job.Backoff.IsDisabled)
                {
                    // One comparison per disabled job per frame; nothing runs until the backoff ends.
                    if (!job.Backoff.IsReenableDue(now))
                    {
                        continue;
                    }

                    long waited = job.Backoff.LastDelay;
                    job.Backoff.Reenable();
                    JobFailureReporter.ReportReenabled(job, waited);
                }

                RunJob(job, now);
            }

            if (SlClock.TakeDirty())
            {
                SlClock.Compact();
            }
        }
        catch (Exception e)
        {
            // Only SlKit's own bookkeeping can land here; never let it escape into the engine loop.
            SlLog.Error("Clock frame failed: " + e);
        }
        finally
        {
            _inTick = false;
            long end = StopwatchTime.Now;
            _frameCost.Add(end - start);
            _lastTick = end;
            _frameCount++;
        }
    }

    private static void RunJob(ClockJob job, long now)
    {
        long t0 = StopwatchTime.Now;
        try
        {
            if (!job.Tick(now))
            {
                return;
            }

            job.Backoff.RecordSuccess();
        }
        catch (Exception e)
        {
            job.Failures++;
            // The job's own settings, fixed at registration: no other plugin can move them.
            FailureVerdict verdict = job.Backoff.RecordFailure(
                now,
                job.Settings.MaxConsecutiveFailures,
                job.FailurePolicy,
                job.Settings.BaseDelayTicks,
                job.Settings.MaxDelayTicks);
            JobFailureReporter.ReportFailure(job, e, now, verdict);
        }

        job.Stats.Add(StopwatchTime.Now - t0);
    }
}
