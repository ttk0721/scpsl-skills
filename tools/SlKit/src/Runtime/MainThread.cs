using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.Threading;
using SlKit.Core;

namespace SlKit;

/// <summary>
/// Marshals work back onto the game's main thread and tells whether the caller is on it.
/// </summary>
/// <remarks>
/// Game, Mirror, LabAPI wrappers and MEC are not thread-safe (plain <c>Dictionary</c>/<c>HashSet</c>
/// statics, no locks), so results of background work must be applied on the main thread.
/// The game's own <c>MainThreadDispatcher.Dispatch</c> drains its queue with no try/catch and no
/// time budget: one throwing action aborts the rest of that drain, and a burst of thousands of
/// actions all run in one frame. <see cref="Post"/> instead queues to a
/// <see cref="ConcurrentQueue{T}"/> that <see cref="SlClock"/> drains once per frame (Update phase)
/// under <see cref="Budget"/>, each action in its own try/catch.
/// Actions posted before the clock is installed wait until <see cref="SlClock.Install"/> runs.
/// The budget is the server owner's setting (<see cref="HostBudget.VariableName"/>), not a property
/// a plugin can set: it limits the posted work of every plugin together, so one plugin raising it
/// would hand all of them a bigger share of every frame.
/// </remarks>
public static class MainThread
{
    /// <summary>
    /// Most actions that may wait for the main thread at once. Beyond it <see cref="Post"/> drops the
    /// action and counts it in <see cref="DroppedCount"/>. Why: if the clock is not installed, or
    /// another plugin rebuilt the PlayerLoop and dropped SlKit's entry, nothing drains the queue and
    /// producers on the worker or the thread pool (HTTP callbacks) would grow it until the server
    /// runs out of memory. At this size the server is already unusable, so shedding is the lesser harm.
    /// The first dropped action logs a warning at once, and further ones at most once per
    /// <see cref="DropWarningInterval"/> while dropping continues, with the count since the last line.
    /// </summary>
    public const int MaxPending = 100000;

    private static readonly ConcurrentQueue<Action> Queue = new ConcurrentQueue<Action>();
    private static int _pending;
    private static long _dropped;
    private static int _mainThreadId;
    private static long _failures;
    private static long _executed;
    private static TokenBucket _errorLogBucket;
    private static int _undrainedWarned;
    private static int _installFailureWarned;

    // Read once, when the type is first used. A static readonly field and no setter: the value is
    // the server owner's, and nothing in the process can change it through SlKit afterwards.
    private static readonly MainThreadBudgetSetting HostBudgetSetting = MainThreadBudgetSetting.Read();

    /// <summary>Shortest distance between two "actions dropped" warnings: 10 seconds. Thread affinity: any thread.</summary>
    public static readonly TimeSpan DropWarningInterval = TimeSpan.FromSeconds(10);

    // Drops come from the worker, the thread pool and the main thread at once; the gate lets one
    // warning through per interval without a lock.
    private static readonly IntervalGate DropWarningGate = new IntervalGate(StopwatchTime.FromTimeSpan(DropWarningInterval));

    /// <summary>
    /// True when the calling thread is the game's main thread. Once <see cref="SlClock"/> is
    /// installed this compares with the captured thread id (one volatile read). Before that it asks
    /// Unity: <c>UnityEngine.Awaitable.MainThreadAsync().IsCompleted</c>, which is documented to be
    /// complete exactly when the caller is on Unity's main thread, so the answer is right on every
    /// thread from the start. Thread affinity: any thread.
    /// </summary>
    public static bool IsMainThread
    {
        get
        {
            int id = Volatile.Read(ref _mainThreadId);
            return id != 0 ? id == Thread.CurrentThread.ManagedThreadId : UnityMainThreadProbe.ReportsMainThread();
        }
    }

    /// <summary>True once the main thread id has been captured. Thread affinity: any thread.</summary>
    public static bool IsKnown => Volatile.Read(ref _mainThreadId) != 0;

    /// <summary>
    /// Maximum time spent running posted actions per frame, for all plugins together. 1 ms unless
    /// the server owner sets the environment variable <see cref="HostBudget.VariableName"/>
    /// (0.05 to 50 ms), read once per process. Read-only: no plugin can change it for the others.
    /// At least one action runs per frame even if it alone exceeds the budget, so the queue always
    /// makes progress. Thread affinity: any thread.
    /// </summary>
    public static TimeSpan Budget => StopwatchTime.ToTimeSpan(HostBudgetSetting.Ticks);

    /// <summary>Number of actions waiting to run, for diagnostics. Thread affinity: any thread.</summary>
    public static int PendingCount => Volatile.Read(ref _pending);

    /// <summary>Total number of actions refused because <see cref="MaxPending"/> were already waiting. Thread affinity: any thread.</summary>
    public static long DroppedCount => Interlocked.Read(ref _dropped);

    /// <summary>Total number of posted actions that threw. Thread affinity: any thread.</summary>
    public static long FailureCount => Interlocked.Read(ref _failures);

    /// <summary>Total number of posted actions that have run. Thread affinity: any thread.</summary>
    public static long ExecutedCount => Interlocked.Read(ref _executed);

    /// <summary>
    /// Queues <paramref name="action"/> to run on the main thread during a later
    /// <see cref="SlClock"/> frame (normally the next one). Never runs inline, even when called on
    /// the main thread, so ordering is always "after the current frame's work".
    /// Allocates nothing beyond the delegate the caller passes (a capturing lambda allocates a
    /// closure; on hot paths cache the delegate). Thread affinity: any thread.
    /// </summary>
    /// <param name="action">The work to run on the main thread.</param>
    public static void Post(Action action)
    {
        if (action == null)
        {
            throw new ArgumentNullException(nameof(action));
        }

        // Own counter instead of ConcurrentQueue.Count, which is not O(1) on every runtime.
        if (Interlocked.Increment(ref _pending) > MaxPending)
        {
            Interlocked.Decrement(ref _pending);
            long dropped = Interlocked.Increment(ref _dropped);
            WarnDropped(dropped);
            return;
        }

        Queue.Enqueue(action);
    }

    /// <summary>
    /// Throws <see cref="InvalidOperationException"/> when called off the main thread, also before
    /// the clock is installed (see <see cref="IsMainThread"/>). Use it to guard APIs that touch game
    /// state. Thread affinity: any thread.
    /// </summary>
    /// <param name="api">Name of the guarded API, used in the exception message.</param>
    public static void ThrowIfNotMainThread(string api)
    {
        if (!IsMainThread)
        {
            throw new InvalidOperationException(
                api + " must be called on the main thread (it touches Unity/game state). "
                + "From a worker thread use MainThread.Post.");
        }
    }

    /// <summary>
    /// Called wherever SlKit is about to queue a main-thread callback (Background results, HTTP
    /// results). Only SlClock drains the queue, so a plugin that never called
    /// <see cref="SlClock.Install"/> would see its callbacks silently never run. On the main thread
    /// this installs the clock; elsewhere it cannot (installing touches the PlayerLoop) and warns once
    /// per process instead. Costs one volatile read once the clock is installed. Any thread.
    /// </summary>
    /// <param name="api">The public API queuing the callback, for the warning.</param>
    /// <param name="owner">Worker or client name, for the warning; may be null.</param>
    internal static void EnsureCallbacksDrained(string api, string owner)
    {
        if (SlClock.IsInstalled)
        {
            return;
        }

        if (IsMainThread)
        {
            try
            {
                SlClock.Install();
                return;
            }
            catch (Exception e)
            {
                // Every later Background/SlHttp call on the main thread retries the install and would
                // log this again; once per process is enough, the warning below repeats nothing new.
                if (Interlocked.Exchange(ref _installFailureWarned, 1) == 0)
                {
                    SlLog.Warn("Could not install SlClock from " + api + ": " + e.GetType().Name + ": " + e.Message);
                }
            }
        }

        if (Interlocked.Exchange(ref _undrainedWarned, 1) != 0)
        {
            return;
        }

        SlLog.Warn(
            api + (string.IsNullOrEmpty(owner) ? string.Empty : " ('" + owner + "')")
            + " queued a main-thread callback while SlClock is not installed, so nothing runs it yet. "
            + "Call SlClock.Install() from Plugin.Enable(); queued callbacks run once it is installed. "
            + "This warning is shown once.");
    }

    private static void WarnDropped(long droppedTotal)
    {
        if (!DropWarningGate.TryPass(StopwatchTime.Now, out long since))
        {
            return;
        }

        SlLog.Warn(
            "MainThread.Post is dropping actions: " + MaxPending + " are already waiting"
            + (since > 0 ? " (" + (since + 1) + " dropped since the last warning, " : " (")
            + droppedTotal + " in total). Nothing is draining the queue: "
            + (SlClock.IsInstalled
                ? "the clock is installed but its frames have stopped (another plugin may have rebuilt the PlayerLoop; the next SlClock registration re-inserts it) or the main thread is stalled."
                : "SlClock is not installed; call SlClock.Install() from Plugin.Enable().")
            + " Callbacks of Background and SlHttp are among the dropped actions.");
    }

    /// <summary>
    /// One line for the clock's install log: the budget and where it came from, plus why an owner
    /// value was ignored (null when nothing was wrong).
    /// </summary>
    /// <param name="problem">Why the environment value was ignored, or null.</param>
    /// <returns>For example "main-thread budget 1 ms (default)".</returns>
    internal static string DescribeBudget(out string problem)
    {
        problem = HostBudgetSetting.Problem;
        return "main-thread budget "
            + HostBudgetSetting.Milliseconds.ToString("0.###", CultureInfo.InvariantCulture) + " ms"
            + (HostBudgetSetting.FromEnvironment ? " (from " + HostBudget.VariableName + ")" : " (default)");
    }

    /// <summary>Records the current thread as the main thread. Called by SlClock on the main thread.</summary>
    internal static void CaptureCurrentThread()
    {
        Volatile.Write(ref _mainThreadId, Thread.CurrentThread.ManagedThreadId);
    }

    /// <summary>Runs queued actions until the queue is empty or the budget is spent. Main thread only.</summary>
    /// <param name="frameStart">Stopwatch timestamp at which the budget starts.</param>
    internal static void Drain(long frameStart)
    {
        if (Queue.IsEmpty)
        {
            return;
        }

        long deadline = frameStart + HostBudgetSetting.Ticks;
        while (Queue.TryDequeue(out Action action))
        {
            Interlocked.Decrement(ref _pending);
            try
            {
                action();
            }
            catch (Exception e)
            {
                Interlocked.Increment(ref _failures);
                ReportFailure(e);
            }

            Interlocked.Increment(ref _executed);

            // Checked after the action so at least one runs per frame, whatever the budget.
            if (StopwatchTime.Now >= deadline)
            {
                break;
            }
        }
    }

    private static void ReportFailure(Exception e)
    {
        if (!ErrorLogThrottle.TryAcquire(ref _errorLogBucket, 3, 0.2, StopwatchTime.Now, out long suppressed))
        {
            return;
        }

        SlLog.Error("MainThread.Post action threw"
                    + (suppressed > 0 ? " (" + suppressed + " similar errors suppressed)" : string.Empty)
                    + ": " + e);
    }
}
