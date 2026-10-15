using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using SlKit.Core;
using static SlKit.TestSupport.TestKit;
using static SlKit.Clock.Tests.ClockHarness;

namespace SlKit.Clock.Tests;

/// <summary>MainThread.Post and the clock's own jobs, driven by the stand-in PlayerLoop.</summary>
internal static class ClockWorkTests
{
    internal static void Register()
    {
        // What the clock does with the work it is given
        Run(nameof(Post_ThrowingActionDoesNotStopTheOnesAfterIt), Post_ThrowingActionDoesNotStopTheOnesAfterIt);
        Run(nameof(Post_BudgetStopsADrainButAlwaysRunsOneAction), Post_BudgetStopsADrainButAlwaysRunsOneAction);
        Run(nameof(Post_BacklogLimitDropsAndCounts), Post_BacklogLimitDropsAndCounts);
        Run(nameof(After_RunsExactlyOnce), After_RunsExactlyOnce);
        Run(nameof(EveryStaggered_HandsOutEachItemInOrderAcrossFrames), EveryStaggered_HandsOutEachItemInOrderAcrossFrames);
        Run(nameof(Backoff_AJobComesBackAfterItsDelayAndWaitsLongerWhenItFailsAgain), Backoff_AJobComesBackAfterItsDelayAndWaitsLongerWhenItFailsAgain);
    }

    // ---------------------------------------------------------------------------------------
    // MainThread.Post and the clock's own jobs, driven by the stand-in PlayerLoop.
    // ---------------------------------------------------------------------------------------

    private static void Post_ThrowingActionDoesNotStopTheOnesAfterIt()
    {
        LabApi.Features.Console.Logger.Take();
        long failuresBefore = MainThread.FailureCount;
        long executedBefore = MainThread.ExecutedCount;
        bool laterRan = false;
        MainThread.Post(() => throw new InvalidOperationException("a posted action that fails"));
        MainThread.Post(() => laterRan = true);
        Equal(2, MainThread.PendingCount, "Post never runs inline");
        False(laterRan, "nothing ran before the frame");

        NextFrame();
        True(laterRan, "the action after the failing one still ran");
        Equal(failuresBefore + 1, MainThread.FailureCount, "the failure was counted");
        Equal(executedBefore + 2, MainThread.ExecutedCount, "both actions count as executed");
        Equal(0, MainThread.PendingCount, "the queue is empty again");
    }

    private static void Post_BudgetStopsADrainButAlwaysRunsOneAction()
    {
        // Each action spends more than the owner's whole budget (2.5 ms), so one frame can run only one of them.
        int ran = 0;
        for (int i = 0; i < 3; i++)
        {
            MainThread.Post(() =>
            {
                Thread.Sleep(10); // scpsl-lint: ignore SL007 -- test stub: no server frame to stall
                ran++;
            });
        }

        NextFrame();
        Equal(1, ran, "the budget was spent by the first action: the frame stops after it, but runs at least one");
        Equal(2, MainThread.PendingCount, "the rest waits for the next frame");

        NextFrame();
        NextFrame();
        Equal(3, ran, "nothing is lost, only delayed");
        Equal(0, MainThread.PendingCount, "drained");
    }

    private static void Post_BacklogLimitDropsAndCounts()
    {
        LabApi.Features.Console.Logger.Take();
        long droppedBefore = MainThread.DroppedCount;
        long executedBefore = MainThread.ExecutedCount;
        const int extra = 5;
        for (int i = 0; i < MainThread.MaxPending + extra; i++)
        {
            MainThread.Post(Nothing);
        }

        Equal(MainThread.MaxPending, MainThread.PendingCount, "the backlog stops at its limit");
        Equal(droppedBefore + extra, MainThread.DroppedCount, "every action over the limit is counted");
        True(LabApi.Features.Console.Logger.Take().Any(l => l.Contains("dropp")), "the first drop logs a warning");

        for (int i = 0; i < 5000 && MainThread.PendingCount > 0; i++)
        {
            NextFrame();
        }

        Equal(0, MainThread.PendingCount, "the backlog drains");
        Equal(executedBefore + MainThread.MaxPending, MainThread.ExecutedCount, "exactly the accepted actions ran");
    }

    private static void Nothing()
    {
    }

    private static void After_RunsExactlyOnce()
    {
        int runs = 0;
        ClockJobHandle once = SlClock.After(TimeSpan.FromMilliseconds(5), () => runs++, "k3.once", ClockScope.Server);
        NextFrame();
        Equal(0, runs, "not before its delay");

        Thread.Sleep(15);
        NextFrame();
        Equal(1, runs, "ran once after its delay");
        True(once.IsDisposed, "a one-shot job is finished after it ran");

        Thread.Sleep(15);
        NextFrame();
        NextFrame();
        Equal(1, runs, "and never again");
    }

    private static void EveryStaggered_HandsOutEachItemInOrderAcrossFrames()
    {
        const int items = 10;
        List<int> source = Enumerable.Range(0, items).ToList();
        List<int> handled = new List<int>();
        int sourceReads = 0;
        System.Diagnostics.Stopwatch since = System.Diagnostics.Stopwatch.StartNew();
        ClockJobHandle job = SlClock.EveryStaggered<int>(TimeSpan.FromMilliseconds(400), () => { sourceReads++; return source; }, handled.Add, "k3.staggered", ClockScope.Server);

        // About a quarter into the first period: only some of the items are due, never all of them.
        SleepUntil(since, 100);
        NextFrame();
        True(handled.Count >= 1 && handled.Count < items, "a first slice, not a burst: " + handled.Count + " of " + items);
        Equal(1, sourceReads, "the source is read once per period");

        // Past the end of the first period: everything of it has been handed out, in order, none twice.
        SleepUntil(since, 480);
        NextFrame();
        True(handled.Count >= items, "the first period was completed: " + handled.Count);
        SequenceEqual(source.ToArray(), handled.Take(items).ToArray(), "in source order, each item once");
        True(sourceReads >= 2, "the next period read the source again");

        job.Dispose();
        NextFrame();
    }

    private static void SleepUntil(System.Diagnostics.Stopwatch since, int milliseconds)
    {
        int left = milliseconds - (int)since.ElapsedMilliseconds;
        if (left > 0)
        {
            Thread.Sleep(left);
        }
    }

    private static void Backoff_AJobComesBackAfterItsDelayAndWaitsLongerWhenItFailsAgain()
    {
        int runs = 0;
        ClockJobHandle job = SlClock.Every(
            TimeSpan.FromMilliseconds(1),
            () =>
            {
                runs++;
                throw new InvalidOperationException("still broken");
            },
            "k3.backoff",
            options: new ClockJobOptions { MaxConsecutiveFailures = 1, ReenableBaseDelay = TimeSpan.FromSeconds(1), ReenableMaxDelay = TimeSpan.FromSeconds(4) });

        Thread.Sleep(5);
        NextFrame();
        Equal(1, runs, "ran and failed");
        True(job.IsDisabled, "disabled after its single allowed failure");

        NextFrame();
        Equal(1, runs, "a disabled job does not run");

        Thread.Sleep(1100);
        NextFrame();
        Equal(2, runs, "it came back after its first delay and ran in that frame");
        True(job.IsDisabled, "and failed again");
        ClockJobStats stats = SlClock.Stats.First(s => s.Name == "k3.backoff");
        Equal(2L, stats.TimesDisabled, "disabled twice");
        True(stats.ReenableIn.HasValue && stats.ReenableIn.Value > TimeSpan.FromSeconds(1), "the second wait is longer than the first: " + stats);

        job.Dispose();
        NextFrame();
    }
}
