using System.Globalization;
using System.Text;
using SlKit;
using SlKit.Core;

namespace SlProbe
{
    /// <summary>
    /// Turns <see cref="FrameSnapshot"/> into text for the server log and the command reply.
    /// </summary>
    /// <remarks>
    /// Everything here allocates strings, which is fine: it runs once per report interval or per
    /// command, never per frame. Thread affinity: <see cref="IsIdle"/> reads Unity state and must run on
    /// the main thread; the formatters are pure.
    /// </remarks>
    public static class ProbeFormat
    {
        private const float IdleTimeScaleThreshold = 0.5f;

        /// <summary>
        /// True while the server is in idle mode. Idle mode sets <c>Time.timeScale</c> to 0.01 and the frame
        /// rate to <c>idle_mode_tickrate</c> (default 1), so every frame in the window is about a second
        /// long and the statistics describe the sleeping server, not the plugin under test.
        /// Thread affinity: MAIN THREAD.
        /// </summary>
        /// <returns>True when <c>Time.timeScale</c> is below 0.5.</returns>
        public static bool IsIdle() => UnityEngine.Time.timeScale < IdleTimeScaleThreshold;

        /// <summary>
        /// One compact line for the periodic server-log report.
        /// Thread affinity: any thread (pure), but the snapshot itself comes from the main thread.
        /// </summary>
        /// <param name="s">Snapshot to describe.</param>
        /// <param name="idle">Whether the server was idle when the snapshot was taken.</param>
        /// <returns>A single line without line breaks.</returns>
        public static string OneLine(FrameSnapshot s, bool idle)
        {
            if (s.Frames == 0)
                return "[sl-probe] no frames recorded yet";

            CultureInfo c = CultureInfo.InvariantCulture;
            StringBuilder sb = new StringBuilder(256);
            sb.Append("[sl-probe] ").Append(s.Frames.ToString(c)).Append(" frames, ")
              .Append(s.RealFps.ToString("0.0", c)).Append(" fps");
            if (idle)
                sb.Append(" (IDLE MODE, numbers describe the sleeping server)");

            sb.Append(" | period ms p50/p95/p99/max ")
              .Append(Four(s.PeriodMs, c))
              .Append(" | busy ms ")
              .Append(Four(s.BusyMs, c))
              .Append(" | >50ms ").Append(s.PercentOver50Ms.ToString("0.00", c))
              .Append("% >100ms ").Append(s.PercentOver100Ms.ToString("0.00", c))
              .Append("% | load ").Append(s.LoadPercent.ToString("0", c))
              .Append("% | Server.Tps ").Append(s.ServerTps.ToString("0", c)).Append(" (smoothed, not a measurement)");
            return sb.ToString();
        }

        /// <summary>
        /// Multi-line report for the <c>slprobe report</c> command: the snapshot text plus, when the
        /// server is idle, a warning that the numbers are not usable.
        /// Thread affinity: any thread (pure).
        /// </summary>
        /// <param name="s">Snapshot to describe.</param>
        /// <param name="idle">Whether the server was idle when the snapshot was taken.</param>
        /// <returns>The multi-line report.</returns>
        public static string Report(FrameSnapshot s, bool idle)
        {
            if (s.Frames == 0)
                return "No frames recorded since the window was last cleared.";

            string text = s.ToString();
            return idle
                ? text + "\nWARNING: the server is in idle mode (timeScale < 0.5). Frames are about 1 s long; do not compare these numbers."
                : text;
        }

        private static string Four(DistributionSummary d, CultureInfo c)
        {
            return d.P50.ToString("0.0", c) + "/" + d.P95.ToString("0.0", c) + "/"
                   + d.P99.ToString("0.0", c) + "/" + d.Max.ToString("0.0", c);
        }
    }
}
