using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using SlKit.Core;

namespace SlKit;

/// <summary>
/// The one wall-clock scheduler for plugin work: a single PlayerLoop entry (Update phase, right
/// after <c>ScriptRunBehaviourUpdate</c>) that drives every periodic, delayed and staggered job,
/// drains <see cref="MainThread.Post"/>, and measures its own cost.
/// </summary>
/// <remarks>
/// Why not MEC: Update-segment waits run on summed, scaled and clamped <c>Time.deltaTime</c>, so in
/// idle mode (<c>timeScale = 0.01</c>) a 30 s wait takes about 50 minutes and hitches beyond
/// <c>maximumDeltaTime</c> are lost; <c>CallPeriodically</c> re-arms from the resume frame and
/// drifts; an exception in a coroutine ends it for good. SlClock uses absolute
/// <see cref="Stopwatch"/> deadlines, skips missed periods instead of replaying them, wraps each
/// job in try/catch, and disables a job after its <see cref="ClockJobOptions.MaxConsecutiveFailures"/>
/// failures in a row (by default 5, and only for a growing while: see <see cref="JobFailurePolicy"/>).
/// Granularity is one server frame: while idle at the default <c>idle_mode_tickrate</c> (1) that
/// is about one second, whatever the requested interval.
/// The entry is installed once per process for this SlKit assembly and shared by every plugin
/// that uses it (put SlKit.dll in <c>LabAPI/dependencies/global</c>). It is never removed: the game
/// never calls <c>Plugin.Disable()</c>, and the PlayerLoop survives scene loads.
/// Because the clock is shared, it has no static setting that changes how other plugins' jobs
/// behave: failure handling is given per job at registration (<see cref="ClockJobOptions"/>), and
/// the main-thread budget belongs to the server owner (<see cref="MainThread.Budget"/>).
/// Thread affinity: MAIN THREAD for every member except <see cref="IsInstalled"/> and disposing
/// returned handles, which are safe from any thread. Steady state allocates nothing per frame.
/// </remarks>
public static class SlClock
{
    private static int _dirty;

    /// <summary>
    /// The default threshold, 5. Obsolete: since 0.2.0 the threshold is per job
    /// (<see cref="ClockJobOptions.MaxConsecutiveFailures"/>, read back with
    /// <see cref="ClockJobHandle.MaxConsecutiveFailures"/>). It was a static setter shared by every
    /// plugin, so one plugin could change when every other plugin's jobs were disabled.
    /// Kept only so a plugin built against an earlier 0.2.0 binary still loads.
    /// Thread affinity: any thread.
    /// </summary>
    [Obsolete("Per job since SlKit 0.2.0: pass new ClockJobOptions { MaxConsecutiveFailures = n } at registration; read it back with ClockJobHandle.MaxConsecutiveFailures.")]
    public static int MaxConsecutiveFailures => ClockJobOptions.DefaultMaxConsecutiveFailures;

    /// <summary>True once the PlayerLoop entry is installed. Thread affinity: any thread.</summary>
    public static bool IsInstalled => ClockInstaller.IsInstalled;

    /// <summary>Number of clock frames executed since install. Thread affinity: main thread (reads are atomic on 64-bit).</summary>
    public static long FrameCount => ClockTicker.FrameCount;

    /// <summary>Number of registered jobs, including disabled ones not yet disposed. Thread affinity: main thread.</summary>
    public static int JobCount => ClockJobTable.Count;

    /// <summary>
    /// The clock's own cost per frame (posted actions + all jobs), in Stopwatch ticks with
    /// microsecond helpers. Thread affinity: main thread.
    /// </summary>
    public static RunningStats FrameCost => ClockTicker.FrameCost;

    /// <summary>
    /// Per-job statistics: runs, failures, disable state, last/average/max microseconds, skipped
    /// periods. Builds a new list on each read (diagnostics, not a hot path). Thread affinity: main thread.
    /// </summary>
    public static IReadOnlyList<ClockJobStats> Stats
    {
        get
        {
            MainThread.ThrowIfNotMainThread("SlClock.Stats");
            return ClockJobTable.Snapshot();
        }
    }

    /// <summary>
    /// Inserts the clock into <c>PlayerLoop.GetCurrentPlayerLoop()</c> (Update phase, after
    /// <c>ScriptRunBehaviourUpdate</c>) unless it is already there, records the calling thread
    /// as the main thread, and subscribes <c>OnRoundRestart</c> to LabAPI's
    /// <c>ServerEvents.RoundRestarted</c> so <see cref="ClockScope.Round"/> jobs (the default of
    /// <see cref="After"/>) end with their round. Idempotent. Call it from <c>Plugin.Enable()</c> so
    /// that <see cref="MainThread.Post"/> works before the first job is registered; the registration
    /// methods and <see cref="Background"/>/<see cref="SlHttp"/> callbacks queued on the main thread
    /// call it too. The first install also logs the SlKit version and main-thread budget, and warns
    /// about other SlKit copies in the process (each copy runs its own clock, queue and workers).
    /// Thread affinity: MAIN THREAD, checked before anything is touched: called from another thread
    /// it throws <see cref="InvalidOperationException"/> and changes nothing (the PlayerLoop is a
    /// Unity API, and the calling thread would be recorded as the main thread).
    /// </summary>
    public static void Install() => ClockInstaller.Install();

    /// <summary>
    /// Runs <paramref name="job"/> every <paramref name="interval"/> of wall-clock time, first at
    /// now + interval. Deadlines are absolute (no drift); when the server is late by more than one
    /// interval the missed runs are skipped, never replayed; at most one run per frame.
    /// Thread affinity: MAIN THREAD (the job also runs on the main thread).
    /// </summary>
    /// <param name="interval">Period; must be positive. Effective resolution is one server frame.</param>
    /// <param name="job">Work to run; exceptions are caught, counted and logged rate-limited.</param>
    /// <param name="name">Name used in logs and <see cref="Stats"/>.</param>
    /// <param name="scope">Lifetime; <see cref="ClockScope.Round"/> jobs end at round restart.</param>
    /// <param name="options">This job's failure handling (threshold, policy, backoff delays); null for the defaults. Copied.</param>
    /// <returns>A handle; dispose it (any thread) to cancel.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A value in <paramref name="options"/> is out of range.</exception>
    [MethodImpl(MethodImplOptions.NoInlining)] // GetCallingAssembly must see the caller's frame
    public static ClockJobHandle Every(TimeSpan interval, Action job, string name, ClockScope scope = ClockScope.Server, ClockJobOptions options = null)
    {
        if (job == null)
        {
            throw new ArgumentNullException(nameof(job));
        }

        if (interval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(interval), "Interval must be positive.");
        }

        JobFailureSettings settings = JobFailureSettings.From(options);
        ClockInstaller.EnsureInstalled("SlClock.Every");
        long now = StopwatchTime.Now;
        return ClockJobTable.Register(new ActionJob(name, scope, settings, job, DeadlineTimer.Periodic(now, StopwatchTime.FromTimeSpan(interval))), Assembly.GetCallingAssembly());
    }

    /// <summary>
    /// Runs <paramref name="job"/> once, <paramref name="delay"/> of wall-clock time from now
    /// (on the next frame at or after the deadline). Unlike MEC's <c>CallDelayed</c>, idle-mode
    /// <c>timeScale</c> does not stretch the delay. Thread affinity: MAIN THREAD.
    /// </summary>
    /// <param name="delay">Delay; zero or negative means "next frame".</param>
    /// <param name="job">Work to run once.</param>
    /// <param name="name">Name used in logs and <see cref="Stats"/>.</param>
    /// <param name="scope">
    /// Lifetime; defaults to <see cref="ClockScope.Round"/> so delayed round logic never leaks into
    /// the next round (<see cref="Install"/>, which every registration runs, wires the round restart).
    /// </param>
    /// <param name="options">This job's failure handling; null for the defaults. Copied.</param>
    /// <returns>A handle; dispose it (any thread) to cancel before it runs.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A value in <paramref name="options"/> is out of range.</exception>
    [MethodImpl(MethodImplOptions.NoInlining)] // GetCallingAssembly must see the caller's frame
    public static ClockJobHandle After(TimeSpan delay, Action job, string name, ClockScope scope = ClockScope.Round, ClockJobOptions options = null)
    {
        if (job == null)
        {
            throw new ArgumentNullException(nameof(job));
        }

        JobFailureSettings settings = JobFailureSettings.From(options);
        ClockInstaller.EnsureInstalled("SlClock.After");
        long now = StopwatchTime.Now;
        return ClockJobTable.Register(new ActionJob(name, scope, settings, job, DeadlineTimer.OneShot(now, StopwatchTime.FromTimeSpan(delay))), Assembly.GetCallingAssembly());
    }

    /// <summary>
    /// Every <paramref name="period"/>, reads <paramref name="source"/> once and calls
    /// <paramref name="perItem"/> for each item, spread evenly over the frames of the period
    /// (e.g. 60 players over 1 s at 60 fps = one player per frame) instead of a burst on one frame.
    /// The spread follows elapsed wall-clock time, so it adapts to the real frame rate.
    /// Thread affinity: MAIN THREAD.
    /// </summary>
    /// <typeparam name="T">Item type.</typeparam>
    /// <param name="period">Period over which one pass over all items is spread; must be positive.</param>
    /// <param name="source">
    /// Returns the items for the next period. Called once per period on the main thread. To stay
    /// allocation-free, return a list you own and refill (the clock keeps the reference for the
    /// whole period, so do not refill it before the next call).
    /// </param>
    /// <param name="perItem">Work per item; an exception skips only that item (the frame counts as a failure).</param>
    /// <param name="name">Name used in logs and <see cref="Stats"/>.</param>
    /// <param name="scope">Lifetime of the job.</param>
    /// <param name="options">This job's failure handling; null for the defaults. Copied.</param>
    /// <returns>A handle; dispose it (any thread) to cancel.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A value in <paramref name="options"/> is out of range.</exception>
    [MethodImpl(MethodImplOptions.NoInlining)] // GetCallingAssembly must see the caller's frame
    public static ClockJobHandle EveryStaggered<T>(TimeSpan period, Func<IReadOnlyList<T>> source, Action<T> perItem, string name, ClockScope scope = ClockScope.Server, ClockJobOptions options = null)
    {
        if (source == null)
        {
            throw new ArgumentNullException(nameof(source));
        }

        if (perItem == null)
        {
            throw new ArgumentNullException(nameof(perItem));
        }

        if (period <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(period), "Period must be positive.");
        }

        JobFailureSettings settings = JobFailureSettings.From(options);
        ClockInstaller.EnsureInstalled("SlClock.EveryStaggered");
        long now = StopwatchTime.Now;
        return ClockJobTable.Register(new StaggeredJob<T>(name, scope, settings, source, perItem, StopwatchTime.FromTimeSpan(period), now), Assembly.GetCallingAssembly());
    }

    /// <summary>
    /// Re-enables every disabled job named <paramref name="name"/> that the CALLING ASSEMBLY
    /// registered, now (for an admin command, once the cause of the failures is fixed). Another
    /// plugin's job of the same name is not touched: a plugin that chose
    /// <see cref="JobFailurePolicy.DisablePermanently"/> decides when its job comes back. Logged per
    /// job. Thread affinity: MAIN THREAD.
    /// </summary>
    /// <param name="name">The name given at registration (ordinal comparison).</param>
    /// <returns>Number of jobs re-enabled.</returns>
    [MethodImpl(MethodImplOptions.NoInlining)] // GetCallingAssembly must see the caller's frame
    public static int Reenable(string name)
    {
        MainThread.ThrowIfNotMainThread("SlClock.Reenable");
        return ClockJobTable.Reenable(Assembly.GetCallingAssembly(), name);
    }

    /// <summary>
    /// Cancels every <see cref="ClockScope.Round"/> job registered before the current frame.
    /// <see cref="Install"/> subscribes it to <c>ServerEvents.RoundRestarted</c>. Internal on
    /// purpose: called by a plugin in the middle of a round it would cancel every plugin's Round jobs.
    /// Round jobs registered in the same frame, typically by another plugin's RoundRestarted handler
    /// that ran first, are kept: they were made for the next round, and handler order must not
    /// decide whether they survive. Thread affinity: MAIN THREAD.
    /// </summary>
    internal static void OnRoundRestart()
    {
        MainThread.ThrowIfNotMainThread("SlClock.OnRoundRestart");
        ClockJobTable.CancelRoundJobs(UnityEngine.Time.frameCount);

        if (!ClockTicker.InTick)
        {
            Compact();
        }
    }

    /// <summary>
    /// Obsolete: <see cref="Install"/> (run by every registration) now subscribes
    /// <c>OnRoundRestart</c> to <c>ServerEvents.RoundRestarted</c> itself. Calling this
    /// installs the clock, which does that wiring once per process. Thread affinity: MAIN THREAD.
    /// </summary>
    [Obsolete("Since SlKit 0.2.0 SlClock.Install() (and every registration) wires ClockScope.Round to ServerEvents.RoundRestarted itself. Remove the call.")]
    public static void AutoWireLabApi() => Install();

    // The dirty flag and the compaction stay on SlClock under these names: a host that precompiles
    // the clock's hot path looks both members up by name (reflection) to have them compiled before the
    // first wake, so renaming them silently turns that precompilation into a no-op.

    /// <summary>Flags that a job was disposed so the next frame compacts the list. Any thread.</summary>
    internal static void MarkDirty() => Interlocked.Exchange(ref _dirty, 1);

    /// <summary>Reads and clears the flag set by <see cref="MarkDirty"/>. Main thread only.</summary>
    internal static bool TakeDirty() => Interlocked.Exchange(ref _dirty, 0) != 0;

    /// <summary>Drops the disposed jobs from the table, keeping the order of the rest. Main thread only.</summary>
    internal static void Compact()
    {
        int write = 0;
        for (int read = 0; read < ClockJobTable.Jobs.Count; read++)
        {
            ClockJob job = ClockJobTable.Jobs[read];
            if (job.IsDisposed)
            {
                continue;
            }

            if (write != read)
            {
                ClockJobTable.Jobs[write] = job;
            }

            write++;
        }

        if (write < ClockJobTable.Jobs.Count)
        {
            ClockJobTable.Jobs.RemoveRange(write, ClockJobTable.Jobs.Count - write);
        }
    }
}
