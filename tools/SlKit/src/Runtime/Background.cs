using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using SlKit.Core;

namespace SlKit;

/// <summary>
/// Background worker threads (background, below-normal priority, bounded queues) for IO and CPU
/// work that must not run on the game's main thread. The static methods use one shared default
/// worker; <see cref="ForOwner"/> gives a plugin a worker of its own.
/// </summary>
/// <remarks>
/// Rules the worker code must follow, because game, Mirror, LabAPI wrappers and MEC are not
/// thread-safe (plain collections, no locks; even <c>Player.Get</c> can write to a dictionary):
/// take a plain snapshot (strings, numbers, POCOs) on the main thread, pass only that snapshot to
/// the work delegate, and apply results on the main thread (the <c>onMainThread</c> callback is
/// posted there through <see cref="MainThread.Post"/>). Logging through LabAPI's <c>Logger</c> is
/// safe from the worker.
/// Dedicated threads rather than the thread pool: work stays ordered (FIFO), a slow item cannot fan
/// out into dozens of pool threads competing with the main thread, and BelowNormal priority lets the
/// OS prefer the game loop. Queues are bounded; when one is full <c>TryRun</c> returns false
/// immediately, so the main thread never waits.
/// The default worker is shared by every plugin using this SlKit copy, so one plugin's slow IO
/// delays the others' and one plugin can fill it for everybody. A plugin that does regular or slow
/// IO should use <see cref="ForOwner"/>, which keys workers by name AND calling assembly, so two
/// plugins never end up sharing one by choosing the same name. Threads start lazily and live for
/// the whole process.
/// Thread affinity: every member may be called from any thread.
/// </remarks>
public static class Background
{
    /// <summary>
    /// Name of the shared default worker in logs. Reserved: <see cref="ForOwner"/> refuses it.
    /// </summary>
    public const string DefaultName = "default";

    /// <summary>Default queue capacity of a worker.</summary>
    public const int DefaultCapacity = 1024;

    private const string UnknownAssembly = "an unknown assembly";

    private static readonly object RegistryLock = new object();

    // Owner workers keyed by (name, calling assembly). The default worker is not in here: its name
    // is reserved, so ForOwner can never hand it out by accident.
    private static readonly OwnerRegistry<BackgroundWorker> Registry = new OwnerRegistry<BackgroundWorker>();

    // Owner workers are named SlKit.Background.<name>; the default one has no suffix.
    private static readonly BackgroundWorker DefaultWorker =
        new BackgroundWorker(DefaultName, DefaultCapacity, "SlKit.Background", AssemblyName(typeof(Background).Assembly));

    /// <summary>The shared default worker the static methods use. Thread affinity: any thread.</summary>
    public static BackgroundWorker Default => DefaultWorker;

    /// <summary>
    /// Capacity of the default worker's queue: <see cref="DefaultCapacity"/> (1024). Read-only: the
    /// default worker is shared by every plugin, so no plugin may resize it for the others. A plugin
    /// that needs another size asks for a worker of its own with <see cref="ForOwner"/>.
    /// Thread affinity: any thread.
    /// </summary>
    public static int Capacity => DefaultWorker.Capacity;

    /// <summary>Items waiting for the default worker. Thread affinity: any thread.</summary>
    public static int PendingCount => DefaultWorker.PendingCount;

    /// <summary>Items the default worker refused because its queue was full. Thread affinity: any thread.</summary>
    public static long RejectedCount => DefaultWorker.RejectedCount;

    /// <summary>Work items that threw on the default worker. Thread affinity: any thread.</summary>
    public static long FailureCount => DefaultWorker.FailureCount;

    /// <summary>True when called on any SlKit worker thread (the default one or an owner's). Thread affinity: any thread.</summary>
    public static bool IsBackgroundThread => BackgroundWorker.IsAnyWorkerThread;

    /// <summary>
    /// Every worker created so far (the default one first, then in creation order), for
    /// diagnostics. A new array per read. Thread affinity: any thread.
    /// </summary>
    public static IReadOnlyList<BackgroundWorker> Workers
    {
        get
        {
            lock (RegistryLock)
            {
                BackgroundWorker[] owned = Registry.ToArray();
                BackgroundWorker[] result = new BackgroundWorker[owned.Length + 1];
                result[0] = DefaultWorker;
                Array.Copy(owned, 0, result, 1, owned.Length);
                return result;
            }
        }
    }

    /// <summary>
    /// Returns the calling assembly's worker registered under <paramref name="name"/>, creating it
    /// on first use with its own thread and its own queue of <paramref name="capacity"/> items.
    /// Calling it again from the same assembly with the same name returns the same worker (threads
    /// live for the whole process, so a plugin that calls this more than once must not leak them);
    /// a different capacity on a later call applies only if the thread has not started yet,
    /// otherwise it is ignored and logged.
    /// Workers are keyed by name AND calling assembly. If another assembly already uses
    /// <paramref name="name"/>, the caller still gets a worker of its own (thread
    /// <c>SlKit.Background.&lt;name&gt;@&lt;assembly&gt;</c>) and one warning names both assemblies:
    /// two plugins never share a worker by picking the same name.
    /// Thread affinity: any thread.
    /// </summary>
    /// <param name="name">
    /// Owner name, normally the plugin name; used in the thread name and in logs.
    /// <see cref="DefaultName"/> (in any letter case) is reserved: use <see cref="Default"/> for the shared worker.
    /// </param>
    /// <param name="capacity">Queue capacity; values below 1 become 1.</param>
    /// <returns>The calling assembly's worker for <paramref name="name"/>.</returns>
    /// <exception cref="ArgumentException"><paramref name="name"/> is null, empty or <see cref="DefaultName"/>.</exception>
    [MethodImpl(MethodImplOptions.NoInlining)] // GetCallingAssembly must see the caller's frame
    public static BackgroundWorker ForOwner(string name, int capacity = DefaultCapacity)
    {
        if (string.IsNullOrEmpty(name))
        {
            throw new ArgumentException("A worker needs an owner name.", nameof(name));
        }

        // Throw rather than hand out the shared worker: a plugin asking for "its own" worker named
        // "default" would otherwise share one queue with every plugin and never notice.
        if (string.Equals(name, DefaultName, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "\"" + DefaultName + "\" is reserved for the shared worker. Use Background.Default for it, "
                + "or your plugin's name for a worker of your own.",
                nameof(name));
        }

        Assembly caller = Assembly.GetCallingAssembly();
        BackgroundWorker worker;
        bool created = false;
        string firstOwner = null;
        lock (RegistryLock)
        {
            if (!Registry.TryGet(name, caller, out worker))
            {
                object first = Registry.FirstOwnerOf(name);
                if (first != null)
                {
                    firstOwner = AssemblyName(first as Assembly);
                }

                string owner = AssemblyName(caller);
                string threadName = "SlKit.Background." + name + (first == null ? string.Empty : "@" + owner);
                worker = new BackgroundWorker(name, capacity, threadName, owner);
                Registry.Add(name, caller, worker);
                created = true;
            }
        }

        if (created)
        {
            // Once per (name, assembly): the next call from this assembly finds the worker. Logged
            // outside the lock, because logging is slow and the lock is shared by every plugin.
            if (firstOwner != null)
            {
                SlLog.Warn(
                    "Background.ForOwner(\"" + name + "\") from " + worker.Owner + ": that name is already used by "
                    + firstOwner + ", so " + worker.Owner + " gets a separate worker (thread " + worker.ThreadName
                    + ") instead of sharing its queue. Give each plugin a unique worker name, normally its plugin Name.");
            }

            return worker;
        }

        if (Math.Max(1, capacity) != worker.Capacity && !worker.TrySetCapacity(capacity) && worker.TryMarkCapacityIgnoredReported())
        {
            SlLog.Warn(
                "Background.ForOwner(\"" + name + "\", " + capacity + ") returned the existing worker, which "
                + "has already started with capacity " + worker.Capacity + "; the new capacity is ignored.");
        }

        return worker;
    }

    /// <summary>
    /// Queues <paramref name="work"/> for the shared default worker. Never blocks. Exceptions thrown
    /// by <paramref name="work"/> are caught on the worker and logged (rate-limited).
    /// Thread affinity: any thread; <paramref name="work"/> runs on the worker and must not touch
    /// game, Unity, Mirror, MEC or LabAPI wrapper state.
    /// </summary>
    /// <param name="work">Pure .NET work on plain data (IO, serialisation, hashing, sorting).</param>
    /// <returns>True when queued; false when the queue is full (the caller decides: drop, retry, count).</returns>
    public static bool TryRun(Action work) => DefaultWorker.TryRun(work);

    /// <summary>
    /// Runs <paramref name="work"/> on the shared default worker, then posts its result to
    /// <paramref name="onMainThread"/> on the main thread (see <see cref="BackgroundWorker.TryRun{T}"/>,
    /// including the clock install it does on the main thread).
    /// Thread affinity: any thread; <paramref name="work"/> runs on the worker (plain data only),
    /// both callbacks run on the main thread.
    /// </summary>
    /// <typeparam name="T">Result type; make it a plain snapshot, not a game object.</typeparam>
    /// <param name="work">Pure .NET work producing a result.</param>
    /// <param name="onMainThread">Receives the result on the main thread; may touch game state.</param>
    /// <param name="onError">Optional; receives the exception on the main thread.</param>
    /// <returns>True when queued; false when the queue is full (nothing will be called).</returns>
    public static bool TryRun<T>(Func<T> work, Action<T> onMainThread, Action<Exception> onError = null)
        => DefaultWorker.TryRun(work, onMainThread, onError);

    private static string AssemblyName(Assembly assembly)
    {
        try
        {
            return assembly?.GetName().Name ?? UnknownAssembly;
        }
        catch (Exception)
        {
            return UnknownAssembly;
        }
    }
}
