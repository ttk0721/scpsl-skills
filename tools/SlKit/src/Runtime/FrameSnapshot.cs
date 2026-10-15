using System.Globalization;
using System.Text;
using SlKit.Core;

namespace SlKit;

/// <summary>
/// Frame-time statistics over the <see cref="FrameProfiler"/> window, next to the engine's own
/// (smoothed) numbers for comparison.
/// </summary>
/// <remarks>
/// <see cref="PeriodMs"/> and <see cref="BusyMs"/> are wall-clock <c>Stopwatch</c> measurements of
/// every frame and show hitches. <see cref="ServerTps"/> is LabAPI's <c>Server.Tps</c>,
/// <c>round(1 / Time.smoothDeltaTime)</c>: low-pass filtered, clamped by <c>maximumDeltaTime</c>
/// and scaled by <c>timeScale</c>, so it reads ~60 while players feel rubber-banding.
/// <see cref="MirrorActualTickRate"/> is an integer frame count per ≥1 s window and
/// <see cref="MirrorFullUpdateMs"/> an exponential moving average; both hide spikes too.
/// Thread affinity: immutable snapshot, any thread.
/// </remarks>
public sealed class FrameSnapshot
{
    internal FrameSnapshot(
        int frames,
        DistributionSummary periodMs,
        DistributionSummary busyMs,
        double percentOver50Ms,
        double percentOver100Ms,
        double realFps,
        double loadPercent,
        int mirrorActualTickRate,
        double mirrorFullUpdateMs,
        double serverTps,
        int targetFrameRate)
    {
        Frames = frames;
        PeriodMs = periodMs;
        BusyMs = busyMs;
        PercentOver50Ms = percentOver50Ms;
        PercentOver100Ms = percentOver100Ms;
        RealFps = realFps;
        LoadPercent = loadPercent;
        MirrorActualTickRate = mirrorActualTickRate;
        MirrorFullUpdateMs = mirrorFullUpdateMs;
        ServerTps = serverTps;
        TargetFrameRate = targetFrameRate;
    }

    /// <summary>Number of frames in the window.</summary>
    public int Frames { get; }

    /// <summary>Frame period (start of frame to start of next frame), milliseconds. 1000 / period = instantaneous FPS.</summary>
    public DistributionSummary PeriodMs { get; }

    /// <summary>Busy time (start of Initialization to end of PostLateUpdate), milliseconds; excludes the frame limiter's wait.</summary>
    public DistributionSummary BusyMs { get; }

    /// <summary>Percentage of frames whose period exceeded 50 ms (below 20 FPS).</summary>
    public double PercentOver50Ms { get; }

    /// <summary>Percentage of frames whose period exceeded 100 ms (below 10 FPS).</summary>
    public double PercentOver100Ms { get; }

    /// <summary>Real frames per second over the window: 1000 / mean period.</summary>
    public double RealFps { get; }

    /// <summary>Mean busy time as a percentage of mean period: how much of the frame budget the server uses.</summary>
    public double LoadPercent { get; }

    /// <summary>Mirror <c>NetworkServer.actualTickRate</c> (0 when the server is not active).</summary>
    public int MirrorActualTickRate { get; }

    /// <summary>Mirror <c>NetworkServer.fullUpdateDuration.average</c> in milliseconds (EMA; 0 when not active).</summary>
    public double MirrorFullUpdateMs { get; }

    /// <summary>LabAPI <c>Server.Tps</c> at snapshot time, for comparison only.</summary>
    public double ServerTps { get; }

    /// <summary><c>Application.targetFrameRate</c> at snapshot time: what actually paces the loop.</summary>
    public int TargetFrameRate { get; }

    /// <summary>Multi-line human-readable report (invariant culture).</summary>
    /// <returns>The report.</returns>
    public override string ToString()
    {
        StringBuilder sb = new StringBuilder(512);
        CultureInfo c = CultureInfo.InvariantCulture;
        sb.AppendFormat(c, "frames={0} realFps={1:0.0} load={2:0.0}% target={3}", Frames, RealFps, LoadPercent, TargetFrameRate).AppendLine();
        sb.Append("period ms: ").Append(PeriodMs.ToString()).AppendLine();
        sb.Append("busy   ms: ").Append(BusyMs.ToString()).AppendLine();
        sb.AppendFormat(c, "frames >50ms: {0:0.00}%  >100ms: {1:0.00}%", PercentOver50Ms, PercentOver100Ms).AppendLine();
        sb.AppendFormat(c, "Mirror actualTickRate={0} fullUpdate={1:0.00}ms (avg) | Server.Tps={2} (smoothed, for comparison)", MirrorActualTickRate, MirrorFullUpdateMs, ServerTps);
        return sb.ToString();
    }
}
