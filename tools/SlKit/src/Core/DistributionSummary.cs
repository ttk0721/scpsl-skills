using System.Globalization;

namespace SlKit.Core;

/// <summary>Summary of a sample distribution: count, min, mean, p50/p95/p99 and max.</summary>
/// <remarks>
/// Percentiles and max are reported because averages hide hitches: a single 300 ms frame in a
/// second of 16 ms frames barely moves the mean (the same reason <c>Server.Tps</c>, a smoothed
/// value, never shows a hitch). Thread affinity: immutable value, safe on any thread.
/// </remarks>
public readonly struct DistributionSummary
{
    /// <summary>Creates a summary from precomputed values.</summary>
    /// <param name="count">Number of samples.</param>
    /// <param name="min">Smallest sample.</param>
    /// <param name="mean">Arithmetic mean.</param>
    /// <param name="p50">Median (nearest rank).</param>
    /// <param name="p95">95th percentile (nearest rank).</param>
    /// <param name="p99">99th percentile (nearest rank).</param>
    /// <param name="max">Largest sample.</param>
    public DistributionSummary(int count, double min, double mean, double p50, double p95, double p99, double max)
    {
        Count = count;
        Min = min;
        Mean = mean;
        P50 = p50;
        P95 = p95;
        P99 = p99;
        Max = max;
    }

    /// <summary>Number of samples summarised (0 means every other field is 0).</summary>
    public int Count { get; }

    /// <summary>Smallest sample.</summary>
    public double Min { get; }

    /// <summary>Arithmetic mean.</summary>
    public double Mean { get; }

    /// <summary>Median, nearest-rank method.</summary>
    public double P50 { get; }

    /// <summary>95th percentile, nearest-rank method.</summary>
    public double P95 { get; }

    /// <summary>99th percentile, nearest-rank method.</summary>
    public double P99 { get; }

    /// <summary>Largest sample.</summary>
    public double Max { get; }

    /// <summary>Formats the summary as "p50=… p95=… p99=… max=… (n=…)" with invariant culture.</summary>
    /// <returns>A one-line human-readable summary.</returns>
    public override string ToString()
    {
        return string.Format(
            CultureInfo.InvariantCulture,
            "p50={0:0.00} p95={1:0.00} p99={2:0.00} max={3:0.00} mean={4:0.00} (n={5})",
            P50, P95, P99, Max, Mean, Count);
    }
}
