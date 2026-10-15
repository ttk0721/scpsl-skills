using System;

namespace SlKit.Core;

/// <summary>
/// Percentile and threshold statistics over sample arrays, allocation-free.
/// </summary>
/// <remarks>
/// Uses the nearest-rank definition (the reported value is always a real sample, so "p99 = 250"
/// means some frame actually took 250 ms). Sorting is O(n log n): for a 3600-frame window do it
/// rarely or off the main thread on a copied array. Thread affinity: pure; safe on any thread
/// as long as the array is not shared with a writer.
/// </remarks>
public static class Percentiles
{
    /// <summary>
    /// Sorts the first <paramref name="count"/> elements of <paramref name="values"/> IN PLACE and
    /// returns their summary.
    /// </summary>
    /// <param name="values">Scratch array; its first <paramref name="count"/> elements are reordered.</param>
    /// <param name="count">Number of valid samples at the start of the array.</param>
    /// <returns>The distribution summary; all zeros when <paramref name="count"/> is 0.</returns>
    public static DistributionSummary SummarizeInPlace(double[] values, int count)
    {
        if (values == null)
        {
            throw new ArgumentNullException(nameof(values));
        }

        if (count > values.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }

        if (count <= 0)
        {
            return default;
        }

        Array.Sort(values, 0, count);

        double sum = 0;
        for (int i = 0; i < count; i++)
        {
            sum += values[i];
        }

        return new DistributionSummary(
            count,
            values[0],
            sum / count,
            NearestRankSorted(values, count, 50),
            NearestRankSorted(values, count, 95),
            NearestRankSorted(values, count, 99),
            values[count - 1]);
    }

    /// <summary>
    /// Nearest-rank percentile of an already ascending-sorted range:
    /// the value at rank <c>ceil(p / 100 * count)</c>.
    /// </summary>
    /// <param name="sorted">Ascending-sorted samples.</param>
    /// <param name="count">Number of valid samples.</param>
    /// <param name="percentile">Percentile in [0, 100].</param>
    /// <returns>The percentile value, or 0 when <paramref name="count"/> is 0.</returns>
    public static double NearestRankSorted(double[] sorted, int count, double percentile)
    {
        if (count <= 0)
        {
            return 0;
        }

        if (percentile <= 0)
        {
            return sorted[0];
        }

        if (percentile >= 100)
        {
            return sorted[count - 1];
        }

        // Multiply first, divide last: for a whole-number percentile the product is an exact integer
        // in double, so the division is correctly rounded and an exact rank is never nudged up.
        // "percentile / 100.0 * count" gives 7.000000000000001 for p=28, n=25 and ceils to rank 8.
        int rank = (int)Math.Ceiling(percentile * count / 100.0);
        if (rank < 1)
        {
            rank = 1;
        }

        return sorted[rank - 1];
    }

    /// <summary>
    /// Percentage (0–100) of the first <paramref name="count"/> samples that are strictly greater
    /// than <paramref name="threshold"/>. Order does not matter.
    /// </summary>
    /// <param name="values">Samples.</param>
    /// <param name="count">Number of valid samples.</param>
    /// <param name="threshold">Threshold in the samples' unit.</param>
    /// <returns>The percentage of samples above the threshold, or 0 for no samples.</returns>
    public static double PercentAbove(double[] values, int count, double threshold)
    {
        if (count <= 0)
        {
            return 0;
        }

        int over = 0;
        for (int i = 0; i < count; i++)
        {
            if (values[i] > threshold)
            {
                over++;
            }
        }

        return over * 100.0 / count;
    }

    /// <summary>Arithmetic mean of the first <paramref name="count"/> samples (0 when empty).</summary>
    /// <param name="values">Samples.</param>
    /// <param name="count">Number of valid samples.</param>
    /// <returns>The mean.</returns>
    public static double Mean(double[] values, int count)
    {
        if (count <= 0)
        {
            return 0;
        }

        double sum = 0;
        for (int i = 0; i < count; i++)
        {
            sum += values[i];
        }

        return sum / count;
    }
}
