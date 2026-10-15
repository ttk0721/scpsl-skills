using System.Globalization;
using System.Text;
using SlKit.Core;

namespace SlKit;

/// <summary>
/// The text of the frame CSV: the header, and one line per <see cref="FrameCsvBatch"/> with its percentiles, slow-frame
/// shares, engine counters and markers. Writer thread only (it reuses one <see cref="StringBuilder"/>).
/// </summary>
internal static class FrameCsvLine
{
    /// <summary>First line of a new or empty file.</summary>
    internal const string Header =
        "utc,frames,fps,period_p50_ms,period_p95_ms,period_p99_ms,period_max_ms,"
        + "busy_p50_ms,busy_p95_ms,busy_p99_ms,busy_max_ms,pct_over_50ms,pct_over_100ms,"
        + "mirror_tick_rate,mirror_full_update_ms,server_tps,target_frame_rate,marks";

    private static readonly StringBuilder Line = new StringBuilder(256);

    /// <summary>Summarises the batch (sorting its samples in place) and formats its line, without a line break.</summary>
    /// <param name="batch">One second of samples.</param>
    /// <returns>The CSV line.</returns>
    internal static string Format(FrameCsvBatch batch)
    {
        int n = batch.Count;
        double over50 = Percentiles.PercentAbove(batch.Period, n, 50);
        double over100 = Percentiles.PercentAbove(batch.Period, n, 100);
        DistributionSummary period = Percentiles.SummarizeInPlace(batch.Period, n);
        DistributionSummary busy = Percentiles.SummarizeInPlace(batch.Busy, n);

        CultureInfo c = CultureInfo.InvariantCulture;
        Line.Clear();
        Line.Append(batch.Utc.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", c)).Append(',');
        Line.Append(n.ToString(c)).Append(',');
        Line.Append((period.Mean > 0 ? 1000.0 / period.Mean : 0).ToString("0.0", c)).Append(',');
        AppendSummary(period, c);
        AppendSummary(busy, c);
        Line.Append(over50.ToString("0.00", c)).Append(',');
        Line.Append(over100.ToString("0.00", c)).Append(',');
        Line.Append(batch.MirrorTickRate.ToString(c)).Append(',');
        Line.Append(batch.MirrorFullUpdateMs.ToString("0.000", c)).Append(',');
        Line.Append(batch.ServerTps.ToString("0", c)).Append(',');
        Line.Append(batch.TargetFrameRate.ToString(c)).Append(',');
        for (int i = 0; i < batch.Marks.Count; i++)
        {
            if (i > 0)
            {
                Line.Append('|');
            }

            AppendSanitized(batch.Marks[i]);
        }

        return Line.ToString();
    }

    private static void AppendSummary(DistributionSummary s, CultureInfo c)
    {
        Line.Append(s.P50.ToString("0.00", c)).Append(',');
        Line.Append(s.P95.ToString("0.00", c)).Append(',');
        Line.Append(s.P99.ToString("0.00", c)).Append(',');
        Line.Append(s.Max.ToString("0.00", c)).Append(',');
    }

    private static void AppendSanitized(string text)
    {
        for (int i = 0; i < text.Length; i++)
        {
            char ch = text[i];
            Line.Append(ch == ',' || ch == '"' || ch == '|' || ch == '\r' || ch == '\n' ? '_' : ch);
        }
    }
}
