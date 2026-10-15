using System;
using LabApi.Events.Arguments.PlayerEvents;
using LabApi.Events.Handlers;
using LabApi.Features.Console;
using LabApi.Features.Wrappers;
using LabApi.Loader.Features.Plugins;
using SlKit;

namespace Examples.VerifiableHandler
{
    /// <summary>
    /// A feature written so that every claim about it can be proven from the outside:
    /// <list type="bullet">
    /// <item>handler works: one log line on the first join of each round (a REAL client: the game never
    /// raises <c>Joined</c> for dummies), and a CSV marker for sl-probe;</item>
    /// <item>cheap: the periodic work is a NAMED SlClock job, so its microseconds appear in
    /// <c>SlClock.Stats</c> and in <c>slprobe status</c>;</item>
    /// <item>survives a round restart: the reset line at WaitingForPlayers shows the previous round's
    /// totals and zeroed counters, so a restart on a test server is visible in the log;</item>
    /// <item>loads: nothing to add, the loader prints "Successfully enabled 'VerifiableHandler'"
    /// itself, and a throw in <see cref="Enable"/> is rethrown so that line cannot lie.</item>
    /// </list>
    /// Everything here runs on the main thread.
    /// </summary>
    public sealed class VerifiableHandlerPlugin : Plugin
    {
        private static readonly TimeSpan SweepPeriod = TimeSpan.FromSeconds(10);

        private bool _subscribed;
        private IDisposable _sweepJob;

        // Per-round state: plain numbers, no Player references, reset at WaitingForPlayers.
        private int _joinsThisRound;
        private int _peakPlayersThisRound;

        public override string Name => "VerifiableHandler";
        public override string Description => "Example: a feature that can prove it works.";
        public override string Author => "scpsl-skills";
        public override Version Version => new Version(1, 0, 0);
        public override Version RequiredApiVersion => new Version(1, 1, 7);

        public override void Enable()
        {
            if (_subscribed)
                return; // idempotent: a second Enable must not stack a second set of handlers

            try
            {
                // Fallible work first. The job name is what shows up in slprobe status.
                SlClock.Install();
                _sweepJob = SlClock.Every(SweepPeriod, Sweep, "verifiable.sweep");

                // Subscriptions last, so a throw above leaves nothing live.
                PlayerEvents.Joined += OnJoined;
                ServerEvents.WaitingForPlayers += OnWaitingForPlayers;
                ServerEvents.Shutdown += OnShutdown;
                _subscribed = true;
            }
            catch
            {
                // Undo, then rethrow: swallowing here would make the loader print
                // "Successfully enabled" for a plugin that is not running.
                Release();
                throw;
            }
        }

        public override void Disable() => Release(); // never called by the game; Shutdown does the real work

        // PlayerEvents.Joined is a per-lifecycle event (once per connection), so a branch and an
        // increment are all it costs. The log line is limited to the first join of a round.
        private void OnJoined(PlayerJoinedEventArgs ev)
        {
            if (ev.Player == null)
                return;

            _joinsThisRound++;
            FrameProfiler.Mark("verifiable.joined"); // no-op unless the sl-probe CSV is on

            if (_joinsThisRound == 1)
                Logger.Info("first join this round handled; handler is live");
        }

        // Runs from the clock on the main thread, every 10 s, never on a hot path.
        // Player.List also holds the host, NPCs and not-yet-authenticated connections, so count
        // real, ready players only. A plain foreach over the live collection: no LINQ, no allocation.
        private void Sweep()
        {
            int online = 0;
            foreach (Player p in Player.List)
            {
                if (p.IsPlayer && p.IsReady)
                    online++;
            }

            if (online > _peakPlayersThisRound)
                _peakPlayersThisRound = online;
        }

        // Fires every round, after the old scene is gone. This line is the restart proof:
        // totals of the round that just ended, then counters back at zero.
        private void OnWaitingForPlayers()
        {
            Logger.Info($"round reset: previous round joins={_joinsThisRound}, peak players={_peakPlayersThisRound}");
            _joinsThisRound = 0;
            _peakPlayersThisRound = 0;
        }

        private void OnShutdown()
        {
            Logger.Info($"shutdown: joins this round={_joinsThisRound}"); // synchronous, nothing to flush here
            Release();
        }

        private void Release()
        {
            _sweepJob?.Dispose();
            _sweepJob = null;
            // Unconditional: "-=" of a handler that was never added is harmless, and it also undoes a
            // half-finished Enable.
            PlayerEvents.Joined -= OnJoined;
            ServerEvents.WaitingForPlayers -= OnWaitingForPlayers;
            ServerEvents.Shutdown -= OnShutdown;
            _subscribed = false;
        }
    }
}
