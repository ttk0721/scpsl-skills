using System;
using LabApi.Events.Handlers;
using UnityEngine.LowLevel;
using static SlKit.TestSupport.TestKit;
using static SlKit.Clock.Tests.ClockHarness;

namespace SlKit.Clock.Tests;

/// <summary>Which thread the clock and MainThread accept, before anything has been installed.</summary>
internal static class ThreadChecksTests
{
    internal static void Register()
    {
        // Thread checks, before anything is installed
        Run(nameof(Thread_IsMainThreadIsRightBeforeInstall), Thread_IsMainThreadIsRightBeforeInstall);
        Run(nameof(Thread_InstallOffMainThreadThrowsAndChangesNothing), Thread_InstallOffMainThreadThrowsAndChangesNothing);
        Run(nameof(Thread_RegistrationOffMainThreadThrowsBeforeInstall), Thread_RegistrationOffMainThreadThrowsBeforeInstall);
    }

    // ---------------------------------------------------------------------------------------
    // The main thread is known from Unity before the clock captures it.
    // ---------------------------------------------------------------------------------------

    private static void Thread_IsMainThreadIsRightBeforeInstall()
    {
        False(MainThread.IsKnown, "nothing captured yet");
        True(MainThread.IsMainThread, "the main thread is recognised before install");
        False(OnWorker(() => MainThread.IsMainThread), "a worker thread is not the main thread before install");
    }

    private static void Thread_InstallOffMainThreadThrowsAndChangesNothing()
    {
        Exception error = OnWorker(() =>
        {
            try
            {
                SlClock.Install();
                return null;
            }
            catch (Exception e)
            {
                return e;
            }
        });

        True(error is InvalidOperationException, "Install from a worker throws InvalidOperationException, got " + (error?.GetType().Name ?? "nothing"));
        Equal(0, PlayerLoop.Writes, "the PlayerLoop was not written");
        False(MainThread.IsKnown, "the worker was not recorded as the main thread");
        False(SlClock.IsInstalled, "the clock is not marked installed");
        Equal(0, ServerEvents.RoundRestartedSubscribers, "no LabAPI event was touched");
    }

    private static void Thread_RegistrationOffMainThreadThrowsBeforeInstall()
    {
        Exception error = OnWorker(() =>
        {
            try
            {
                SlClock.Every(TimeSpan.FromSeconds(1), () => { }, "worker.every");
                return null;
            }
            catch (Exception e)
            {
                return e;
            }
        });

        True(error is InvalidOperationException, "Every from a worker throws, got " + (error?.GetType().Name ?? "nothing"));
        Equal(0, SlClock.JobCount, "nothing was registered");
        Equal(0, PlayerLoop.Writes, "the PlayerLoop was not written");
    }
}
