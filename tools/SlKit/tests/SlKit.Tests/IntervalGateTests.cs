using System;
using System.Threading;
using System.Threading.Tasks;
using SlKit.Core;
using static SlKit.TestSupport.TestKit;

namespace SlKit.Tests;

/// <summary>Tests of IntervalGate: one pass per interval from any number of threads.</summary>
internal static class IntervalGateTests
{
    internal static void Register()
    {
        // Interval gate (MainThread drop warnings)
        Run(nameof(Gate_FirstCallPassesThenOncePerInterval), Gate_FirstCallPassesThenOncePerInterval);
        Run(nameof(Gate_ReportsBlockedCountSinceLastPass), Gate_ReportsBlockedCountSinceLastPass);
        Run(nameof(Gate_ConcurrentCallersGetExactlyOnePass), Gate_ConcurrentCallersGetExactlyOnePass);
        Run(nameof(Gate_ZeroIntervalAndHugeTimestampsAreSafe), Gate_ZeroIntervalAndHugeTimestampsAreSafe);
    }

    // ---------------------------------------------------------------------------------------
    // Interval gate. Regression for "MainThread.Post drops silently": the warning must fire on
    // the first drop and then at most once per interval, from any number of threads.
    // ---------------------------------------------------------------------------------------

    private static void Gate_FirstCallPassesThenOncePerInterval()
    {
        IntervalGate gate = new IntervalGate(100);
        True(gate.TryPass(5000, out _), "first call ever passes");
        False(gate.TryPass(5000, out _), "same instant");
        False(gate.TryPass(5099, out _), "one tick before the interval ends");
        True(gate.TryPass(5100, out _), "exactly one interval later");
        False(gate.TryPass(5150, out _), "inside the next interval");
        True(gate.TryPass(9000, out _), "long after");
        Equal(100L, gate.Interval);
        Throws<ArgumentOutOfRangeException>(() => new IntervalGate(-1), "negative interval");
    }

    private static void Gate_ReportsBlockedCountSinceLastPass()
    {
        IntervalGate gate = new IntervalGate(100);
        long blocked;
        True(gate.TryPass(0, out blocked));
        Equal(0L, blocked, "nothing blocked before the first pass");
        for (int i = 1; i <= 7; i++)
        {
            False(gate.TryPass(i, out blocked));
        }

        Equal(0L, blocked, "a refused call reports 0");
        Equal(7L, gate.BlockedSinceLastPass);
        True(gate.TryPass(100, out blocked));
        Equal(7L, blocked, "the pass reports the 7 refused calls");
        Equal(0L, gate.BlockedSinceLastPass, "and resets the count");
    }

    private static void Gate_ConcurrentCallersGetExactlyOnePass()
    {
        // 8 threads hammer the gate at the same "now": exactly one may log.
        IntervalGate gate = new IntervalGate(1_000_000);
        int passes = 0;
        using (Barrier start = new Barrier(8))
        {
            Task[] tasks = new Task[8];
            for (int t = 0; t < tasks.Length; t++)
            {
                tasks[t] = Task.Run(() =>
                {
                    start.SignalAndWait();
                    for (int i = 0; i < 10_000; i++)
                    {
                        if (gate.TryPass(42, out _))
                        {
                            Interlocked.Increment(ref passes);
                        }
                    }
                });
            }

            Task.WaitAll(tasks);
        }

        Equal(1, passes, "one pass for the whole burst");
        Equal(8L * 10_000 - 1, gate.BlockedSinceLastPass, "every other call counted");
    }

    private static void Gate_ZeroIntervalAndHugeTimestampsAreSafe()
    {
        IntervalGate zero = new IntervalGate(0);
        True(zero.TryPass(10, out _));
        True(zero.TryPass(10, out _), "zero interval lets every call through");

        IntervalGate gate = new IntervalGate(long.MaxValue);
        True(gate.TryPass(long.MaxValue - 5, out _), "no overflow on the next-allowed sum");
        False(gate.TryPass(long.MaxValue - 1, out _), "saturated at MaxValue, not wrapped into the past");
        False(gate.TryPass(0, out _), "a wrapped sum would have let this through");
    }
}
