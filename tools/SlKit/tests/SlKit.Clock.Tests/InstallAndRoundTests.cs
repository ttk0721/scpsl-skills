using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using LabApi.Events.Handlers;
using SlKit.Core;
using UnityEngine.LowLevel;
using static SlKit.TestSupport.TestKit;
using static SlKit.Clock.Tests.ClockHarness;

namespace SlKit.Clock.Tests;

/// <summary>What Install wires up by itself, and how ClockScope.Round jobs end at a round restart.</summary>
internal static class InstallAndRoundTests
{
    internal static void Register()
    {
        // Install wires the round restart
        Run(nameof(Install_OnMainThreadWiresRoundRestartOnce), Install_OnMainThreadWiresRoundRestartOnce);
        Run(nameof(Install_LogsBudgetAndRoundWiring), Install_LogsBudgetAndRoundWiring);
        Run(nameof(Thread_InstalledClockStillRefusesOtherThreads), Thread_InstalledClockStillRefusesOtherThreads);
        Run(nameof(Round_AfterDefaultsToRoundAndEndsAtRestart), Round_AfterDefaultsToRoundAndEndsAtRestart);
        Run(nameof(Round_JobRegisteredInTheRestartFrameIsKept), Round_JobRegisteredInTheRestartFrameIsKept);
    }

    private static void Thread_InstalledClockStillRefusesOtherThreads()
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

        True(error is InvalidOperationException, "Install from a worker after install throws too");
        True(MainThread.IsMainThread, "the main thread is still the main thread");
    }

    // ---------------------------------------------------------------------------------------
    // ClockScope.Round works without any wiring by the plugin.
    // ---------------------------------------------------------------------------------------

    private static void Install_OnMainThreadWiresRoundRestartOnce()
    {
        SlClock.Install();
        True(SlClock.IsInstalled, "installed");
        Equal(MainThreadId, PlayerLoop.LastWriterThreadId, "the PlayerLoop was written on the main thread");
        Equal(1, ServerEvents.RoundRestartedSubscribers, "Install subscribed OnRoundRestart");

        SlClock.Install();
#pragma warning disable CS0618 // the obsolete call must stay harmless for plugins that still make it
        SlClock.AutoWireLabApi();
        Equal(ClockJobOptions.DefaultMaxConsecutiveFailures, SlClock.MaxConsecutiveFailures, "the obsolete read-only default is the per-job default");
#pragma warning restore CS0618
        Equal(1, ServerEvents.RoundRestartedSubscribers, "still exactly one subscription");
        True(MainThread.IsKnown, "main thread captured");
    }

    private static void Install_LogsBudgetAndRoundWiring()
    {
        List<string> lines = LabApi.Features.Console.Logger.Take();
        string installed = lines.FirstOrDefault(l => l.Contains("Clock installed"));
        True(installed != null, "an install line was logged: " + string.Join(" | ", lines));
        True(installed.Contains("main-thread budget 2.5 ms (from " + HostBudget.VariableName + ")"), "the line names the owner's budget: " + installed);
        True(installed.Contains("RoundRestarted"), "the line says Round jobs end at RoundRestarted: " + installed);
    }

    private static void Round_AfterDefaultsToRoundAndEndsAtRestart()
    {
        bool roundJobRan = false;
        bool serverJobRan = false;
        UnityEngine.Time.frameCount = 100;
        ClockJobHandle roundJob = SlClock.After(TimeSpan.FromMilliseconds(30), () => roundJobRan = true, "k2.after");
        ClockJobHandle serverJob = SlClock.After(TimeSpan.FromMilliseconds(30), () => serverJobRan = true, "k2.after.server", ClockScope.Server);
        Equal(ClockScope.Round, roundJob.Scope, "After defaults to Round");

        UnityEngine.Time.frameCount = 101;
        ServerEvents.RaiseRoundRestarted(); // what LabAPI does at a round restart; the plugin wired nothing
        True(roundJob.IsDisposed, "the Round job was cancelled by the restart");
        False(serverJob.IsDisposed, "a Server job survives the restart");

        Thread.Sleep(50);
        NextFrame();
        False(roundJobRan, "the cancelled job never ran in the next round");
        True(serverJobRan, "the Server job ran");
    }

    private static void Round_JobRegisteredInTheRestartFrameIsKept()
    {
        UnityEngine.Time.frameCount = 200;
        ClockJobHandle old = SlClock.After(TimeSpan.FromMinutes(1), () => { }, "k2.old-round");

        // Frame 201: another plugin's RoundRestarted handler happens to run before SlKit's and
        // schedules work for the next round. SlKit's handler (OnRoundRestart) runs afterwards.
        UnityEngine.Time.frameCount = 201;
        ClockJobHandle next = SlClock.After(TimeSpan.FromMinutes(1), () => { }, "k2.next-round");
        SlClock.OnRoundRestart();

        True(old.IsDisposed, "the job from the old round is cancelled");
        False(next.IsDisposed, "the job registered during the restart is kept, whatever the handler order");
        next.Dispose();
    }
}
