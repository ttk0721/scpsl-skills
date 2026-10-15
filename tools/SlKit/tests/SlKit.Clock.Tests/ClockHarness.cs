using System;
using System.Threading;
using UnityEngine.LowLevel;

namespace SlKit.Clock.Tests;

/// <summary>What the clock tests share: the thread that plays Unity's main thread, one engine frame at a time, and a helper to run work on another thread.</summary>
internal static class ClockHarness
{
    /// <summary>Managed id of the thread the tests treat as Unity's main thread; set once by Program.</summary>
    internal static int MainThreadId { get; set; }

    /// <summary>One engine frame on the main thread: the frame number advances, then the loop runs.</summary>
    internal static void NextFrame()
    {
        UnityEngine.Time.frameCount++;
        PlayerLoop.RunFrame();
    }

    internal static T OnWorker<T>(Func<T> work)
    {
        T result = default;
        Exception crash = null;
        Thread thread = new Thread(() =>
        {
            try
            {
                result = work();
            }
            catch (Exception e)
            {
                crash = e;
            }
        });
        thread.Start();
        thread.Join();
        if (crash != null)
        {
            throw new Exception("worker crashed: " + crash);
        }

        return result;
    }
}
