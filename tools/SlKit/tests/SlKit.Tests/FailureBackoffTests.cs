using System;
using SlKit.Core;
using static SlKit.TestSupport.TestKit;

namespace SlKit.Tests;

/// <summary>Tests of FailureBackoff: when a failing job is disabled and when it comes back.</summary>
internal static class FailureBackoffTests
{
    internal static void Register()
    {
        // Job failure backoff
        Run(nameof(Backoff_DelayDoublesUpToTheCap), Backoff_DelayDoublesUpToTheCap);
        Run(nameof(Backoff_DelayNeverOverflows), Backoff_DelayNeverOverflows);
        Run(nameof(Backoff_DisablesOnThresholdAndReenablesWhenDue), Backoff_DisablesOnThresholdAndReenablesWhenDue);
        Run(nameof(Backoff_RepeatedDisablesGrowAndSuccessResets), Backoff_RepeatedDisablesGrowAndSuccessResets);
        Run(nameof(Backoff_PermanentPolicyNeverReenablesByItself), Backoff_PermanentPolicyNeverReenablesByItself);
        Run(nameof(Backoff_SuccessBeforeThresholdResetsCount), Backoff_SuccessBeforeThresholdResetsCount);
    }

    // ---------------------------------------------------------------------------------------
    // Failure backoff. Regression for "a job disabled after 5 failures is dead until restart".
    // ---------------------------------------------------------------------------------------

    private static void Backoff_DelayDoublesUpToTheCap()
    {
        // 30 s base, 30 min cap, in seconds as ticks: 30, 60, 120, 240, 480, 960, 1800, 1800...
        long[] expected = { 30, 60, 120, 240, 480, 960, 1800, 1800, 1800 };
        for (int streak = 1; streak <= expected.Length; streak++)
        {
            Equal(expected[streak - 1], FailureBackoff.DelayFor(streak, 30, 1800), "streak " + streak);
        }

        Equal(30L, FailureBackoff.DelayFor(0, 30, 1800), "streak 0 counts as the first");
        Equal(50L, FailureBackoff.DelayFor(3, 50, 10), "cap below base acts as the base");
        Equal(1L, FailureBackoff.DelayFor(1, 0, 0), "base floors at one tick");
    }

    private static void Backoff_DelayNeverOverflows()
    {
        long cap = StopwatchTime.MaxDurationTicks;
        long delay = FailureBackoff.DelayFor(int.MaxValue, StopwatchTime.FromTimeSpan(TimeSpan.FromSeconds(30)), cap);
        Equal(cap, delay, "huge streak saturates at the cap");
        Equal(long.MaxValue, FailureBackoff.DelayFor(200, long.MaxValue / 3, long.MaxValue), "no wrap near long.MaxValue");

        FailureBackoff b = default;
        for (int i = 0; i < 5; i++)
        {
            b.RecordFailure(long.MaxValue - 10, 5, JobFailurePolicy.Backoff, 100, 100);
        }

        True(b.IsDisabled);
        Equal(FailureBackoff.Never, b.ReenableAt, "now + delay past MaxValue saturates instead of wrapping into the past");
    }

    private static void Backoff_DisablesOnThresholdAndReenablesWhenDue()
    {
        FailureBackoff b = default;
        False(b.IsDisabled, "default is enabled");
        Equal(FailureBackoff.Never, b.ReenableAt, "default schedules nothing");

        for (int i = 1; i <= 4; i++)
        {
            Equal((int)FailureVerdict.Counted, (int)b.RecordFailure(1000, 5, JobFailurePolicy.Backoff, 30, 1800), "failure " + i);
        }

        Equal((int)FailureVerdict.DisabledWithBackoff, (int)b.RecordFailure(1000, 5, JobFailurePolicy.Backoff, 30, 1800), "fifth disables");
        True(b.IsDisabled);
        Equal(1030L, b.ReenableAt);
        Equal(30L, b.LastDelay);
        Equal(1L, b.TimesDisabled);
        Equal(1, b.DisableStreak);

        False(b.IsReenableDue(1029), "one tick early");
        True(b.IsReenableDue(1030), "due on time");
        b.Reenable();
        False(b.IsDisabled);
        Equal(0, b.ConsecutiveFailures, "count restarts after re-enable");
        Equal(FailureBackoff.Never, b.ReenableAt);
        False(b.IsReenableDue(5000), "an enabled job is never 'due'");
    }

    private static void Backoff_RepeatedDisablesGrowAndSuccessResets()
    {
        FailureBackoff b = default;
        long now = 0;
        long[] delays = new long[4];
        for (int round = 0; round < 4; round++)
        {
            for (int i = 0; i < 3; i++)
            {
                b.RecordFailure(now, 3, JobFailurePolicy.Backoff, 30, 100);
            }

            True(b.IsDisabled, "disabled in round " + round);
            delays[round] = b.LastDelay;
            now = b.ReenableAt;
            b.Reenable();
        }

        SequenceEqual(new long[] { 30, 60, 100, 100 }, delays, "30, doubled, then capped");
        Equal(4L, b.TimesDisabled);

        b.RecordSuccess();
        Equal(0, b.DisableStreak, "a success forgets the streak");
        for (int i = 0; i < 3; i++)
        {
            b.RecordFailure(now, 3, JobFailurePolicy.Backoff, 30, 100);
        }

        Equal(30L, b.LastDelay, "back to the short delay after recovering");
        Equal(5L, b.TimesDisabled, "lifetime count keeps growing");
    }

    private static void Backoff_PermanentPolicyNeverReenablesByItself()
    {
        FailureBackoff b = default;
        for (int i = 0; i < 4; i++)
        {
            b.RecordFailure(0, 5, JobFailurePolicy.DisablePermanently, 30, 1800);
        }

        Equal((int)FailureVerdict.DisabledPermanently, (int)b.RecordFailure(0, 5, JobFailurePolicy.DisablePermanently, 30, 1800));
        True(b.IsDisabled);
        Equal(FailureBackoff.Never, b.ReenableAt);
        False(b.IsReenableDue(long.MaxValue - 1), "never due");
        Equal((int)FailureVerdict.Counted, (int)b.RecordFailure(0, 5, JobFailurePolicy.DisablePermanently, 30, 1800), "no second disable while disabled");
        Equal(1L, b.TimesDisabled);

        b.Reenable();
        False(b.IsDisabled, "a manual re-enable works for permanent disables too");
    }

    private static void Backoff_SuccessBeforeThresholdResetsCount()
    {
        FailureBackoff b = default;
        for (int round = 0; round < 10; round++)
        {
            for (int i = 0; i < 4; i++)
            {
                b.RecordFailure(0, 5, JobFailurePolicy.Backoff, 30, 1800);
            }

            b.RecordSuccess();
        }

        False(b.IsDisabled, "4 failures then a success, ten times, never reaches 5 in a row");
        Equal(0L, b.TimesDisabled);
        Equal((int)FailureVerdict.DisabledWithBackoff, (int)FailFiveTimes(ref b), "five in a row after that still disables");
    }

    private static FailureVerdict FailFiveTimes(ref FailureBackoff b)
    {
        FailureVerdict last = FailureVerdict.Counted;
        for (int i = 0; i < 5; i++)
        {
            last = b.RecordFailure(0, 5, JobFailurePolicy.Backoff, 30, 1800);
        }

        return last;
    }
}
