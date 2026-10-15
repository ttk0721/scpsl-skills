using System;
using System.Runtime.CompilerServices;
using System.Threading;

namespace SlKit;

/// <summary>
/// Unity's own answer to "is this the main thread", which <see cref="MainThread.IsMainThread"/> uses until the clock
/// has captured the thread id. Any thread.
/// </summary>
internal static class UnityMainThreadProbe
{
    private static Type _unityContextType;
    private static int _unityContextResolved;
    private static int _awaitableUnavailable;

    /// <summary>
    /// Unity's own answer to "is this the main thread", for the time before the clock has captured
    /// the thread id. Any thread; allocates nothing (the awaitable is a struct).
    /// </summary>
    /// <remarks>
    /// Primary check: <c>Awaitable.MainThreadAsync().IsCompleted</c> (Unity 6, public). It compares
    /// with the main thread Unity recorded at startup, so it cannot be fooled by a copied
    /// synchronization context on a worker thread. Fallback, for a Unity without
    /// <c>Awaitable</c>: the current <see cref="SynchronizationContext"/> is Unity's own context type,
    /// which Unity installs on the main thread only.
    /// </remarks>
    /// <returns>True on Unity's main thread.</returns>
    internal static bool ReportsMainThread()
    {
        if (Volatile.Read(ref _awaitableUnavailable) == 0)
        {
            try
            {
                return AwaitableSaysMainThread();
            }
            catch (Exception)
            {
                // TypeLoad/MissingMethod on an engine without Awaitable: remember it and use the fallback.
                Volatile.Write(ref _awaitableUnavailable, 1);
            }
        }

        SynchronizationContext context = SynchronizationContext.Current;
        if (context == null)
        {
            return false;
        }

        Type unityType = ResolveUnityContextType();
        return unityType != null && context.GetType() == unityType;
    }

    // Separate and not inlined, so a missing Awaitable type fails when THIS method is compiled and
    // the caller can catch it.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool AwaitableSaysMainThread() => UnityEngine.Awaitable.MainThreadAsync().IsCompleted;

    private static Type ResolveUnityContextType()
    {
        if (Volatile.Read(ref _unityContextResolved) != 0)
        {
            return Volatile.Read(ref _unityContextType);
        }

        Type type = null;
        try
        {
            // Internal type of UnityEngine.CoreModule, so it is looked up by name.
            type = typeof(UnityEngine.Object).Assembly.GetType("UnityEngine.UnitySynchronizationContext", false);
        }
        catch (Exception)
        {
            // Without it the check answers "not the main thread", which only means "warn, don't install".
        }

        Volatile.Write(ref _unityContextType, type);
        Volatile.Write(ref _unityContextResolved, 1);
        return type;
    }
}
