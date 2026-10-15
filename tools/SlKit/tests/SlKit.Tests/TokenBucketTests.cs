using System;
using SlKit.Core;
using static SlKit.TestSupport.TestKit;

namespace SlKit.Tests;

/// <summary>Tests of TokenBucket and ErrorLogThrottle: burst, refill and the count of refused calls.</summary>
internal static class TokenBucketTests
{
    internal static void Register()
    {
        // Token bucket
        Run(nameof(Bucket_StartsFullAndAllowsBurstThenDenies), Bucket_StartsFullAndAllowsBurstThenDenies);
        Run(nameof(Bucket_RefillsAtConfiguredRate), Bucket_RefillsAtConfiguredRate);
        Run(nameof(Bucket_NeverExceedsCapacityAfterLongIdle), Bucket_NeverExceedsCapacityAfterLongIdle);
        Run(nameof(Bucket_SustainedRateMatchesRefill), Bucket_SustainedRateMatchesRefill);
        Run(nameof(Bucket_CostGreaterThanOne), Bucket_CostGreaterThanOne);
        Run(nameof(Bucket_DeniedCountAccumulatesAndResets), Bucket_DeniedCountAccumulatesAndResets);
        Run(nameof(Bucket_ZeroRefillNeverRecovers), Bucket_ZeroRefillNeverRecovers);
        Run(nameof(Bucket_IgnoresTimeGoingBackwards), Bucket_IgnoresTimeGoingBackwards);
        Run(nameof(Bucket_ConstructorValidation), Bucket_ConstructorValidation);
        Run(nameof(ErrorLogThrottle_LetsABurstThroughThenReportsWhatWasSuppressed), ErrorLogThrottle_LetsABurstThroughThenReportsWhatWasSuppressed);
    }

    // ---------------------------------------------------------------------------------------
    // Token bucket. Time is in Stopwatch ticks; one second is StopwatchTime.Frequency ticks.
    // ---------------------------------------------------------------------------------------

    private static long Seconds(double s) => (long)(s * StopwatchTime.Frequency);

    private static void Bucket_StartsFullAndAllowsBurstThenDenies()
    {
        long t0 = 1_000_000;
        TokenBucket bucket = new TokenBucket(5, 1, t0);
        Near(5, bucket.Tokens, 0, "starts full");
        Near(5, bucket.Capacity, 0);
        Near(1, bucket.RefillPerSecond, 0);

        for (int i = 0; i < 5; i++)
        {
            True(bucket.TryTake(t0), "burst token " + (i + 1));
        }

        False(bucket.TryTake(t0), "burst exhausted at the same instant");
        False(bucket.TryTake(t0 + Seconds(0.5)), "half a token is not enough");
    }

    private static void Bucket_RefillsAtConfiguredRate()
    {
        TokenBucket bucket = new TokenBucket(5, 2, 0);
        for (int i = 0; i < 5; i++)
        {
            True(bucket.TryTake(0), "drain " + i);
        }

        // 2 tokens/s: after 0.6 s there are 1.2 tokens -> one take succeeds, the next does not.
        True(bucket.TryTake(Seconds(0.6)), "one token after 0.6 s");
        False(bucket.TryTake(Seconds(0.6)), "second token not yet there");
        Near(0.2, bucket.Tokens, 1e-6, "0.2 tokens left");

        // 0.4 s more: 0.2 + 0.8 = 1.0. Use 0.45 s to stay clear of float rounding at the boundary.
        True(bucket.TryTake(Seconds(1.05)), "next token after another 0.45 s");
    }

    private static void Bucket_NeverExceedsCapacityAfterLongIdle()
    {
        TokenBucket bucket = new TokenBucket(3, 10, 0);
        bucket.TryTake(0);
        bucket.Refill(Seconds(3600));
        Near(3, bucket.Tokens, 0, "capped at capacity after an hour of idle");

        int granted = 0;
        for (int i = 0; i < 10; i++)
        {
            if (bucket.TryTake(Seconds(3600)))
            {
                granted++;
            }
        }

        Equal(3, granted, "an hour of idle gives a burst of exactly capacity, not more");
    }

    private static void Bucket_SustainedRateMatchesRefill()
    {
        // Hammer the bucket every 10 ms for 60 s with capacity 10 and 5 tokens/s refill.
        // Allowed = initial burst + refill over the window, to within one token.
        TokenBucket bucket = new TokenBucket(10, 5, 0);
        int allowed = 0;
        for (long ms = 10; ms <= 60_000; ms += 10)
        {
            if (bucket.TryTake(Seconds(ms / 1000.0)))
            {
                allowed++;
            }
        }

        double expected = 10 + 5 * 60.0;
        True(Math.Abs(allowed - expected) <= 1, "allowed " + allowed + ", expected about " + expected);
    }

    private static void Bucket_CostGreaterThanOne()
    {
        TokenBucket bucket = new TokenBucket(5, 0, 0);
        True(bucket.TryTake(0, 3), "cost 3 of 5");
        Near(2, bucket.Tokens, 1e-12);
        False(bucket.TryTake(0, 3), "cost 3 of 2 refused");
        Near(2, bucket.Tokens, 1e-12, "a refused take consumes nothing");
        True(bucket.TryTake(0, 2), "exactly the remaining 2");
        False(bucket.TryTake(0, 0.5), "empty");

        TokenBucket small = new TokenBucket(2, 0, 0);
        False(small.TryTake(0, 2.5), "cost above capacity can never be granted");
        Equal(1L, small.DeniedCount);
    }

    private static void Bucket_DeniedCountAccumulatesAndResets()
    {
        TokenBucket bucket = new TokenBucket(1, 0, 0);
        True(bucket.TryTake(0));
        False(bucket.TryTake(0));
        False(bucket.TryTake(0));
        False(bucket.TryTake(0));
        Equal(3L, bucket.DeniedCount);
        Equal(3L, bucket.TakeDeniedCount(), "returns the count");
        Equal(0L, bucket.DeniedCount, "and resets it");
        Equal(0L, bucket.TakeDeniedCount());
        False(bucket.TryTake(0));
        Equal(1L, bucket.DeniedCount);
    }

    private static void Bucket_ZeroRefillNeverRecovers()
    {
        TokenBucket bucket = new TokenBucket(2, 0, 0);
        True(bucket.TryTake(0));
        True(bucket.TryTake(0));
        False(bucket.TryTake(Seconds(100000)), "no refill means no recovery");
    }

    private static void Bucket_IgnoresTimeGoingBackwards()
    {
        TokenBucket bucket = new TokenBucket(2, 1, Seconds(100));
        True(bucket.TryTake(Seconds(100)));
        True(bucket.TryTake(Seconds(100)));

        // A timestamp before the last refill must neither add nor remove tokens.
        False(bucket.TryTake(Seconds(50)), "earlier timestamp yields no tokens");
        Near(0, bucket.Tokens, 0);

        // And it must not rewind the refill clock: 1 s after the real last refill gives 1 token.
        True(bucket.TryTake(Seconds(101.2)), "1.2 s after the last real refill");
    }

    private static void ErrorLogThrottle_LetsABurstThroughThenReportsWhatWasSuppressed()
    {
        TokenBucket bucket = null;
        True(ErrorLogThrottle.TryAcquire(ref bucket, 2, 1, 0, out long suppressed), "first line");
        True(bucket != null, "the bucket is created on first use");
        Equal(0L, suppressed, "nothing held back yet");
        True(ErrorLogThrottle.TryAcquire(ref bucket, 2, 1, 0, out _), "second line of the burst");

        False(ErrorLogThrottle.TryAcquire(ref bucket, 2, 1, 0, out _), "burst used up");
        False(ErrorLogThrottle.TryAcquire(ref bucket, 2, 1, Seconds(0.5), out suppressed), "half a token is not enough");
        Equal(0L, suppressed, "a refused call reports nothing");

        True(ErrorLogThrottle.TryAcquire(ref bucket, 2, 1, Seconds(1), out suppressed), "a line again after 1 s");
        Equal(2L, suppressed, "the two refused calls are reported once");
        True(ErrorLogThrottle.TryAcquire(ref bucket, 2, 1, Seconds(2), out suppressed), "and the next line");
        Equal(0L, suppressed, "the count starts again from zero");
    }

    private static void Bucket_ConstructorValidation()
    {
        Throws<ArgumentOutOfRangeException>(() => new TokenBucket(0, 1, 0), "capacity 0");
        Throws<ArgumentOutOfRangeException>(() => new TokenBucket(-1, 1, 0), "negative capacity");
        Throws<ArgumentOutOfRangeException>(() => new TokenBucket(1, -0.1, 0), "negative refill");
    }
}
