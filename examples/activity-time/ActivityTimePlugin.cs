using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Examples.ActivityTime.Core;
using LabApi.Events.Arguments.PlayerEvents;
using LabApi.Events.Handlers;
using LabApi.Features.Wrappers;
using LabApi.Loader;
using LabApi.Loader.Features.Plugins;
using SlKit;
using UnityEngine;
using Log = LabApi.Features.Console.Logger; // UnityEngine also has a Logger type

namespace Examples.ActivityTime
{
    /// <summary>
    /// Thin LabAPI wiring around Core: gathers players, converts Vector3 to Pos3, forwards to the
    /// ledger. Every decision worth testing lives in Core/ and is unit-tested on plain .NET.
    /// </summary>
    public sealed class ActivityTimePlugin : Plugin<ActivityConfig>
    {
        public override string Name => "ActivityTime";
        public override string Description => "Counts active (non-AFK) play time per player";
        public override string Author => "scpsl-skills examples";
        public override Version Version => new Version(1, 0, 0);
        public override Version RequiredApiVersion => new Version(1, 1, 7);

        // One owner of the state: this class. Keyed by UserId inside the ledger, no Player stored.
        private readonly ActivityLedger _ledger = new ActivityLedger();
        private readonly List<Player> _batch = new List<Player>(64);
        private readonly List<KeyValuePair<string, double>> _totals = new List<KeyValuePair<string, double>>(64);
        private ActivityPolicy _policy = new ActivityPolicy(0.5, 30, 2, null);
        private IDisposable _sampleJob;
        private bool _enabled;

        private ActivityConfig Cfg => Config ??= new ActivityConfig();

        public override void LoadConfigs()
        {
            base.LoadConfigs();
            ActivityConfig c = Cfg;
            double period = Math.Max(0.25, c.SamplePeriodSeconds);
            // A sample may credit at most two periods: a late sample or a hitch never over-credits.
            _policy = new ActivityPolicy(c.MinMoveMeters, c.IdleWindowSeconds, 2 * period, c.ExcludedGroups);
        }

        public override void Enable()
        {
            if (_enabled) return;
            try
            {
                SlClock.Install(); // also ends ClockScope.Round jobs at RoundRestarted

                ServerEvents.WaitingForPlayers += OnWaitingForPlayers;
                ServerEvents.Shutdown += OnShutdown;
                PlayerEvents.Left += OnLeft;

                // One staggered job: each ready player sampled once per period, spread over frames
                // (1 player per frame at 60 players and 60 fps). No coroutine per player.
                TimeSpan period = TimeSpan.FromSeconds(Math.Max(0.25, Cfg.SamplePeriodSeconds));
                _sampleJob = SlClock.EveryStaggered<Player>(period, FillBatch, SampleOne, "activity.sample");
                _enabled = true;
            }
            catch
            {
                Teardown();
                throw;
            }
        }

        public override void Disable() => Teardown(); // never called by the game; nothing relies on it

        private void Teardown()
        {
            ServerEvents.WaitingForPlayers -= OnWaitingForPlayers;
            ServerEvents.Shutdown -= OnShutdown;
            PlayerEvents.Left -= OnLeft;
            _sampleJob?.Dispose();
            _sampleJob = null;
            _enabled = false;
        }

        // Called once per period by SlClock (main thread). Reuses one list: no allocation per player
        // (the interface foreach boxes one enumerator per period, which is negligible at 1 Hz).
        private IReadOnlyList<Player> FillBatch()
        {
            _batch.Clear();
            if (!Cfg.Enabled) return _batch;
            ActivityPolicy policy = _policy;
            foreach (Player p in Player.List)
            {
                if (policy.ShouldTrack(p.IsHost || p.IsDummy, p.IsReady, p.PermissionsGroupName)
                    && ActivityPolicy.IsAccountId(p.UserId))
                    _batch.Add(p);
            }
            return _batch;
        }

        private void SampleOne(Player p)
        {
            if (p.IsDestroyed) return; // the batch is a snapshot; players leave mid-period
            Vector3 v = p.Position;
            double now = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
            _ledger.Sample(p.UserId, new Pos3(v.x, v.y, v.z), p.IsAlive, now, _policy);
        }

        private void OnWaitingForPlayers() => _ledger.ResetMotion(); // new map; totals survive

        private void OnLeft(PlayerLeftEventArgs ev)
        {
            try
            {
                // During a restart Left fires for every hub; ResetMotion covers that case.
                if (RoundRestarting.RoundRestart.IsRoundRestarting) return;
                _ledger.ForgetMotion(ev.Player.UserId);
            }
            catch (Exception e) { Log.Error("[ActivityTime] Left: " + e); } // per-lifecycle event, not hot
        }

        private void OnShutdown()
        {
            // The process quits within about a second: synchronous, small, no async work.
            try
            {
                _ledger.CopyTotals(_totals);
                var lines = new List<string>(_totals.Count);
                foreach (KeyValuePair<string, double> kv in _totals)
                    lines.Add(kv.Key + "\t" + ((long)kv.Value).ToString());
                string path = Path.Combine(this.GetConfigDirectory().FullName, "activity.tsv");
                // Append, never overwrite: nothing is loaded at boot, so each process lifetime adds one
                // block of totals and the reader sums per UserId. A crash loses this lifetime's block;
                // a periodic flush (adding-per-player-tracking) is the persistence task, not this one.
                // scpsl-lint: ignore SL007 -- shutdown flush must finish before the process exits
                File.AppendAllLines(path, lines);
            }
            catch (Exception e) { Log.Error("[ActivityTime] Shutdown flush: " + e); }
        }
    }
}
