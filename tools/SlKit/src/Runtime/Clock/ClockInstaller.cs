using System;
using UnityEngine.LowLevel;
using UpdatePhase = UnityEngine.PlayerLoop.Update;

namespace SlKit;

/// <summary>
/// Puts <see cref="SlClock"/>'s entry into the PlayerLoop: the install that runs once per process (entry, main-thread
/// capture, round-restart subscription, log lines, check for other SlKit copies) and the cheap check, made by every
/// registration, that the entry is still there. Main thread only, except reading <see cref="IsInstalled"/>.
/// </summary>
internal static class ClockInstaller
{
    private static volatile bool _installed;
    private static bool _roundRestartWired;
    private static bool _copiesChecked;

    internal static bool IsInstalled => _installed;

    /// <summary>The work of <see cref="SlClock.Install"/>, which documents the contract.</summary>
    internal static void Install()
    {
        // Unity's own main-thread check (MainThread.IsMainThread), valid before anything was captured.
        MainThread.ThrowIfNotMainThread("SlClock.Install");
        WireRoundRestart();
        if (_installed && PlayerLoopInjector.IsPresent(typeof(SlKitClockMarker)))
        {
            // The entry is there, so the stall was a real pause (scene load, debugger), not a dropped
            // entry. Restart the stall window, otherwise every registration until the next frame
            // would pay for reading the whole PlayerLoop again.
            ClockTicker.RestartStallWindow();
            return;
        }

        PlayerLoopSystem system = new PlayerLoopSystem
        {
            type = typeof(SlKitClockMarker),
            updateDelegate = ClockTicker.TickDelegate,
        };

        bool inserted = PlayerLoopInjector.TryInsert(
            typeof(UpdatePhase),
            system,
            LoopPosition.After,
            typeof(UpdatePhase.ScriptRunBehaviourUpdate),
            out bool anchorFound);

        MainThread.CaptureCurrentThread();
        ClockTicker.RestartStallWindow();
        bool wasInstalled = _installed;
        _installed = true;

        if (inserted)
        {
            string budget = MainThread.DescribeBudget(out string budgetProblem);
            SlLog.Info(
                (wasInstalled ? "Clock re-inserted (it had been removed from the PlayerLoop)" : "Clock installed")
                + (anchorFound ? " in Update after ScriptRunBehaviourUpdate." : " at the end of Update (ScriptRunBehaviourUpdate not found).")
                + " SlKit " + CopyDetector.SelfVersion + ", " + budget
                + ", ClockScope.Round jobs end at ServerEvents.RoundRestarted.");
            if (budgetProblem != null && !wasInstalled)
            {
                SlLog.Warn("Ignored the main-thread budget setting: " + budgetProblem + ". Using the default.");
            }
        }
        else if (!PlayerLoopInjector.IsPresent(typeof(SlKitClockMarker)))
        {
            _installed = false;
            throw new InvalidOperationException("SlKit could not find the Update phase in the current PlayerLoop.");
        }

        if (!_copiesChecked)
        {
            // Once per process: every plugin is loaded before any Enable() runs, so the assembly list
            // is complete by now.
            _copiesChecked = true;
            CopyDetector.WarnIfOtherCopies();
        }
    }

    /// <summary>Run by every registration: installs the clock, or installs it again when its entry looks dropped.</summary>
    /// <param name="api">The registering API, for the main-thread check.</param>
    internal static void EnsureInstalled(string api)
    {
        MainThread.ThrowIfNotMainThread(api);
        if (!_installed)
        {
            Install();
            return;
        }

        // Another plugin may have rebuilt the PlayerLoop and dropped our entry. A stalled frame
        // counter is the cheap signal; only then pay for reading the loop (it allocates).
        if (ClockTicker.HasStalled())
        {
            Install();
        }
    }

    // Once per process and SlKit copy. Main thread only: LabAPI's static events are plain
    // multicast delegates without locks. Called before the PlayerLoop is touched, so Round jobs are
    // covered even if inserting the clock fails.
    private static void WireRoundRestart()
    {
        if (_roundRestartWired)
        {
            return;
        }

        LabApi.Events.Handlers.ServerEvents.RoundRestarted += SlClock.OnRoundRestart;
        _roundRestartWired = true;
    }
}
