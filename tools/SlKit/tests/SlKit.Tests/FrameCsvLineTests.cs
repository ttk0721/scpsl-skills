using System;
using static SlKit.TestSupport.TestKit;

namespace SlKit.Tests;

/// <summary>Tests of FrameCsvLine: the header and the line the frame profiler writes for one second of samples.</summary>
internal static class FrameCsvLineTests
{
    internal static void Register()
    {
        // Frame profiler CSV line
        Run(nameof(CsvLine_FormatsOneSecondInTheColumnsOfTheHeader), CsvLine_FormatsOneSecondInTheColumnsOfTheHeader);
        Run(nameof(CsvLine_CountsSlowFramesAndSanitizesMarks), CsvLine_CountsSlowFramesAndSanitizesMarks);
    }

    // ---------------------------------------------------------------------------------------
    // CSV line of the frame profiler. Regression for the split of FrameProfiler: the bytes of a
    // line, and so the header, must stay exactly as they were.
    // ---------------------------------------------------------------------------------------

    private static FrameCsvBatch Batch(double[] period, double[] busy)
    {
        FrameCsvBatch batch = new FrameCsvBatch(period.Length);
        Array.Copy(period, batch.Period, period.Length);
        Array.Copy(busy, batch.Busy, busy.Length);
        batch.Count = period.Length;
        batch.Path = "frames.csv";
        batch.Utc = new DateTime(2026, 10, 15, 12, 0, 1, 234, DateTimeKind.Utc);
        batch.MirrorTickRate = 60;
        batch.MirrorFullUpdateMs = 1.5;
        batch.ServerTps = 59.6;
        batch.TargetFrameRate = 60;
        return batch;
    }

    private static void CsvLine_FormatsOneSecondInTheColumnsOfTheHeader()
    {
        FrameCsvBatch batch = Batch(new double[] { 10, 20, 30, 40 }, new double[] { 1, 2, 3, 4 });
        batch.Marks.Add("round start");
        batch.Marks.Add("a,b");

        Equal(
            "2026-10-15T12:00:01.234Z,4,40.0,20.00,40.00,40.00,40.00,2.00,4.00,4.00,4.00,0.00,0.00,60,1.500,60,60,round start|a_b",
            FrameCsvLine.Format(batch),
            "one second of samples");
        Equal(
            "utc,frames,fps,period_p50_ms,period_p95_ms,period_p99_ms,period_max_ms,busy_p50_ms,busy_p95_ms,busy_p99_ms,busy_max_ms,"
            + "pct_over_50ms,pct_over_100ms,mirror_tick_rate,mirror_full_update_ms,server_tps,target_frame_rate,marks",
            FrameCsvLine.Header,
            "header");
        Equal(FrameCsvLine.Header.Split(',').Length, FrameCsvLine.Format(batch).Split(',').Length, "a line has the header's columns");
    }

    private static void CsvLine_CountsSlowFramesAndSanitizesMarks()
    {
        FrameCsvBatch batch = Batch(new double[] { 10, 60, 120, 40 }, new double[] { 1, 2, 3, 4 });
        batch.Marks.Add("a,b");
        batch.Marks.Add("say \"hi\"");
        batch.Marks.Add("x|y");
        batch.Marks.Add("l1\r\nl2");

        string[] fields = FrameCsvLine.Format(batch).Split(',');
        Equal(18, fields.Length, "no mark adds a column");
        Equal("50.00", fields[11], "two of four frames are over 50 ms");
        Equal("25.00", fields[12], "one of four frames is over 100 ms");
        Equal("a_b|say _hi_|x_y|l1__l2", fields[17], "commas, quotes, bars and line breaks cannot break the line");
    }
}
