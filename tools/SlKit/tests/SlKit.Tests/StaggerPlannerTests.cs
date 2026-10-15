using System;
using SlKit.Core;
using static SlKit.TestSupport.TestKit;

namespace SlKit.Tests;

/// <summary>Tests of StaggerPlanner: how per-item work is spread over the frames of a period.</summary>
internal static class StaggerPlannerTests
{
    internal static void Register()
    {
        // Stagger planner
        Run(nameof(Stagger_OnePerFrameAt60FpsFor60Items), Stagger_OnePerFrameAt60FpsFor60Items);
        Run(nameof(Stagger_ToleratesFrameTimestampJitter), Stagger_ToleratesFrameTimestampJitter);
        Run(nameof(Stagger_RemainderIsSpreadNotDumped), Stagger_RemainderIsSpreadNotDumped);
        Run(nameof(Stagger_ZeroItemsNeverTakes), Stagger_ZeroItemsNeverTakes);
        Run(nameof(Stagger_FewerItemsThanFramesSpacesThemOut), Stagger_FewerItemsThanFramesSpacesThemOut);
        Run(nameof(Stagger_MoreItemsThanFramesBatches), Stagger_MoreItemsThanFramesBatches);
        Run(nameof(Stagger_SumEqualsNForManyCombinations), Stagger_SumEqualsNForManyCombinations);
        Run(nameof(Stagger_SlowFramesCatchUpWithinPeriod), Stagger_SlowFramesCatchUpWithinPeriod);
        Run(nameof(Stagger_HandsOutEverythingOnceTheWindowIsOver), Stagger_HandsOutEverythingOnceTheWindowIsOver);
        Run(nameof(Stagger_BeginRestartsAndCancelStops), Stagger_BeginRestartsAndCancelStops);
        Run(nameof(Stagger_BeforeBeginAndBeforeStartTakeNothing), Stagger_BeforeBeginAndBeforeStartTakeNothing);
        Run(nameof(Stagger_ClampsBadArguments), Stagger_ClampsBadArguments);
        Run(nameof(Stagger_TargetProcessedEdgesAndLargeValues), Stagger_TargetProcessedEdgesAndLargeValues);
        Run(nameof(Stagger_ReportsPeriodWindow), Stagger_ReportsPeriodWindow);
    }

    // ---------------------------------------------------------------------------------------
    // Stagger planner. Frames are simulated at fixed steps; period = frames * step.
    // ---------------------------------------------------------------------------------------

    /// <summary>Runs one period at <paramref name="frames"/> frames and returns the per-frame take counts (frame k at k*step).</summary>
    private static int[] SimulatePeriod(int items, int frames, long step = 1000)
    {
        StaggerPlanner planner = default;
        planner.Begin(0, frames * step, items);
        int[] takes = new int[frames];
        int expectedProcessed = 0;
        for (int k = 1; k <= frames; k++)
        {
            int take = planner.Take(k * step);
            takes[k - 1] = take;
            expectedProcessed += take;
            Equal(expectedProcessed, planner.Processed, "Processed tracks the sum of takes");
        }

        return takes;
    }

    private static void Stagger_OnePerFrameAt60FpsFor60Items()
    {
        int[] takes = SimulatePeriod(60, 60);
        for (int k = 0; k < takes.Length; k++)
        {
            Equal(1, takes[k], "frame " + (k + 1));
        }
    }

    private static void Stagger_ToleratesFrameTimestampJitter()
    {
        // Real frame timestamps are never exactly on the k*step grid. 60 items at 60 fps with
        // up to +/-5 ticks of jitter (0.5% of a frame) must still be 1 per frame, not 2/0.
        StaggerPlanner planner = default;
        long step = 1000;
        planner.Begin(0, 60 * step, 60);
        long[] jitter = { 5, -5, 3, -4, 0, 5, -5 };
        int sum = 0;
        for (int k = 1; k <= 60; k++)
        {
            int take = planner.Take(k * step + jitter[k % jitter.Length]);
            Equal(1, take, "frame " + k);
            sum += take;
        }

        Equal(60, sum);
    }

    private static void Stagger_RemainderIsSpreadNotDumped()
    {
        // 7 items over 3 frames: cumulative round(7k/3) = 2, 5, 7 -> 2,3,2. The remainder is not
        // dumped on the last frame (2,2,3) and not on the first.
        int[] takes = SimulatePeriod(7, 3);
        SequenceEqual(new[] { 2, 3, 2 }, takes);

        // 10 items over 4 frames: round(2.5)=3 (half up), 5, round(7.5)=8, 10 -> 3,2,3,2
        SequenceEqual(new[] { 3, 2, 3, 2 }, SimulatePeriod(10, 4));
    }

    private static void Stagger_ZeroItemsNeverTakes()
    {
        int[] takes = SimulatePeriod(0, 10);
        foreach (int take in takes)
        {
            Equal(0, take, "no items, no work");
        }

        StaggerPlanner planner = default;
        planner.Begin(0, 1000, 0);
        False(planner.IsActive, "an empty period is not active");
        Equal(0, planner.Take(5000), "even long after the period");
        Equal(0, planner.ItemCount);
    }

    private static void Stagger_FewerItemsThanFramesSpacesThemOut()
    {
        // 3 items over 10 frames: cumulative round(0.3k) = 0,1,1,1,2,2,2,2,3,3
        SequenceEqual(new[] { 0, 1, 0, 0, 1, 0, 0, 0, 1, 0 }, SimulatePeriod(3, 10));

        // One item over many frames lands mid-period, not on the first frame and not last-only.
        int[] one = SimulatePeriod(1, 9);
        Equal(1, Sum(one));
        int index = Array.IndexOf(one, 1);
        True(index >= 3 && index <= 5, "single item lands mid-period, frame index " + index);

        // Never more than one item per frame when N < frames.
        foreach (int take in SimulatePeriod(5, 100))
        {
            True(take <= 1, "at most one item per frame");
        }
    }

    private static void Stagger_MoreItemsThanFramesBatches()
    {
        // 250 items over 60 frames: 4 or 5 per frame (floor/ceil of 4.1667), never a burst.
        int[] takes = SimulatePeriod(250, 60);
        Equal(250, Sum(takes));
        foreach (int take in takes)
        {
            True(take == 4 || take == 5, "4 or 5 per frame, got " + take);
        }

        // Exactly divisible: 120 items over 60 frames is exactly 2 per frame.
        foreach (int take in SimulatePeriod(120, 60))
        {
            Equal(2, take);
        }

        // Single frame in the period: everything at once (nothing to spread over).
        SequenceEqual(new[] { 17 }, SimulatePeriod(17, 1));
    }

    private static void Stagger_SumEqualsNForManyCombinations()
    {
        // For every N and frame count: the period hands out exactly N items, per-frame counts are
        // never more than 1 apart (even spread), and the planner is finished at the period end.
        int[] itemCounts = { 0, 1, 2, 3, 7, 10, 11, 59, 60, 61, 64, 100, 127, 250, 1000 };
        int[] frameCounts = { 1, 2, 3, 7, 10, 30, 60, 61, 120 };
        foreach (int n in itemCounts)
        {
            foreach (int frames in frameCounts)
            {
                int[] takes = SimulatePeriod(n, frames);
                string label = "N=" + n + " frames=" + frames;
                Equal(n, Sum(takes), "sum " + label);

                int min = int.MaxValue;
                int max = int.MinValue;
                // The last frame lands exactly on the period end and is allowed to flush the
                // remainder; for the interior frames the spread must be even.
                for (int k = 0; k < takes.Length; k++)
                {
                    min = Math.Min(min, takes[k]);
                    max = Math.Max(max, takes[k]);
                    True(takes[k] >= 0, "non-negative " + label);
                }

                True(max - min <= 1, "even spread (min " + min + ", max " + max + ") " + label);
            }
        }
    }

    private static void Stagger_SlowFramesCatchUpWithinPeriod()
    {
        // 60 items over 1 s at 20 fps -> 3 per frame; the period still completes on time.
        int[] takes = SimulatePeriod(60, 20, 50);
        foreach (int take in takes)
        {
            Equal(3, take);
        }

        // A hitch: frames at 0.1 s then 0.9 s. Time-based, so the planner catches up in one call.
        StaggerPlanner planner = default;
        planner.Begin(0, 1000, 60);
        Equal(6, planner.Take(100));
        Equal(48, planner.Take(900));
        Equal(6, planner.Take(1000));
        Equal(60, planner.Processed);
    }

    private static void Stagger_HandsOutEverythingOnceTheWindowIsOver()
    {
        StaggerPlanner planner = default;
        planner.Begin(0, 1000, 25);
        Equal(0, planner.Take(1));
        True(planner.IsActive, "active while items remain");
        Equal(25, planner.Take(5000), "long after the period everything is due");
        False(planner.IsActive, "finished");
        Equal(0, planner.Take(6000), "nothing more is handed out");
    }

    private static void Stagger_BeginRestartsAndCancelStops()
    {
        StaggerPlanner planner = default;
        planner.Begin(0, 1000, 10);
        Equal(5, planner.Take(500));

        // A new period discards the 5 unhandled items and restarts the counters.
        planner.Begin(2000, 1000, 4);
        Equal(0, planner.Processed);
        Equal(4, planner.ItemCount);
        Equal(0, planner.Take(2000), "no time elapsed in the new period");
        Equal(2, planner.Take(2500));

        planner.Cancel();
        False(planner.IsActive, "cancelled");
        Equal(0, planner.Take(9999), "a cancelled period hands out nothing");
    }

    private static void Stagger_BeforeBeginAndBeforeStartTakeNothing()
    {
        StaggerPlanner fresh = default;
        Equal(0, fresh.Take(123456), "never begun");
        False(fresh.IsActive, "never begun is inactive");

        StaggerPlanner planner = default;
        planner.Begin(1000, 1000, 10);
        Equal(0, planner.Take(900), "clock before the start (elapsed < 0)");
        Equal(0, planner.Take(1000), "elapsed == 0");
    }

    private static void Stagger_ClampsBadArguments()
    {
        StaggerPlanner planner = default;
        planner.Begin(0, 0, -5);
        Equal(0, planner.ItemCount, "negative count becomes 0");
        Equal(1L, planner.PeriodEnd - planner.PeriodStart, "period below 1 becomes 1 tick");

        planner.Begin(0, -10, 3);
        Equal(1L, planner.PeriodEnd, "negative period becomes 1 tick");
        Equal(3, planner.Take(1), "a 1-tick period is over after 1 tick");
    }

    private static void Stagger_TargetProcessedEdgesAndLargeValues()
    {
        Equal(0, StaggerPlanner.TargetProcessed(0, 500, 1000), "no items");
        Equal(0, StaggerPlanner.TargetProcessed(-3, 500, 1000), "negative items");
        Equal(0, StaggerPlanner.TargetProcessed(10, 0, 1000), "no time elapsed");
        Equal(0, StaggerPlanner.TargetProcessed(10, -1, 1000), "negative elapsed");
        Equal(10, StaggerPlanner.TargetProcessed(10, 1000, 1000), "elapsed == period");
        Equal(10, StaggerPlanner.TargetProcessed(10, 5, 0), "zero period is treated as over");
        Equal(5, StaggerPlanner.TargetProcessed(10, 500, 1000), "half way");
        Equal(2, StaggerPlanner.TargetProcessed(3, 500, 1000), "1.5 rounds half up");
        Equal(0, StaggerPlanner.TargetProcessed(3, 100, 1000), "0.3 rounds down");

        // total * elapsed would overflow a long here; the double path must not.
        long hugePeriod = 4_000_000_000_000_000_000L;
        Equal(50, StaggerPlanner.TargetProcessed(100, hugePeriod / 2, hugePeriod), "overflow-safe midpoint");
        Equal(100, StaggerPlanner.TargetProcessed(100, hugePeriod, hugePeriod));
    }

    private static void Stagger_ReportsPeriodWindow()
    {
        StaggerPlanner planner = default;
        planner.Begin(300, 700, 5);
        Equal(300L, planner.PeriodStart);
        Equal(1000L, planner.PeriodEnd);
        Equal(5, planner.ItemCount);
    }

    // ---------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------

    private static int Sum(int[] values)
    {
        int sum = 0;
        foreach (int value in values)
        {
            sum += value;
        }

        return sum;
    }
}
