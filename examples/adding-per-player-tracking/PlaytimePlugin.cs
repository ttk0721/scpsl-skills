using System;
using System.IO;
using LabApi.Events.Arguments.PlayerEvents;
using LabApi.Events.Handlers;
using LabApi.Features.Wrappers;
using LabApi.Loader;
using LabApi.Loader.Features.Plugins;
using SlKit;
using Logger = LabApi.Features.Console.Logger;

namespace Examples.AddingPerPlayerTracking
{
    /// <summary>
    /// Per-player time tracking (active, AFK, spectating) for 60 players: one staggered SlClock job
    /// instead of a coroutine per player, state keyed by UserId, one round-state owner reset at
    /// WaitingForPlayers, an append-only journal flushed periodically by a worker and, at Shutdown,
    /// by the main thread itself (the worker dies with the process, so it is not trusted with the last write).
    /// </summary>
    public sealed class PlaytimePlugin : Plugin<TrackerConfig>
    {
        private static ActivityTracker _liveTracker; // lets other plugins read totals through TryGetTotals

        private ActivityTracker _tracker;
        private IDisposable _sampler;
        private IDisposable _flusher;
        private bool _enabled;

        public override string Name => "AddingPerPlayerTracking";
        public override string Description => "Example: per-player playtime, AFK and spectator time.";
        public override string Author => "scpsl-skills";
        public override Version Version => new Version(1, 0, 0);
        public override Version RequiredApiVersion => new Version(1, 1, 7); // a constant, never CompiledVersion

        /// <summary>Seconds recorded for an account since the journal was created, including this boot.</summary>
        public static bool TryGetTotals(string userId, out double active, out double afk, out double spectating)
        {
            ActivityTracker t = _liveTracker;
            if (t == null)
            {
                active = afk = spectating = 0;
                return false;
            }

            return t.TryGetTotals(userId, out active, out afk, out spectating);
        }

        public override void Enable()
        {
            if (_enabled)
                return; // idempotent: EXILED-style reloads call Enable again
            if (Config == null || !Config.IsEnabled) // Config is null for an empty or broken config.yml
            {
                Logger.Info("AddingPerPlayerTracking: disabled by config.");
                return;
            }

            // Fallible work first. Server.Port is valid this early, and the folder already exists
            // because LabAPI wrote config.yml into it.
            string path = Path.Combine(this.GetConfigDirectory().FullName, Config.FileName);
            int sampleSeconds = Math.Max(1, Config.SampleSeconds);

            // The plugin's own worker thread, created once and kept for the process: the shared default
            // worker would queue our journal write behind every other plugin's IO. One job per flush, so 4 slots is plenty.
            BackgroundWorker worker = Background.ForOwner(Name, 4);
            _tracker = new ActivityTracker(() => Config, new JournalStore(path), worker, sampleSeconds); // Config is replaced on reload: read it at use
            _tracker.LoadTotals();

            SlClock.Install(); // idempotent; makes MainThread.Post and Background callbacks work from the start

            // ClockScope.Server (the default) is right here: nothing the jobs hold belongs to a
            // round. Per-round things live in RoundState and are replaced at WaitingForPlayers.
            TimeSpan period = TimeSpan.FromSeconds(sampleSeconds);
            _sampler = SlClock.EveryStaggered<Player>(period, _tracker.Refill, _tracker.Sample, "playtime.sample");
            _flusher = SlClock.Every(TimeSpan.FromSeconds(Math.Max(10, Config.FlushSeconds)), _tracker.FlushAsync, "playtime.flush");

            // Subscribe last: a throw above must not leave live handlers behind.
            PlayerEvents.Joined += OnJoined;
            PlayerEvents.Left += OnLeft;
            PlayerEvents.ChangedRole += OnChangedRole;
            ServerEvents.WaitingForPlayers += OnWaitingForPlayers;
            ServerEvents.Shutdown += OnShutdown;

            _liveTracker = _tracker;
            _enabled = true;
        }

        // LabAPI and the game never call Disable(); real cleanup is OnShutdown. Kept for hosts that do.
        public override void Disable() => OnShutdown();

        private void OnWaitingForPlayers()
        {
            // The new world exists and every hub of the last round is gone. This drops hub scratch
            // only (positions, baselines). Totals and the per-account AFK clock stay: resetting
            // them here would give an AFK player a fresh grace period every round. Players that
            // already finished joining keep working because Sample adopts anyone it has no entry for.
            _tracker.ResetRound();
        }

        private void OnJoined(PlayerJoinedEventArgs ev)
        {
            try { _tracker.OnJoined(ev.Player); }
            catch (Exception e) { RateLimitedLog.Error("playtime.joined", () => e.ToString()); }
        }

        private void OnLeft(PlayerLeftEventArgs ev)
        {
            try { _tracker.OnLeft(ev.Player); }
            catch (Exception e) { RateLimitedLog.Error("playtime.left", () => e.ToString()); }
        }

        private void OnChangedRole(PlayerChangedRoleEventArgs ev)
        {
            try { _tracker.OnChangedRole(ev.Player); }
            catch (Exception e) { RateLimitedLog.Error("playtime.role", () => e.ToString()); }
        }

        private void OnShutdown()
        {
            ActivityTracker tracker = _tracker;
            Release();           // stop sampling first, so nothing races the final flush
            tracker?.FlushFinal();
        }

        private void Release()
        {
            if (!_enabled)
                return;
            PlayerEvents.Joined -= OnJoined;
            PlayerEvents.Left -= OnLeft;
            PlayerEvents.ChangedRole -= OnChangedRole;
            ServerEvents.WaitingForPlayers -= OnWaitingForPlayers;
            ServerEvents.Shutdown -= OnShutdown;
            _sampler?.Dispose();
            _flusher?.Dispose();
            _sampler = _flusher = null;
            _liveTracker = null;
            _enabled = false;
        }
    }
}
