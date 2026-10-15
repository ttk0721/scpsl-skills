using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using LabApi.Features.Console;
using static SlKit.TestSupport.TestKit;

namespace SlKit.Runtime.Tests;

/// <summary>
/// SlKit is one DLL shared by every plugin, so no static it exposes may let
/// one plugin change how the others log or how big the shared worker is.
/// </summary>
internal static partial class Program
{
    private static readonly Func<string> HotMessage = () => "hot";

    private static void Shared_NoPublicStaticSetterIsLeft()
    {
        foreach (Type type in new[] { typeof(Background), typeof(RateLimitedLog), typeof(RateLimitedLogger), typeof(SlHttp), typeof(SlHttpClient), typeof(BackgroundWorker) })
        {
            foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Static))
            {
                True(property.SetMethod == null || !property.SetMethod.IsPublic, type.Name + "." + property.Name + " has a public static setter");
            }

            foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                True(field.IsLiteral || field.IsInitOnly, type.Name + "." + field.Name + " is a writable public static field");
            }

            // Nothing that wipes shared state, either: no public static Reset on the shared table.
            MethodInfo reset = type.GetMethod("Reset", BindingFlags.Public | BindingFlags.Static);
            True(reset == null, type.Name + " has a public static Reset");
        }

#pragma warning disable CS0618 // the obsolete read-only member stays for plugins built against the earlier 0.2.0 binary
        Equal(SlHttp.Default.Timeout, SlHttp.DefaultTimeout, "the obsolete DefaultTimeout reads the default client's timeout");
#pragma warning restore CS0618
    }

    private static void Background_CapacityIsReadOnlyAndOwnersSizeTheirOwn()
    {
        Equal(Background.DefaultCapacity, Background.Capacity, "the shared worker keeps its size");
        BackgroundWorker own = Background.ForOwner("k1b-own", 7);
        Equal(7, own.Capacity, "a plugin sizes its own worker");
        Equal(Background.DefaultCapacity, Background.Capacity, "and the shared one is unchanged");
    }

    private static void Log_SharedTableHasFixedDefaults()
    {
        Equal(3, RateLimitedLog.Burst);
        Equal(TimeSpan.FromSeconds(10), RateLimitedLog.RefillInterval);
        Equal(TimeSpan.FromSeconds(30), RateLimitedLog.SummaryInterval);
    }

    private static void Log_SharedTableAllowsTheBurstThenSuppressesAndSummarises()
    {
        RateLimitedLog.Reset();
        Logger.Take();
        long before = RateLimitedLog.SuppressedTotal;
        for (int i = 0; i < 5; i++)
        {
            RateLimitedLog.Warn("k1.shared", () => "line");
        }

        List<string> lines = Logger.Take();
        Equal(3, lines.Count, "burst of 3: " + string.Join(" | ", lines));
        True(lines[0].StartsWith("WARN [k1.shared] line"), lines[0]);
        Equal(2L, RateLimitedLog.SuppressedTotal - before, "two refused");

        RateLimitedLog.FlushSuppressed();
        lines = Logger.Take();
        Equal(1, lines.Count, "one summary: " + string.Join(" | ", lines));
        True(lines[0].Contains("[k1.shared] 2 messages suppressed"), lines[0]);
    }

    private static void Log_OwnLoggerHasItsOwnLimitsAndKeys()
    {
        RateLimitedLog.Reset();
        Logger.Take();
        RateLimitedLogger strict = new RateLimitedLogger("k1-strict", burst: 1);
        RateLimitedLogger loose = new RateLimitedLogger("k1-loose", burst: 5);
        Equal(1, strict.Burst);
        Equal(5, loose.Burst);

        for (int i = 0; i < 6; i++)
        {
            strict.Info("same", () => "strict");
            loose.Info("same", () => "loose");
            RateLimitedLog.Info("same", () => "shared");
        }

        List<string> lines = Logger.Take();
        Equal(1, lines.FindAll(l => l.Contains("strict")).Count, "burst 1");
        Equal(5, lines.FindAll(l => l.Contains("loose")).Count, "burst 5, same key text, own bucket");
        Equal(3, lines.FindAll(l => l.Contains("shared")).Count, "the shared table keeps its default of 3");
        Equal(5L, strict.SuppressedTotal);
        Equal(1L, loose.SuppressedTotal);
        Equal(1, strict.KeyCount);
    }

    private static void Log_ResetOfOneLoggerLeavesOthersAlone()
    {
        RateLimitedLog.Reset();
        Logger.Take();
        RateLimitedLogger mine = new RateLimitedLogger("k1-mine", burst: 1);
        RateLimitedLogger theirs = new RateLimitedLogger("k1-theirs", burst: 1);

        for (int i = 0; i < 3; i++)
        {
            RateLimitedLog.Warn("k1.reset", () => "shared");
            theirs.Warn("k1.reset", () => "theirs");
            mine.Warn("k1.reset", () => "mine");
        }

        Logger.Take();
        mine.Reset();
        Equal(0, mine.KeyCount, "my table is empty");
        Equal(1, theirs.KeyCount, "theirs is untouched");
        True(RateLimitedLog.KeyCount >= 1, "and so is the shared table");

        // Their bucket is still empty (no new line) and their suppressed count survived.
        theirs.Warn("k1.reset", () => "theirs again");
        RateLimitedLog.Warn("k1.reset", () => "shared again");
        Equal(0, Logger.Take().Count, "no bucket was refilled by my Reset");
        theirs.FlushSuppressed();
        RateLimitedLog.FlushSuppressed();
        List<string> lines = Logger.Take();
        True(lines.Exists(l => l.Contains("[k1.reset] 3 messages suppressed")), "their count survived: " + string.Join(" | ", lines));
        True(lines.Exists(l => l.Contains("[k1.reset] 1 message suppressed")), "so did the shared table's: " + string.Join(" | ", lines));
        Equal(2, lines.Count, "one summary each: " + string.Join(" | ", lines));
    }

    private static void Log_SuppressedCallsSkipTheFactoryAndAllocateNothing()
    {
        RateLimitedLogger logger = new RateLimitedLogger("k1-hot", burst: 1, refillInterval: TimeSpan.FromHours(1));
        logger.Info("hot", HotMessage);
        Logger.Take();

        int factoryRuns = 0;
        Func<string> counting = () => { factoryRuns++; return "x"; };
        for (int i = 0; i < 100; i++)
        {
            logger.Info("hot", counting); // warm-up
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10000; i++)
        {
            logger.Info("hot", counting);
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Equal(0, factoryRuns, "a refused call never builds the string");
        Equal(0L, allocated, "a refused call allocates nothing (cached delegate, constant key)");
        Equal(0, Logger.Take().Count, "and logs nothing");
    }

    private static void Log_ConcurrentCallsNeverExceedTheBurst()
    {
        RateLimitedLogger logger = new RateLimitedLogger("k1-threads", burst: 3, refillInterval: TimeSpan.FromHours(1));
        Logger.Take();
        Thread[] threads = new Thread[8];
        for (int t = 0; t < threads.Length; t++)
        {
            threads[t] = new Thread(() =>
            {
                for (int i = 0; i < 1000; i++)
                {
                    logger.Error("contended", HotMessage);
                }
            });
            threads[t].Start();
        }

        foreach (Thread thread in threads)
        {
            thread.Join();
        }

        // A summary line ("N messages suppressed") may appear between the first allowed lines when a
        // sweep races them, so count the message lines only.
        List<string> lines = Logger.Take();
        Equal(3, lines.FindAll(l => l.EndsWith("[contended] hot")).Count, "exactly the burst got through: " + string.Join(" | ", lines));
        Equal(8000L - 3, logger.SuppressedTotal, "every other call was counted");
    }

    private static void Log_KeyTableIsBoundedPerLogger()
    {
        RateLimitedLogger logger = new RateLimitedLogger("k1-keys");
        for (int i = 0; i < RateLimitedLogger.MaxKeys + 50; i++)
        {
            logger.Info("key" + i, HotMessage);
        }

        Equal(RateLimitedLogger.MaxKeys + 1, logger.KeyCount, "512 keys plus the overflow key");
        Logger.Take();
    }

    private static void Log_FactoryThatThrowsIsLoggedNotThrown()
    {
        RateLimitedLogger logger = new RateLimitedLogger("k1-throw");
        Logger.Take();
        logger.Warn("boom", () => throw new InvalidOperationException("nope"));
        List<string> lines = Logger.Take();
        Equal(1, lines.Count);
        True(lines[0].Contains("message factory threw InvalidOperationException: nope"), lines[0]);
    }

    private static void Log_ConstructorRejectsInvalidLimitsInsteadOfClamping()
    {
        Throws<ArgumentException>(() => new RateLimitedLogger(null), "no name");
        Throws<ArgumentException>(() => new RateLimitedLogger(""), "empty name");
        Throws<ArgumentOutOfRangeException>(() => new RateLimitedLogger("x", burst: 0), "burst 0");
        Throws<ArgumentOutOfRangeException>(() => new RateLimitedLogger("x", refillInterval: TimeSpan.Zero), "refill 0");
        Throws<ArgumentOutOfRangeException>(() => new RateLimitedLogger("x", summaryInterval: TimeSpan.FromMilliseconds(500)), "summary below 1 s");
        RateLimitedLogger ok = new RateLimitedLogger("x", 2, TimeSpan.FromMilliseconds(1), TimeSpan.FromSeconds(1));
        Equal(2, ok.Burst);
        Equal(TimeSpan.FromMilliseconds(1), ok.RefillInterval);
        Equal(TimeSpan.FromSeconds(1), ok.SummaryInterval);
    }

    private static void Log_PeriodicSummaryRegistersOneJobPerLogger()
    {
        RateLimitedLogger one = new RateLimitedLogger("k1-periodic-a", summaryInterval: TimeSpan.FromSeconds(5));
        RateLimitedLogger two = new RateLimitedLogger("k1-periodic-b");
        one.EnablePeriodicSummary();
        one.EnablePeriodicSummary();
        two.EnablePeriodicSummary();
        List<string> mine;
        lock (SlClock.Registrations)
        {
            mine = SlClock.Registrations.FindAll(r => r.StartsWith("ratelimitedlog.summary.k1-periodic"));
        }

        Equal(2, mine.Count, "idempotent per logger: " + string.Join(" | ", mine));
        Equal("ratelimitedlog.summary.k1-periodic-a every 5 s", mine[0]);
        Equal("ratelimitedlog.summary.k1-periodic-b every 30 s", mine[1]);
    }
}
