using System;
using System.Threading;
using SlKit.Core;

namespace SlKit;

/// <summary>
/// One dedicated background thread (background, below-normal priority) with its own bounded FIFO
/// queue. Obtain one per plugin with <see cref="Background.ForOwner"/> (keyed by name and calling
/// assembly); the static
/// <see cref="Background"/> methods use a shared default worker.
/// </summary>
/// <remarks>
/// Why one worker per owner: a worker runs items one after another, so a plugin whose file or
/// database write stalls for seconds would otherwise hold up every other plugin's IO behind it,
/// and one plugin filling the queue would make every other plugin's <c>TryRun</c> fail.
/// Separate workers isolate both the latency and the capacity.
/// Rules for the work, as for <see cref="Background"/>: take a plain snapshot on the main thread,
/// touch only that snapshot on the worker, apply results on the main thread.
/// The thread starts on first use and lives for the whole process (the game never unloads a
/// plugin), so create a worker once, in <c>Enable</c>, and keep it.
/// Thread affinity: every member may be called from any thread.
/// </remarks>
public sealed class BackgroundWorker
{
    [ThreadStatic]
    private static BackgroundWorker _current;

    private readonly object _startLock = new object();
    private BoundedWorkQueue<Action> _queue;
    private int _capacity;
    private int _capacityIgnoredReported;
    private long _failures;
    private TokenBucket _errorLogBucket;

    internal BackgroundWorker(string name, int capacity, string threadName, string owner)
    {
        Name = name;
        Owner = owner;
        _capacity = Math.Max(1, capacity);
        ThreadName = threadName;
    }

    /// <summary>The owner name given to <see cref="Background.ForOwner"/>. Thread affinity: any thread.</summary>
    public string Name { get; }

    /// <summary>
    /// Simple name of the assembly that created this worker through <see cref="Background.ForOwner"/>
    /// (SlKit's own for the default worker). Workers are keyed by name and this assembly.
    /// Thread affinity: any thread.
    /// </summary>
    public string Owner { get; }

    /// <summary>
    /// Name of the worker's thread as it appears in thread dumps and profilers:
    /// <c>SlKit.Background.&lt;name&gt;</c>, with <c>@&lt;owner&gt;</c> appended when another assembly
    /// had already used the name. Thread affinity: any thread.
    /// </summary>
    public string ThreadName { get; }

    /// <summary>Queue capacity. Fixed once the thread has started. Thread affinity: any thread.</summary>
    public int Capacity => Volatile.Read(ref _capacity);

    /// <summary>True once the thread has started (first <c>TryRun</c>). Thread affinity: any thread.</summary>
    public bool IsStarted => Volatile.Read(ref _queue) != null;

    /// <summary>Items waiting for this worker. Thread affinity: any thread.</summary>
    public int PendingCount => Volatile.Read(ref _queue)?.Count ?? 0;

    /// <summary>Items refused because this worker's queue was full. Thread affinity: any thread.</summary>
    public long RejectedCount => Volatile.Read(ref _queue)?.RejectedCount ?? 0;

    /// <summary>Work items that threw on this worker. Thread affinity: any thread.</summary>
    public long FailureCount => Interlocked.Read(ref _failures);

    /// <summary>True when called on this worker's thread. Thread affinity: any thread.</summary>
    public bool IsWorkerThread => _current == this;

    /// <summary>True when called on any SlKit worker thread of this SlKit copy.</summary>
    internal static bool IsAnyWorkerThread => _current != null;

    /// <summary>
    /// Queues <paramref name="work"/> for this worker. Never blocks. Exceptions thrown by
    /// <paramref name="work"/> are caught on the worker and logged (rate-limited).
    /// Thread affinity: any thread; <paramref name="work"/> runs on the worker and must not touch
    /// game, Unity, Mirror, MEC or LabAPI wrapper state.
    /// </summary>
    /// <param name="work">Pure .NET work on plain data (IO, serialisation, hashing, sorting).</param>
    /// <returns>True when queued; false when the queue is full (the caller decides: drop, retry, count).</returns>
    public bool TryRun(Action work)
    {
        if (work == null)
        {
            throw new ArgumentNullException(nameof(work));
        }

        return EnsureStarted().TryEnqueue(work);
    }

    /// <summary>
    /// Runs <paramref name="work"/> on this worker, then posts its result to
    /// <paramref name="onMainThread"/> on the main thread. If <paramref name="work"/> throws,
    /// <paramref name="onError"/> is posted instead (or the error is logged when it is null).
    /// Never blocks. When called on the main thread before any plugin installed
    /// <see cref="SlClock"/>, it installs the clock, because only the clock drains the callbacks;
    /// from another thread it cannot, and it logs a warning once instead.
    /// Thread affinity: any thread; <paramref name="work"/> runs on the worker (plain data only),
    /// both callbacks run on the main thread.
    /// </summary>
    /// <typeparam name="T">Result type; make it a plain snapshot, not a game object.</typeparam>
    /// <param name="work">Pure .NET work producing a result.</param>
    /// <param name="onMainThread">Receives the result on the main thread; may touch game state.</param>
    /// <param name="onError">Optional; receives the exception on the main thread.</param>
    /// <returns>True when queued; false when the queue is full (nothing will be called).</returns>
    public bool TryRun<T>(Func<T> work, Action<T> onMainThread, Action<Exception> onError = null)
    {
        if (work == null)
        {
            throw new ArgumentNullException(nameof(work));
        }

        if (onMainThread == null)
        {
            throw new ArgumentNullException(nameof(onMainThread));
        }

        MainThread.EnsureCallbacksDrained("Background.TryRun<T>", Name);

        // One closure per call: this is for discrete jobs, not per-frame streams.
        return EnsureStarted().TryEnqueue(() =>
        {
            T result;
            try
            {
                result = work();
            }
            catch (Exception e)
            {
                Interlocked.Increment(ref _failures);
                if (onError != null)
                {
                    MainThread.Post(() => onError(e));
                }
                else
                {
                    ReportFailure(e);
                }

                return;
            }

            MainThread.Post(() => onMainThread(result));
        });
    }

    /// <summary>
    /// True for the first caller only: a worker reports an ignored capacity once, however often
    /// <see cref="Background.ForOwner"/> is asked for it again. Any thread.
    /// </summary>
    internal bool TryMarkCapacityIgnoredReported() => Interlocked.Exchange(ref _capacityIgnoredReported, 1) == 0;

    /// <summary>Changes the capacity if the thread has not started. Any thread.</summary>
    /// <returns>True when applied; false when the worker was already running.</returns>
    internal bool TrySetCapacity(int capacity)
    {
        lock (_startLock)
        {
            if (_queue != null)
            {
                return false;
            }

            Volatile.Write(ref _capacity, Math.Max(1, capacity));
            return true;
        }
    }

    private BoundedWorkQueue<Action> EnsureStarted()
    {
        BoundedWorkQueue<Action> queue = Volatile.Read(ref _queue);
        if (queue != null)
        {
            return queue;
        }

        lock (_startLock)
        {
            if (_queue != null)
            {
                return _queue;
            }

            queue = new BoundedWorkQueue<Action>(_capacity);
            Thread thread = new Thread(() => WorkerLoop(queue))
            {
                Name = ThreadName,
                IsBackground = true, // never keeps the server process alive on shutdown
                Priority = ThreadPriority.BelowNormal,
            };

            thread.Start();
            Volatile.Write(ref _queue, queue);
            return queue;
        }
    }

    private void WorkerLoop(BoundedWorkQueue<Action> queue)
    {
        _current = this;
        while (true)
        {
            if (!queue.TryDequeue(out Action work, Timeout.Infinite))
            {
                continue;
            }

            try
            {
                work();
            }
            catch (Exception e)
            {
                Interlocked.Increment(ref _failures);
                try
                {
                    ReportFailure(e);
                }
                catch (Exception)
                {
                    // An unhandled exception on this thread would end the process; a dead worker
                    // would leave every later TryRun queueing into a queue nobody drains.
                }
            }
        }
    }

    private void ReportFailure(Exception e)
    {
        // Called from this worker's thread only, so the bucket needs no lock.
        if (!ErrorLogThrottle.TryAcquire(ref _errorLogBucket, 3, 0.2, StopwatchTime.Now, out long suppressed))
        {
            return;
        }

        SlLog.Error("Background work on worker '" + Name + "' threw"
                    + (suppressed > 0 ? " (" + suppressed + " similar errors suppressed)" : string.Empty)
                    + ": " + e);
    }
}
