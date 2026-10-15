using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using SlKit.Core;

namespace SlKit;

/// <summary>
/// The main-thread half of the frame CSV: the one-second timer, the event markers, and the copying of each second's
/// samples into a <see cref="FrameCsvBatch"/> that the profiler's own worker then writes (see <see cref="FrameCsvWriter"/>).
/// Everything is main thread except <see cref="CurrentPath"/>, <see cref="DroppedSeconds"/> and <see cref="Mark"/>.
/// </summary>
internal static class FrameCsvSink
{
    private const int MaxPendingMarks = 256;

    private static readonly long CsvInterval = StopwatchTime.FromTimeSpan(TimeSpan.FromSeconds(1));
    private static readonly ConcurrentQueue<string> PendingMarks = new ConcurrentQueue<string>();

    // Its own worker: a stalled disk must not delay plugins' work on the shared default worker, and
    // plugins filling that queue must not make the profiler drop seconds. Capacity: a few batches.
    private static BackgroundWorker _csvWorker;

    private static volatile string _csvPath;
    private static DeadlineTimer _csvTimer;
    private static int _framesSinceCsv;
    private static int _pendingMarkCount;
    private static long _csvDroppedSeconds;

    /// <summary>The CSV writer's worker, created on first use. Main thread only.</summary>
    private static BackgroundWorker CsvWorker => _csvWorker ??= Background.ForOwner("SlKit.FrameProfiler", 16);

    /// <summary>Current CSV file, or null when the sink is off. Any thread.</summary>
    internal static string CurrentPath => _csvPath;

    /// <summary>Seconds not written because the writer fell behind. Any thread.</summary>
    internal static long DroppedSeconds => Interlocked.Read(ref _csvDroppedSeconds);

    /// <summary>Adds a marker to the current second's line; no-op while the sink is off. Any thread.</summary>
    internal static void Mark(string name)
    {
        if (_csvPath == null || string.IsNullOrEmpty(name))
        {
            return;
        }

        if (Interlocked.Increment(ref _pendingMarkCount) > MaxPendingMarks)
        {
            Interlocked.Decrement(ref _pendingMarkCount);
            return;
        }

        PendingMarks.Enqueue(name);
    }

    /// <summary>Starts (or redirects) the sink: the next second is captured one interval from now. Main thread.</summary>
    internal static void Start(string path)
    {
        _framesSinceCsv = 0;
        _csvTimer = DeadlineTimer.Periodic(StopwatchTime.Now, CsvInterval);
        _csvPath = Path.GetFullPath(path);
    }

    /// <summary>Stops the sink and asks the worker to flush and close the file. Main thread.</summary>
    internal static void Stop()
    {
        if (_csvPath == null)
        {
            return;
        }

        _csvPath = null;
        _csvTimer.Disarm();
        CsvWorker.TryRun(FrameCsvWriter.CloseAction);
    }

    /// <summary>Called at the end of every measured frame; once a second it captures the samples for the writer.</summary>
    /// <param name="now">Stopwatch ticks at the end of the frame.</param>
    /// <param name="period">The profiler's frame periods.</param>
    /// <param name="busy">The profiler's frame busy times.</param>
    internal static void OnFrameEnd(long now, RingBuffer period, RingBuffer busy)
    {
        if (_csvPath == null)
        {
            return;
        }

        _framesSinceCsv++;
        if (_csvTimer.Poll(now))
        {
            CaptureSecond(period, busy);
        }
    }

    private static void CaptureSecond(RingBuffer period, RingBuffer busy)
    {
        int frames = Math.Min(_framesSinceCsv, period.Capacity);
        _framesSinceCsv = 0;

        if (!FrameCsvBatchPool.TryRent(out FrameCsvBatch batch))
        {
            // Writer is behind by several seconds: drop this second rather than grow memory.
            Interlocked.Increment(ref _csvDroppedSeconds);
            return;
        }

        try
        {
            batch.Count = period.CopyLast(frames, batch.Period, 0);
            busy.CopyLast(frames, batch.Busy, 0);
            batch.Utc = DateTime.UtcNow;
            batch.Path = _csvPath;
            EngineCounters.Read(out batch.MirrorTickRate, out batch.MirrorFullUpdateMs, out batch.ServerTps, out batch.TargetFrameRate);

            batch.Marks.Clear();
            while (PendingMarks.TryDequeue(out string mark))
            {
                Interlocked.Decrement(ref _pendingMarkCount);
                batch.Marks.Add(mark);
            }
        }
        catch (Exception e)
        {
            FrameCsvBatchPool.Return(batch);
            SlLog.Error("FrameProfiler CSV capture failed: " + e);
            return;
        }

        FrameCsvBatchPool.Submit(batch);

        // If the worker queue is full the batch stays queued and goes out with the next one.
        CsvWorker.TryRun(FrameCsvWriter.WriteAction);
    }
}
