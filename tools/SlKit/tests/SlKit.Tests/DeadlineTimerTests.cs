using System;
using SlKit.Core;
using static SlKit.TestSupport.TestKit;

namespace SlKit.Tests;

/// <summary>Tests of DeadlineTimer: absolute deadlines, no drift, skipped periods, one-shot timers.</summary>
internal static class DeadlineTimerTests
{
    internal static void Register()
    {
        // Deadline scheduler
        Run(nameof(Deadline_PeriodicStartsOneFullPeriodAhead), Deadline_PeriodicStartsOneFullPeriodAhead);
        Run(nameof(Deadline_NoDriftWhenPolledLate), Deadline_NoDriftWhenPolledLate);
        Run(nameof(Deadline_NoDriftOverThousandsOfPeriodsWithJitter), Deadline_NoDriftOverThousandsOfPeriodsWithJitter);
        Run(nameof(Deadline_FiresExactlyOnDeadline), Deadline_FiresExactlyOnDeadline);
        Run(nameof(Deadline_AtMostOneRunPerPoll), Deadline_AtMostOneRunPerPoll);
        Run(nameof(Deadline_SkipsMissedPeriodsAfterHitch), Deadline_SkipsMissedPeriodsAfterHitch);
        Run(nameof(Deadline_KeepsPhaseAfterHitch), Deadline_KeepsPhaseAfterHitch);
        Run(nameof(Deadline_LateByExactPeriodsSkipsStrictlyPastNow), Deadline_LateByExactPeriodsSkipsStrictlyPastNow);
        Run(nameof(Deadline_RunsPlusSkippedCoverEveryGridPoint), Deadline_RunsPlusSkippedCoverEveryGridPoint);
        Run(nameof(Deadline_OneShotFiresOnceThenDisarms), Deadline_OneShotFiresOnceThenDisarms);
        Run(nameof(Deadline_OneShotNegativeDelayIsDueImmediately), Deadline_OneShotNegativeDelayIsDueImmediately);
        Run(nameof(Deadline_DisarmStopsFiring), Deadline_DisarmStopsFiring);
        Run(nameof(Deadline_PeriodFloorsAtOneTick), Deadline_PeriodFloorsAtOneTick);
        Run(nameof(Deadline_PeriodicFromUsesExplicitFirstDeadline), Deadline_PeriodicFromUsesExplicitFirstDeadline);
        Run(nameof(Deadline_NextDeadlineRejectsNonPositivePeriod), Deadline_NextDeadlineRejectsNonPositivePeriod);
        Run(nameof(Deadline_DefaultInstanceNeverFires), Deadline_DefaultInstanceNeverFires);
    }

    // ---------------------------------------------------------------------------------------
    // Deadline scheduler. Times are plain tick counts; the grid used below is 100 ticks.
    // ---------------------------------------------------------------------------------------

    private static void Deadline_PeriodicStartsOneFullPeriodAhead()
    {
        DeadlineTimer t = DeadlineTimer.Periodic(1000, 100);
        Equal(1100L, t.Deadline);
        Equal(100L, t.Period);
        True(t.IsArmed && t.IsPeriodic, "armed periodic");
        False(t.Poll(1099), "one tick early");
        True(t.Poll(1100), "on time");
    }

    private static void Deadline_NoDriftWhenPolledLate()
    {
        // Polled 7 ticks late every period (a frame boundary after the deadline). A timer that
        // re-armed from "now" would drift by 7 each period; this one must stay on the grid.
        DeadlineTimer t = DeadlineTimer.Periodic(0, 100);
        for (int k = 1; k <= 50; k++)
        {
            True(t.Poll(k * 100L + 7), "due in period " + k);
            Equal((k + 1) * 100L, t.Deadline, "deadline stays on grid after period " + k);
        }

        Equal(0L, t.SkippedPeriods, "no skips when each poll is within one period");
    }

    private static void Deadline_NoDriftOverThousandsOfPeriodsWithJitter()
    {
        // 16-tick frames vs a 100-tick period: poll times are never aligned with the grid.
        DeadlineTimer t = DeadlineTimer.Periodic(0, 100);
        long runs = 0;
        long now = 0;
        while (now < 100_000)
        {
            now += 16;
            if (t.Poll(now))
            {
                runs++;
            }
        }

        // The deadline after the last poll is the first grid point beyond now: exactly on the grid.
        Equal(0L, t.Deadline % 100, "deadline is a multiple of the period");
        True(t.Deadline > now, "deadline is in the future");
        True(t.Deadline - now <= 100, "deadline is at most one period ahead");
        Equal(now / 100, runs + t.SkippedPeriods, "every grid point was run or counted as skipped");
    }

    private static void Deadline_FiresExactlyOnDeadline()
    {
        DeadlineTimer t = DeadlineTimer.Periodic(0, 100);
        True(t.Poll(100), "now == deadline fires");
        Equal(200L, t.Deadline);
        Equal(0L, t.SkippedPeriods);
    }

    private static void Deadline_AtMostOneRunPerPoll()
    {
        DeadlineTimer t = DeadlineTimer.Periodic(0, 100);
        True(t.Poll(100), "first poll at deadline fires");
        False(t.Poll(100), "second poll at the same instant does not");
        False(t.Poll(150), "still before the next deadline");

        // Even when many periods are overdue, one poll is one run.
        DeadlineTimer late = DeadlineTimer.Periodic(0, 100);
        True(late.Poll(10_000), "overdue timer fires");
        False(late.Poll(10_000), "but only once for that frame");
    }

    private static void Deadline_SkipsMissedPeriodsAfterHitch()
    {
        // A 2 s freeze with a 100 ms period must run the job once, not twenty times.
        long period = 100;
        DeadlineTimer t = DeadlineTimer.Periodic(0, period);
        int runs = 0;
        for (long now = 16; now <= 200; now += 16)
        {
            if (t.Poll(now))
            {
                runs++;
            }
        }

        long beforeHitch = t.SkippedPeriods;

        long afterFreeze = 200 + 2000;
        int runsOnFrame = 0;
        while (t.Poll(afterFreeze))
        {
            runsOnFrame++;
        }

        Equal(1, runsOnFrame, "one run on the frame after the freeze (looping Poll at one instant fires once)");
        True(t.SkippedPeriods - beforeHitch >= 19, "about twenty periods reported as skipped, got " + (t.SkippedPeriods - beforeHitch));
        True(runs >= 1, "ran normally before the hitch");
    }

    private static void Deadline_KeepsPhaseAfterHitch()
    {
        // Deadline fired at 100 (grid 100,200,...). Poll at 450: grid points 200,300,400 are lost,
        // the next one is 500 (not 550 = now + period).
        DeadlineTimer t = DeadlineTimer.Periodic(0, 100);
        True(t.Poll(450), "late poll fires");
        Equal(500L, t.Deadline);
        Equal(3L, t.SkippedPeriods);
    }

    private static void Deadline_LateByExactPeriodsSkipsStrictlyPastNow()
    {
        // now == deadline + period: the next grid point equals now, so it is already due and must
        // be skipped, otherwise the next frame would run the job twice in a row.
        DeadlineTimer t = DeadlineTimer.Periodic(0, 100);
        True(t.Poll(200), "poll exactly one period late");
        Equal(300L, t.Deadline, "next deadline is strictly after now");
        Equal(1L, t.SkippedPeriods);
        False(t.Poll(200), "not due again at the same instant");

        long skipped;
        Equal(100L + 100, DeadlineTimer.NextDeadline(100, 100, 150, out skipped), "within a period: plain +period");
        Equal(0L, skipped);
        Equal(300L, DeadlineTimer.NextDeadline(100, 100, 200, out skipped), "now == next grid point is skipped");
        Equal(1L, skipped);
        Equal(300L, DeadlineTimer.NextDeadline(100, 100, 299, out skipped));
        Equal(1L, skipped);
        Equal(400L, DeadlineTimer.NextDeadline(100, 100, 300, out skipped));
        Equal(2L, skipped);
    }

    private static void Deadline_RunsPlusSkippedCoverEveryGridPoint()
    {
        // Invariant with an irregular frame sequence containing hitches: every grid point at or
        // before the last poll is either run or counted as skipped, and nothing is run twice.
        DeadlineTimer t = DeadlineTimer.Periodic(0, 100);
        long[] steps = { 16, 16, 16, 450, 16, 1000, 1, 99, 100, 101, 3000, 16, 16, 7, 250 };
        long now = 0;
        long runs = 0;
        foreach (long step in steps)
        {
            now += step;
            if (t.Poll(now))
            {
                runs++;
            }

            Equal(0L, t.Deadline % 100, "stays on grid at t=" + now);
        }

        Equal(now / 100, runs + t.SkippedPeriods);
    }

    private static void Deadline_OneShotFiresOnceThenDisarms()
    {
        DeadlineTimer t = DeadlineTimer.OneShot(500, 250);
        Equal(750L, t.Deadline);
        False(t.IsPeriodic, "one-shot is not periodic");
        False(t.Poll(749), "not yet");
        True(t.Poll(750), "fires on deadline");
        False(t.IsArmed, "disarmed after firing");
        False(t.Poll(10_000), "never again");
    }

    private static void Deadline_OneShotNegativeDelayIsDueImmediately()
    {
        DeadlineTimer t = DeadlineTimer.OneShot(500, -42);
        Equal(500L, t.Deadline);
        True(t.Poll(500), "due at creation time");
    }

    private static void Deadline_DisarmStopsFiring()
    {
        DeadlineTimer t = DeadlineTimer.Periodic(0, 100);
        t.Disarm();
        False(t.IsArmed, "disarmed");
        False(t.Poll(1_000_000), "a disarmed timer never fires");
    }

    private static void Deadline_PeriodFloorsAtOneTick()
    {
        DeadlineTimer t = DeadlineTimer.Periodic(10, 0);
        Equal(1L, t.Period);
        Equal(11L, t.Deadline);
        DeadlineTimer negative = DeadlineTimer.PeriodicFrom(10, -5);
        Equal(1L, negative.Period);
    }

    private static void Deadline_PeriodicFromUsesExplicitFirstDeadline()
    {
        DeadlineTimer t = DeadlineTimer.PeriodicFrom(250, 100);
        Equal(250L, t.Deadline);
        False(t.Poll(249), "before the explicit first deadline");
        True(t.Poll(250), "at it");
        Equal(350L, t.Deadline);
    }

    private static void Deadline_NextDeadlineRejectsNonPositivePeriod()
    {
        long skipped;
        long ignored;
        Throws<ArgumentOutOfRangeException>(() => ignored = DeadlineTimer.NextDeadline(0, 0, 10, out skipped), "period 0");
        Throws<ArgumentOutOfRangeException>(() => ignored = DeadlineTimer.NextDeadline(0, -1, 10, out skipped), "period -1");
    }

    private static void Deadline_DefaultInstanceNeverFires()
    {
        DeadlineTimer t = default;
        False(t.IsArmed, "default is disarmed");
        False(t.Poll(long.MaxValue), "default never fires");
    }
}
