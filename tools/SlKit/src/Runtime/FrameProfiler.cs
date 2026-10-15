using System;
using System.Diagnostics;
using SlKit.Core;
using UnityEngine.LowLevel;
using InitializationPhase = UnityEngine.PlayerLoop.Initialization;
using PostLateUpdatePhase = UnityEngine.PlayerLoop.PostLateUpdate;

namespace SlKit;

/// <summary>
/// Spike-accurate frame timing: two PlayerLoop entries (first in Initialization, last in
/// PostLateUpdate) record each frame's period and busy time with <see cref="Stopwatch"/> into
/// ring buffers of the last N frames.
/// </summary>
/// <remarks>
/// Why: <c>Server.Tps</c> (and the <c>tps</c> command) is <c>round(1 / Time.smoothDeltaTime)</c>:
/// smoothed, clamped at <c>maximumDeltaTime</c> (it cannot read below ~3 and a 2 s freeze reads as
/// 0.333 s) and scaled by <c>timeScale</c>. Mirror's <c>actualTickRate</c> is a per-second integer
/// and <c>fullUpdateDuration</c> an average. A wall-clock measurement at the loop boundaries is the
/// only source that shows individual hitches.
/// Cost: two native-to-managed calls and two <see cref="StopwatchTime.Now"/> reads per frame,
/// no allocation. The CSV sink copies one second of samples on the main thread (a few hundred
/// doubles) and does sorting, formatting and file IO on its own <see cref="BackgroundWorker"/>
/// (<c>SlKit.FrameProfiler</c>, started with the first CSV second).
/// Assumption (unverified for this Unity build): the frame limiter's wait happens before
/// Initialization, so it counts in the period but not in busy time.
/// Thread affinity: MAIN THREAD for every member except <see cref="Mark"/>,
/// <see cref="IsInstalled"/> and <see cref="CsvPath"/>, which are safe from any thread.
/// </remarks>
public static class FrameProfiler
{
    /// <summary>Default window: 3600 frames (one minute at 60 FPS).</summary>
    public const int DefaultCapacity = 3600;

    private static readonly PlayerLoopSystem.UpdateFunction BeginDelegate = OnFrameBegin;
    private static readonly PlayerLoopSystem.UpdateFunction EndDelegate = OnFrameEnd;

    private static volatile bool _installed;
    private static RingBuffer _period;
    private static RingBuffer _busy;
    private static double[] _scratchA;
    private static double[] _scratchB;
    private static long _frameStart;
    private static long _previousFrameStart;
    private static double _pendingPeriodMs;
    private static long _frameCount;

    /// <summary>True while the PlayerLoop entries are installed. Thread affinity: any thread.</summary>
    public static bool IsInstalled => _installed;

    /// <summary>Window size in frames (0 before install). Thread affinity: main thread.</summary>
    public static int Capacity => _period?.Capacity ?? 0;

    /// <summary>Frames measured since install. Thread affinity: main thread.</summary>
    public static long FrameCount => _frameCount;

    /// <summary>Current CSV file, or null when the sink is off. Thread affinity: any thread.</summary>
    public static string CsvPath => FrameCsvSink.CurrentPath;

    /// <summary>Seconds not written to the CSV because the writer fell behind. Thread affinity: any thread.</summary>
    public static long CsvDroppedSeconds => FrameCsvSink.DroppedSeconds;

    /// <summary>
    /// Inserts the two PlayerLoop entries into <c>PlayerLoop.GetCurrentPlayerLoop()</c> and starts
    /// recording. Idempotent; a second call keeps the existing window size.
    /// Thread affinity: MAIN THREAD.
    /// </summary>
    /// <param name="capacity">Number of most recent frames kept for <see cref="Snapshot"/>.</param>
    public static void Install(int capacity = DefaultCapacity)
    {
        MainThread.ThrowIfNotMainThread("FrameProfiler.Install");
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), "Capacity must be positive.");
        }

        if (_period == null)
        {
            _period = new RingBuffer(capacity);
            _busy = new RingBuffer(capacity);
            _scratchA = new double[capacity];
            _scratchB = new double[capacity];
            FrameCsvBatchPool.Prepare(capacity);
        }

        _frameStart = 0;
        _previousFrameStart = 0;

        PlayerLoopInjector.TryInsert(
            typeof(InitializationPhase),
            new PlayerLoopSystem { type = typeof(SlKitFrameBeginMarker), updateDelegate = BeginDelegate },
            LoopPosition.First,
            null,
            out _);

        PlayerLoopInjector.TryInsert(
            typeof(PostLateUpdatePhase),
            new PlayerLoopSystem { type = typeof(SlKitFrameEndMarker), updateDelegate = EndDelegate },
            LoopPosition.Last,
            null,
            out _);

        bool ok = PlayerLoopInjector.IsPresent(typeof(SlKitFrameBeginMarker))
                  && PlayerLoopInjector.IsPresent(typeof(SlKitFrameEndMarker));
        if (!ok)
        {
            PlayerLoopInjector.Remove(typeof(SlKitFrameBeginMarker), typeof(SlKitFrameEndMarker));
            throw new InvalidOperationException("SlKit could not find Initialization/PostLateUpdate in the current PlayerLoop.");
        }

        _installed = true;
    }

    /// <summary>
    /// Removes the two PlayerLoop entries and stops the CSV sink. Recorded samples are kept.
    /// Thread affinity: MAIN THREAD.
    /// </summary>
    public static void Uninstall()
    {
        MainThread.ThrowIfNotMainThread("FrameProfiler.Uninstall");
        StopCsv();
        PlayerLoopInjector.Remove(typeof(SlKitFrameBeginMarker), typeof(SlKitFrameEndMarker));
        _installed = false;
    }

    /// <summary>
    /// Empties the period and busy windows so the next <see cref="Snapshot"/> covers only frames
    /// recorded from now on. The PlayerLoop entries, the frame counter and the CSV sink are left
    /// alone (the CSV is a continuous log and must not lose seconds). Use it to start a clean
    /// measurement window, e.g. at the start of a round or right after a plugin was toggled.
    /// Thread affinity: MAIN THREAD (the ring buffers are written by the frame-end entry).
    /// </summary>
    public static void ResetWindow()
    {
        MainThread.ThrowIfNotMainThread("FrameProfiler.ResetWindow");
        if (_period == null)
        {
            return;
        }

        _period.Clear();
        _busy.Clear();
    }

    /// <summary>
    /// Statistics over the recorded window plus Mirror's and LabAPI's own numbers. Sorts two
    /// arrays of up to <see cref="Capacity"/> doubles (O(n log n)); call it on demand (a command,
    /// once per minute), not every frame. Thread affinity: MAIN THREAD (reads Mirror statics,
    /// <c>Server.Tps</c> and <c>Application.targetFrameRate</c>).
    /// </summary>
    /// <returns>The snapshot; all-zero frame statistics before install.</returns>
    public static FrameSnapshot Snapshot()
    {
        MainThread.ThrowIfNotMainThread("FrameProfiler.Snapshot");
        int n = 0;
        if (_period != null)
        {
            n = _period.CopyTo(_scratchA);
            _busy.CopyLast(n, _scratchB, 0);
        }

        double over50 = Percentiles.PercentAbove(_scratchA, n, 50);
        double over100 = Percentiles.PercentAbove(_scratchA, n, 100);
        DistributionSummary period = n > 0 ? Percentiles.SummarizeInPlace(_scratchA, n) : default;
        DistributionSummary busy = n > 0 ? Percentiles.SummarizeInPlace(_scratchB, n) : default;

        EngineCounters.Read(out int mirrorTick, out double mirrorFullMs, out double tps, out int target);
        return new FrameSnapshot(
            n,
            period,
            busy,
            over50,
            over100,
            period.Mean > 0 ? 1000.0 / period.Mean : 0,
            period.Mean > 0 ? busy.Mean / period.Mean * 100.0 : 0,
            mirrorTick,
            mirrorFullMs,
            tps,
            target);
    }

    /// <summary>
    /// Adds an event marker (round start, wave, detonation…) to the current second's CSV line.
    /// No-op while the CSV sink is off; at most 256 pending markers are kept.
    /// Thread affinity: any thread.
    /// </summary>
    /// <param name="name">Short marker text; commas, quotes and line breaks are replaced in the CSV.</param>
    public static void Mark(string name)
    {
        FrameCsvSink.Mark(name);
    }

    /// <summary>
    /// Starts (or redirects) the CSV sink: one line per second appended to <paramref name="path"/>
    /// with period/busy percentiles, % slow frames, Mirror counters, <c>Server.Tps</c> and markers.
    /// File IO happens on <see cref="Background"/>. Requires <see cref="Install"/>.
    /// Thread affinity: MAIN THREAD.
    /// </summary>
    /// <param name="path">Target file; created with a header when it does not exist or is empty.</param>
    public static void StartCsv(string path)
    {
        MainThread.ThrowIfNotMainThread("FrameProfiler.StartCsv");
        if (string.IsNullOrEmpty(path))
        {
            throw new ArgumentNullException(nameof(path));
        }

        if (!_installed)
        {
            throw new InvalidOperationException("Call FrameProfiler.Install() before StartCsv().");
        }

        FrameCsvSink.Start(path);
    }

    /// <summary>Stops the CSV sink and closes the file (on the worker). Thread affinity: MAIN THREAD.</summary>
    public static void StopCsv()
    {
        MainThread.ThrowIfNotMainThread("FrameProfiler.StopCsv");
        FrameCsvSink.Stop();
    }

    private static void OnFrameBegin()
    {
        long now = StopwatchTime.Now;
        _pendingPeriodMs = _previousFrameStart != 0 ? StopwatchTime.ToMilliseconds(now - _previousFrameStart) : -1;
        _previousFrameStart = now;
        _frameStart = now;
    }

    private static void OnFrameEnd()
    {
        long now = StopwatchTime.Now;
        if (_frameStart == 0 || _pendingPeriodMs < 0)
        {
            return; // first frame after install: no period yet
        }

        _period.Add(_pendingPeriodMs);
        _busy.Add(StopwatchTime.ToMilliseconds(now - _frameStart));
        _frameStart = 0;
        _frameCount++;

        FrameCsvSink.OnFrameEnd(now, _period, _busy);
    }
}
