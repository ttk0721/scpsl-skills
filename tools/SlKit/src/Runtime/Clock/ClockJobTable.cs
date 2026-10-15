using System;
using System.Collections.Generic;
using System.Reflection;
using SlKit.Core;

namespace SlKit;

/// <summary>
/// The jobs registered with <see cref="SlClock"/>, in registration order: adding a job, the per-owner re-enable, the
/// round-end cancellation and the statistics snapshot. Main thread only.
/// </summary>
internal static class ClockJobTable
{
    internal static readonly List<ClockJob> Jobs = new List<ClockJob>(64);

    internal static int Count => Jobs.Count;

    internal static ClockJobHandle Register(ClockJob job, Assembly owner)
    {
        job.Owner = owner;
        // Unity's frame number, not the clock's own count: the clock ticks once per frame AFTER
        // scripts, so its count would lump the end of one frame with the start of the next.
        job.RegisteredFrame = UnityEngine.Time.frameCount;
        Jobs.Add(job);
        return job;
    }

    internal static IReadOnlyList<ClockJobStats> Snapshot()
    {
        long now = StopwatchTime.Now;
        ClockJobStats[] result = new ClockJobStats[Jobs.Count];
        for (int i = 0; i < Jobs.Count; i++)
        {
            result[i] = Jobs[i].Snapshot(now);
        }

        return result;
    }

    internal static int Reenable(Assembly caller, string name)
    {
        int count = 0;
        for (int i = 0; i < Jobs.Count; i++)
        {
            ClockJob job = Jobs[i];
            if (ReferenceEquals(job.Owner, caller) && string.Equals(job.Name, name, StringComparison.Ordinal) && job.Reenable())
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>Disposes every Round job registered before <paramref name="frame"/> (Unity's frame number).</summary>
    internal static void CancelRoundJobs(int frame)
    {
        for (int i = 0; i < Jobs.Count; i++)
        {
            ClockJob job = Jobs[i];
            if (job.Scope == ClockScope.Round && !job.IsDisposed && job.RegisteredFrame < frame)
            {
                job.Dispose();
            }
        }
    }
}
