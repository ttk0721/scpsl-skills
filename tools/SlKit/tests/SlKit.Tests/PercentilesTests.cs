using System;
using System.Globalization;
using SlKit.Core;
using static SlKit.TestSupport.TestKit;

namespace SlKit.Tests;

/// <summary>Tests of Percentiles, the percent-above-threshold helpers and the frame budget scenario built on them.</summary>
internal static class PercentilesTests
{
    internal static void Register()
    {
        // Percentiles
        Run(nameof(Percentiles_OneToHundred), Percentiles_OneToHundred);
        Run(nameof(Percentiles_OneToTen), Percentiles_OneToTen);
        Run(nameof(Percentiles_SingleSample), Percentiles_SingleSample);
        Run(nameof(Percentiles_TwoSamples), Percentiles_TwoSamples);
        Run(nameof(Percentiles_SmallSamplesUseRealSamples), Percentiles_SmallSamplesUseRealSamples);
        Run(nameof(Percentiles_NearestRankMatchesIntegerMathForEveryRank), Percentiles_NearestRankMatchesIntegerMathForEveryRank);
        Run(nameof(Percentiles_SortsUnsortedInputInPlaceAndHonoursCount), Percentiles_SortsUnsortedInputInPlaceAndHonoursCount);
        Run(nameof(Percentiles_EmptyAndArgumentChecks), Percentiles_EmptyAndArgumentChecks);
        Run(nameof(Percentiles_OutlierMovesTailNotMedian), Percentiles_OutlierMovesTailNotMedian);
        Run(nameof(Percentiles_SummaryFormatsWithInvariantCulture), Percentiles_SummaryFormatsWithInvariantCulture);

        // Thresholds
        Run(nameof(Threshold_PercentAboveIsStrict), Threshold_PercentAboveIsStrict);
        Run(nameof(Threshold_PercentAboveIgnoresOrderAndTail), Threshold_PercentAboveIgnoresOrderAndTail);
        Run(nameof(Threshold_MeanHelper), Threshold_MeanHelper);
        Run(nameof(Threshold_FrameBudgetScenario), Threshold_FrameBudgetScenario);
    }

    // ---------------------------------------------------------------------------------------
    // Percentiles
    // ---------------------------------------------------------------------------------------

    private static void Percentiles_OneToHundred()
    {
        double[] data = new double[100];
        for (int i = 0; i < 100; i++)
        {
            data[i] = 100 - i; // descending on purpose: summarising must sort
        }

        DistributionSummary s = Percentiles.SummarizeInPlace(data, 100);
        Equal(100, s.Count);
        Near(1, s.Min);
        Near(50.5, s.Mean);
        Near(50, s.P50, 0, "p50 is rank 50");
        Near(95, s.P95, 0, "p95 is rank 95");
        Near(99, s.P99, 0, "p99 is rank 99");
        Near(100, s.Max);
    }

    private static void Percentiles_OneToTen()
    {
        double[] data = { 10, 3, 7, 1, 9, 2, 8, 4, 6, 5 };
        DistributionSummary s = Percentiles.SummarizeInPlace(data, data.Length);
        Near(5.5, s.Mean);
        Near(5, s.P50, 0, "ceil(0.50 * 10) = rank 5");
        Near(10, s.P95, 0, "ceil(9.5) = rank 10");
        Near(10, s.P99, 0, "ceil(9.9) = rank 10");
        Near(1, s.Min);
        Near(10, s.Max);
    }

    private static void Percentiles_SingleSample()
    {
        double[] data = { 42 };
        DistributionSummary s = Percentiles.SummarizeInPlace(data, 1);
        Equal(1, s.Count);
        Near(42, s.Min);
        Near(42, s.Mean);
        Near(42, s.P50);
        Near(42, s.P95);
        Near(42, s.P99);
        Near(42, s.Max);
    }

    private static void Percentiles_TwoSamples()
    {
        double[] data = { 20, 10 };
        DistributionSummary s = Percentiles.SummarizeInPlace(data, 2);
        Near(10, s.P50, 0, "ceil(1.0) = rank 1: the lower sample");
        Near(20, s.P95, 0, "ceil(1.9) = rank 2");
        Near(20, s.P99, 0, "ceil(1.98) = rank 2");
        Near(15, s.Mean);
    }

    private static void Percentiles_SmallSamplesUseRealSamples()
    {
        // The point of nearest rank: reported percentiles are values that were actually measured,
        // never an interpolation. With 5 samples, p99 is the max and p50 is the middle one.
        double[] data = { 16, 17, 18, 300, 16 };
        DistributionSummary s = Percentiles.SummarizeInPlace(data, 5);
        Near(17, s.P50, 0, "ceil(2.5) = rank 3 of 16,16,17,18,300 -> 17");
        Near(300, s.P95, 0, "ceil(4.75) = rank 5");
        Near(300, s.P99);
        Near(300, s.Max);
        foreach (double reported in new[] { s.P50, s.P95, s.P99 })
        {
            True(Array.IndexOf(data, reported) >= 0, "percentile " + reported + " is a real sample");
        }
    }

    private static void Percentiles_NearestRankMatchesIntegerMathForEveryRank()
    {
        // rank = ceil(p * n / 100) computed in integers is the textbook definition. A double
        // implementation that evaluates p / 100.0 * n can land a hair above an exact integer
        // (0.07 * 100 = 7.000000000000001) and ceil it up a whole rank.
        const int MaxCount = 300;
        double[] sorted = new double[MaxCount];
        for (int i = 0; i < MaxCount; i++)
        {
            sorted[i] = i + 1; // value == rank
        }

        int mismatches = 0;
        string firstMismatch = null;
        for (int count = 1; count <= MaxCount; count++)
        {
            for (int p = 1; p <= 99; p++)
            {
                int expectedRank = (p * count + 99) / 100; // integer ceil(p * count / 100)
                if (expectedRank < 1)
                {
                    expectedRank = 1;
                }

                double actual = Percentiles.NearestRankSorted(sorted, count, p);
                if (actual != expectedRank)
                {
                    mismatches++;
                    if (firstMismatch == null)
                    {
                        firstMismatch = "count=" + count + " p=" + p + " expected rank " + expectedRank + " got " + actual;
                    }
                }
            }
        }

        Equal(0, mismatches, "nearest-rank mismatches vs integer math; first: " + firstMismatch);
    }

    private static void Percentiles_SortsUnsortedInputInPlaceAndHonoursCount()
    {
        // Only the first `count` elements are samples; the tail is scratch and must be untouched.
        double[] data = { 5, 1, 4, 2, 3, -999, -999 };
        DistributionSummary s = Percentiles.SummarizeInPlace(data, 5);
        Near(1, s.Min);
        Near(5, s.Max);
        Near(3, s.Mean);
        SequenceEqual(new double[] { 1, 2, 3, 4, 5, -999, -999 }, data);
    }

    private static void Percentiles_EmptyAndArgumentChecks()
    {
        DistributionSummary empty = Percentiles.SummarizeInPlace(new double[4], 0);
        Equal(0, empty.Count);
        Near(0, empty.Max);
        Near(0, empty.P99);

        Near(0, Percentiles.NearestRankSorted(new double[4], 0, 50), 0, "empty nearest rank");
        Near(0, Percentiles.Mean(new double[4], 0), 0, "empty mean");
        Near(0, Percentiles.PercentAbove(new double[4], 0, 1), 0, "empty percent above");

        Throws<ArgumentNullException>(() => Percentiles.SummarizeInPlace(null, 1), "null array");
        Throws<ArgumentOutOfRangeException>(() => Percentiles.SummarizeInPlace(new double[2], 3), "count beyond array");

        double[] sorted = { 1, 2, 3 };
        Near(1, Percentiles.NearestRankSorted(sorted, 3, 0), 0, "p0 is the minimum");
        Near(3, Percentiles.NearestRankSorted(sorted, 3, 100), 0, "p100 is the maximum");
        Near(1, Percentiles.NearestRankSorted(sorted, 3, -5), 0, "negative percentile clamps to min");
        Near(3, Percentiles.NearestRankSorted(sorted, 3, 250), 0, "percentile over 100 clamps to max");
    }

    private static void Percentiles_OutlierMovesTailNotMedian()
    {
        // 99 frames of 16 ms and one 300 ms hitch: the mean barely moves (the reason a smoothed TPS
        // hides hitches), p99 and max expose it. Nearest-rank p99 of 100 samples is rank 99.
        double[] data = new double[100];
        for (int i = 0; i < 99; i++)
        {
            data[i] = 16;
        }

        data[99] = 300;

        DistributionSummary s = Percentiles.SummarizeInPlace(data, 100);
        Near(16, s.P50);
        Near(16, s.P95);
        Near(16, s.P99, 0, "rank 99 is still a 16 ms frame");
        Near(300, s.Max);
        Near(18.84, s.Mean, 1e-9, "the mean only moves by under 3 ms");
    }

    private static void Percentiles_SummaryFormatsWithInvariantCulture()
    {
        CultureInfo previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("pl-PL"); // decimal comma
            double[] data = { 1.5, 2.5 };
            string text = Percentiles.SummarizeInPlace(data, 2).ToString();
            True(text.Contains("1.50"), "decimal point, not comma: " + text);
            True(text.Contains("n=2"), "includes the sample count: " + text);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    // ---------------------------------------------------------------------------------------
    // Threshold percentages
    // ---------------------------------------------------------------------------------------

    private static void Threshold_PercentAboveIsStrict()
    {
        double[] data = { 10, 50, 50, 50.0001, 100 };
        Near(40, Percentiles.PercentAbove(data, 5, 50), 1e-12, "only values strictly above 50 count: 50.0001 and 100");
        Near(80, Percentiles.PercentAbove(data, 5, 49.9), 1e-12, "50, 50, 50.0001 and 100 are above 49.9");
        Near(0, Percentiles.PercentAbove(data, 5, 100), 0, "nothing exceeds the maximum");
        Near(100, Percentiles.PercentAbove(data, 5, 9.99), 1e-12, "everything exceeds a tiny threshold");
    }

    private static void Threshold_PercentAboveIgnoresOrderAndTail()
    {
        double[] data = { 100, 1, 100, 1, 9999, 9999 }; // tail beyond count must be ignored
        Near(50, Percentiles.PercentAbove(data, 4, 50), 1e-12, "2 of the first 4");
        Near(0, Percentiles.PercentAbove(data, 0, 50), 0, "count 0");
    }

    private static void Threshold_MeanHelper()
    {
        double[] data = { 2, 4, 6, 1000 };
        Near(4, Percentiles.Mean(data, 3), 1e-12, "only the first 3");
        Near(253, Percentiles.Mean(data, 4), 1e-12);
    }

    private static void Threshold_FrameBudgetScenario()
    {
        // 200 frames: 190 at 16 ms, 7 at 60 ms (over 50, under 100), 3 at 120 ms (over both).
        double[] frames = new double[200];
        int index = 0;
        for (int i = 0; i < 190; i++)
        {
            frames[index++] = 16;
        }

        for (int i = 0; i < 7; i++)
        {
            frames[index++] = 60;
        }

        for (int i = 0; i < 3; i++)
        {
            frames[index++] = 120;
        }

        Near(5.0, Percentiles.PercentAbove(frames, 200, 50), 1e-9, "10 of 200 are over 50 ms");
        Near(1.5, Percentiles.PercentAbove(frames, 200, 100), 1e-9, "3 of 200 are over 100 ms");
    }
}
