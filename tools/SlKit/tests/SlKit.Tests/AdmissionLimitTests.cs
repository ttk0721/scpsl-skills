using System;
using System.Threading;
using System.Threading.Tasks;
using SlKit.Core;
using static SlKit.TestSupport.TestKit;

namespace SlKit.Tests;

/// <summary>Tests of AdmissionLimit: how many requests an SlHttp client accepts.</summary>
internal static class AdmissionLimitTests
{
    internal static void Register()
    {
        // Admission limit (SlHttp client capacity)
        Run(nameof(Admission_RefusesPastTheLimitAndReleases), Admission_RefusesPastTheLimitAndReleases);
        Run(nameof(Admission_HugeSumDoesNotWrapNegative), Admission_HugeSumDoesNotWrapNegative);
        Run(nameof(Admission_ConcurrentCallersNeverExceedTheLimit), Admission_ConcurrentCallersNeverExceedTheLimit);
    }

    // ---------------------------------------------------------------------------------------
    // Admission limit. Regression for "MaxConcurrency + MaxQueued wraps negative for a huge queue
    // setting and every SlHttp request is then refused".
    // ---------------------------------------------------------------------------------------

    private static void Admission_RefusesPastTheLimitAndReleases()
    {
        AdmissionLimit limit = new AdmissionLimit(3);
        Equal(3L, limit.Limit);
        True(limit.TryEnter());
        True(limit.TryEnter());
        True(limit.TryEnter());
        False(limit.TryEnter(), "fourth is refused");
        Equal(3, limit.Count, "a refusal does not leak a slot");
        Equal(1L, limit.RefusedCount);

        limit.Leave();
        True(limit.TryEnter(), "a freed slot is usable again");
        Equal(3, limit.Count);

        AdmissionLimit none = new AdmissionLimit(0);
        False(none.TryEnter(), "limit 0 refuses everything");
        Throws<ArgumentOutOfRangeException>(() => new AdmissionLimit(-1), "negative limit");
    }

    private static void Admission_HugeSumDoesNotWrapNegative()
    {
        // The sum SlHttpClient builds: MaxConcurrency 4 + MaxQueued int.MaxValue. As an int it is negative.
        int maxConcurrency = 4;
        int maxQueued = int.MaxValue;
        True(unchecked(maxConcurrency + maxQueued) < 0, "the int sum really wraps (the old bug)");

        AdmissionLimit limit = new AdmissionLimit((long)maxConcurrency + maxQueued);
        True(limit.TryEnter(), "the first request is admitted");
        True(limit.TryEnter(), "and the second");
        Equal(0L, limit.RefusedCount);
    }

    private static void Admission_ConcurrentCallersNeverExceedTheLimit()
    {
        AdmissionLimit limit = new AdmissionLimit(10);
        int admitted = 0;
        using (Barrier start = new Barrier(8))
        {
            Task[] tasks = new Task[8];
            for (int t = 0; t < tasks.Length; t++)
            {
                tasks[t] = Task.Run(() =>
                {
                    start.SignalAndWait();
                    for (int i = 0; i < 5_000; i++)
                    {
                        if (limit.TryEnter())
                        {
                            Interlocked.Increment(ref admitted);
                        }
                    }
                });
            }

            Task.WaitAll(tasks);
        }

        Equal(10, admitted, "never more than the limit while nobody leaves");
        Equal(10, limit.Count);
        Equal(8L * 5_000 - 10, limit.RefusedCount);
    }
}
