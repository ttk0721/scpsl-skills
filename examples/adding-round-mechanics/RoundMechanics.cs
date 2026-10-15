using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using LabApi.Events.Arguments.ServerEvents;
using LabApi.Events.Handlers;
using LabApi.Features.Console;
using LabApi.Features.Wrappers;
using LabApi.Loader.Features.Plugins;
using SlKit;

namespace Examples.AddingRoundMechanics
{
    /// <summary>
    /// Config: public get/set properties only, defaults as initializers. Every feature is off at 0,
    /// and <see cref="IsEnabled"/> turns the whole plugin off without removing it.
    /// </summary>
    public sealed class RoundMechanicsConfig
    {
        [Description("Kill switch. False = the plugin subscribes to nothing and touches no round state.")]
        public bool IsEnabled { get; set; } = true;

        [Description("Hold the lobby until this many real players are ready. 0 = off.")]
        public int LobbyMinPlayers { get; set; } = 4;

        [Description("Safety valve: release the lobby after this many seconds with at least one player waiting, whatever the count.")]
        public int LobbyMaxHoldSeconds { get; set; } = 180;

        [Description("Hard round time limit in minutes. At the limit the round ends as a draw. 0 = off.")]
        public int RoundLimitMinutes { get; set; } = 20;

        [Description("Minutes before the limit at which a warning goes out.")]
        public List<int> RoundLimitWarnMinutes { get; set; } = new List<int> { 5, 1 };

        [Description("The round cannot end on team elimination before this many seconds. 0 = off.")]
        public int MinRoundSeconds { get; set; } = 0;

        [Description("No respawn waves during the last N minutes before the limit. Needs RoundLimitMinutes. 0 = off.")]
        public int NoWavesInFinalMinutes { get; set; } = 3;

        [Description("Never spawn mini waves.")]
        public bool DisableMiniWaves { get; set; } = false;

        [Description("Seconds a broadcast stays on screen.")]
        public ushort BroadcastSeconds { get; set; } = 6;

        [Description("Optional CASSIE line spoken with each warning. {minutes} is replaced. Empty = broadcast only. Unknown words are skipped silently, so test it.")]
        public string CassieWarning { get; set; } = "";
    }

    /// <summary>
    /// Custom round flow on one clock and one owner of per-round state: a lobby hold, a hard time
    /// limit with warnings, an end-condition floor, and wave suppression. Everything runs on the
    /// main thread and nothing is per player, so 60 players cost the same as 6.
    /// </summary>
    public sealed class RoundMechanicsPlugin : Plugin<RoundMechanicsConfig>
    {
        // After cancelling a wave, push its timer this far ahead. See OnWaveTeamSelecting.
        private const float WaveRecheckSeconds = 15f;

        private bool _subscribed;

        // Per-round state: plain values and handles, never Player or hub references.
        private bool _roundActive;      // RoundStarted .. RoundEnded of THIS round
        private bool _limitReached;     // our own time limit asked for the end
        private bool _ownsLobbyLock;    // Round.IsLobbyLocked is a static that survives restarts
        private readonly Stopwatch _roundClock = new Stopwatch();
        private readonly Stopwatch _lobbyWait = new Stopwatch();
        private readonly List<IDisposable> _roundJobs = new List<IDisposable>(4);
        private IDisposable _lobbyJob;

        public override string Name => "AddingRoundMechanics";
        public override string Description => "Example: lobby hold, round time limit, end conditions, wave suppression.";
        public override string Author => "scpsl-skills";
        public override Version Version => new Version(1, 0, 0);
        public override Version RequiredApiVersion => new Version(1, 1, 7);

        // An empty or broken config.yml deserialises to null.
        private RoundMechanicsConfig Cfg => Config ??= new RoundMechanicsConfig();

        public override void Enable()
        {
            if (_subscribed || !Cfg.IsEnabled)
                return;

            try
            {
                // Fallible work first, subscriptions last: a throw after subscribing would leave
                // handlers live with no rollback.
                SlClock.Install(); // also ends ClockScope.Round jobs at RoundRestarted

                ServerEvents.WaitingForPlayers += OnWaitingForPlayers;
                ServerEvents.RoundStarted += OnRoundStarted;
                ServerEvents.RoundEndingConditionsCheck += OnEndingConditionsCheck;
                ServerEvents.RoundEnding += OnRoundEnding;
                ServerEvents.RoundEnded += OnRoundEnded;
                ServerEvents.RoundRestarted += OnRoundRestarted;
                ServerEvents.WaveTeamSelecting += OnWaveTeamSelecting;
                ServerEvents.Shutdown += OnShutdown;
                _subscribed = true;
            }
            catch
            {
                Unsubscribe();
                throw; // swallowing would make the loader print "enabled" for a dead plugin
            }
        }

        // Never called by LabAPI or the game. Shutdown does the real work.
        public override void Disable() => Unsubscribe();

        // ---- lifecycle ----------------------------------------------------------------------

        private void OnWaitingForPlayers()
        {
            // The new world exists. Everything of the last round is gone, so rebuild from zero.
            // Reset first, so nothing of the old world leaks even if RoundRestarted was missed.
            ResetRoundState();
            _lobbyJob?.Dispose();
            _lobbyJob = null;

            RoundMechanicsConfig cfg = Cfg;
            if (cfg.LobbyMinPlayers > 0)
            {
                // The lock is static and survives restarts, and a cancelled RoundStarting does NOT
                // hold the lobby (the lobby loop writes the start itself). The lock is the hold.
                Round.IsLobbyLocked = true;
                _ownsLobbyLock = true;
                _lobbyWait.Reset();
                // Round scope: the job also dies at RoundRestarted if the round never starts.
                _lobbyJob = SlClock.Every(TimeSpan.FromSeconds(1), LobbyTick, "rounds.lobby", ClockScope.Round);
            }
            else
            {
                ReleaseLobby("disabled in config"); // only releases a lock we took earlier
            }
        }

        private void OnRoundStarted()
        {
            // Roles are still None here. This handler only starts clocks, it assigns nothing.
            ResetRoundState();
            ReleaseLobby("round started"); // forcestart and Round.Start() bypass the lock
            _roundActive = true;
            _limitReached = false;

            // Our own stopwatch, not Round.Duration: that one stays 0 for the whole round if any
            // plugin ever cancels RoundStarting, and a limit built on it would never fire.
            _roundClock.Restart();

            RoundMechanicsConfig cfg = Cfg;
            if (cfg.RoundLimitMinutes <= 0)
                return;

            _roundJobs.Add(SlClock.After(TimeSpan.FromMinutes(cfg.RoundLimitMinutes), OnLimitReached, "rounds.limit", ClockScope.Round));

            List<int> warns = cfg.RoundLimitWarnMinutes;
            if (warns == null)
                return;
            for (int i = 0; i < warns.Count; i++)
            {
                int minutes = warns[i];
                if (minutes <= 0 || minutes >= cfg.RoundLimitMinutes)
                    continue;
                TimeSpan at = TimeSpan.FromMinutes(cfg.RoundLimitMinutes - minutes);
                _roundJobs.Add(SlClock.After(at, () => Warn(minutes), "rounds.warn", ClockScope.Round));
            }
        }

        private void OnRoundEnded(RoundEndedEventArgs ev)
        {
            // ClockScope.Round ends at RoundRestarted, which is auto_round_restart_time (about 10 s)
            // AFTER this event. Without this the warnings would fire into the summary screen.
            ResetRoundState();
        }

        private void OnRoundRestarted()
        {
            // "Restart initiated", not "restart done": the old world lives on and the event can
            // fire twice. ResetRoundState is idempotent for that reason.
            ResetRoundState();
        }

        private void OnShutdown()
        {
            // The only reliable last call (process exit). Statics die with the process, but release
            // anyway so a shutdown that does not exit never leaves the lobby locked.
            ResetRoundState();
            ReleaseLobby("shutdown");
            Unsubscribe();
        }

        // ---- lobby --------------------------------------------------------------------------

        private void LobbyTick()
        {
            RoundMechanicsConfig cfg = Cfg;
            if (cfg.LobbyMinPlayers <= 0)
            {
                ReleaseLobby("disabled in config");
                return;
            }

            int ready = CountRealPlayers();
            if (ready == 0)
            {
                _lobbyWait.Reset(); // an empty server never times out into a start
                return;
            }

            if (!_lobbyWait.IsRunning)
                _lobbyWait.Start();

            if (ready >= cfg.LobbyMinPlayers)
                ReleaseLobby(ready + " players ready");
            else if (_lobbyWait.Elapsed.TotalSeconds >= cfg.LobbyMaxHoldSeconds)
                ReleaseLobby("hold timed out with " + ready + " players");
        }

        private static int CountRealPlayers()
        {
            // Player.List, not ReadyList: ReadyList is a LINQ query that allocates per call, and
            // it includes dummies, which must not count toward a real-player minimum.
            int n = 0;
            foreach (Player p in Player.List)
                if (p.IsPlayer && p.IsReady)
                    n++;
            return n;
        }

        private void ReleaseLobby(string why)
        {
            _lobbyJob?.Dispose();
            _lobbyJob = null;
            _lobbyWait.Reset();

            // Release only what we took: another plugin or an admin may hold the lock on purpose.
            if (!_ownsLobbyLock)
                return;
            _ownsLobbyLock = false;
            Round.IsLobbyLocked = false;
            // The vanilla loop resets its countdown while locked, so a fresh lobby_waiting_time
            // countdown starts now. Logged once per round: rare, not a hot path.
            Logger.Info("Lobby released: " + why);
        }

        // ---- end conditions -----------------------------------------------------------------

        private void OnLimitReached()
        {
            if (!_roundActive || _limitReached)
                return;

            _limitReached = true;
            Announce("Time limit reached. The round is over.", null);

            // Both halves, on purpose. End(true) opens the gate (round lock, one-hub rule, 15 s
            // guard) and OnEndingConditionsCheck then supplies the verdict. Either alone does not
            // end a round while two or more teams are alive.
            Round.End(true);
        }

        private void OnEndingConditionsCheck(RoundEndingConditionsCheckEventArgs ev)
        {
            if (!_roundActive)
                return;

            // CanEnd arrives pre-filled with the vanilla verdict and the last writer wins, so only
            // overwrite it when we have something to say. Runs every 2.5 s: cost is irrelevant.
            if (_limitReached)
            {
                ev.CanEnd = true;
                return;
            }

            int floor = Cfg.MinRoundSeconds;
            if (floor > 0 && _roundClock.Elapsed.TotalSeconds < floor)
                ev.CanEnd = false;
        }

        private void OnRoundEnding(RoundEndingEventArgs ev)
        {
            // A timeout has no natural winner. Draw is only a label for the summary screen.
            if (_limitReached)
                ev.LeadingTeam = RoundSummary.LeadingTeam.Draw;
        }

        // ---- waves --------------------------------------------------------------------------

        private void OnWaveTeamSelecting(WaveTeamSelectingEventArgs ev)
        {
            // Cancel HERE, not at WaveRespawning: that one still burns a respawn token and resets
            // every wave timer. But a cancel leaves the wave "ready", so the game re-raises this
            // event EVERY FRAME and the first ready wave starves the others. The fix is to push
            // the wave's timer ahead in the same call, so it is raised once per recheck, not per frame.
            if (!_roundActive)
                return;

            RespawnWave wave = RespawnWaves.Get(ev.Wave);
            if (wave == null)
                return;

            RoundMechanicsConfig cfg = Cfg;
            bool block = cfg.DisableMiniWaves && wave is MiniRespawnWave;

            if (!block && cfg.NoWavesInFinalMinutes > 0 && cfg.RoundLimitMinutes > 0)
            {
                double remaining = cfg.RoundLimitMinutes * 60.0 - _roundClock.Elapsed.TotalSeconds;
                block = remaining <= cfg.NoWavesInFinalMinutes * 60.0;
            }

            if (!block)
                return;

            ev.IsAllowed = false;
            wave.TimeLeft = WaveRecheckSeconds;
        }

        // ---- announcements ------------------------------------------------------------------

        private void Warn(int minutes)
        {
            if (!_roundActive || _limitReached)
                return;
            string text = "Round ends in " + minutes + (minutes == 1 ? " minute." : " minutes.");
            Announce(text, minutes.ToString());
        }

        private void Announce(string text, string minutes)
        {
            RoundMechanicsConfig cfg = Cfg;

            // One broadcast to everyone, once. Broadcasts are for rare announcements, never a ticker.
            Server.SendBroadcast(text, cfg.BroadcastSeconds);

            string cassie = cfg.CassieWarning;
            if (string.IsNullOrEmpty(cassie) || minutes == null)
                return;

            // Message(string) alone shows no subtitles, so pass them. The queue is cleared at restart.
            Announcer.Message(cassie.Replace("{minutes}", minutes), text);
        }

        // ---- plumbing -----------------------------------------------------------------------

        private void ResetRoundState()
        {
            _roundActive = false;
            _limitReached = false;
            _roundClock.Reset();

            for (int i = 0; i < _roundJobs.Count; i++)
                _roundJobs[i].Dispose();
            _roundJobs.Clear();
        }

        private void Unsubscribe()
        {
            if (!_subscribed)
                return;

            ServerEvents.WaitingForPlayers -= OnWaitingForPlayers;
            ServerEvents.RoundStarted -= OnRoundStarted;
            ServerEvents.RoundEndingConditionsCheck -= OnEndingConditionsCheck;
            ServerEvents.RoundEnding -= OnRoundEnding;
            ServerEvents.RoundEnded -= OnRoundEnded;
            ServerEvents.RoundRestarted -= OnRoundRestarted;
            ServerEvents.WaveTeamSelecting -= OnWaveTeamSelecting;
            ServerEvents.Shutdown -= OnShutdown;
            _subscribed = false;
        }
    }
}
