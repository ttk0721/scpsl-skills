using System;
using System.Collections.Generic;
using LabApi.Events.Handlers;
using LabApi.Features.Wrappers;
using SlKit;

namespace Examples.PortingExiledPairs
{
    /// <summary>
    /// Pair 4 (coroutine loops to SlClock). The EXILED original is MEC:
    /// <code>
    /// private CoroutineHandle _loop;
    /// OnEnabled():  _loop = Timing.RunCoroutine(AnnounceLoop());
    /// OnDisabled(): Timing.KillCoroutines(_loop);
    ///
    /// private IEnumerator&lt;float&gt; AnnounceLoop()
    /// {
    ///     for (;;)
    ///     {
    ///         yield return Timing.WaitForSeconds(300f);
    ///         if (Round.IsStarted) Map.Broadcast(5, "announcement");
    ///     }
    /// }
    ///
    /// Handlers.Server.RoundStarted += () => Timing.CallDelayed(10f, ShowTip);
    /// // and per-player work: foreach (Player p in Player.List) p.ShowHint(...) inside a loop
    /// </code>
    /// Why not port it as a coroutine: an Update-segment wait is scaled by Time.timeScale, so in idle mode
    /// (timeScale 0.01) a 300 s wait lasts about 8 hours; a coroutine that throws dies for good and MEC only logs
    /// it; one coroutine or CallDelayed per event or per player costs allocations that grow with players.
    /// SlClock is one wall-clock scheduler: absolute Stopwatch deadlines, a try/catch per job, skipped (not replayed)
    /// periods after a freeze, per-job cost statistics.
    /// </summary>
    internal sealed class PeriodicJobs
    {
        private readonly Func<PairsConfig> _cfg;
        private readonly Messaging _messaging;
        private readonly JoinTracker _joins;
        private readonly List<Player> _visit = new List<Player>(64);   // reused: steady state allocates nothing
        private IDisposable _announce;
        private IDisposable _onlineHint;
        private IDisposable _tip;
        private string _onlineText = string.Empty;
        private bool _started;

        public PeriodicJobs(Func<PairsConfig> cfg, Messaging messaging, JoinTracker joins)
        {
            _cfg = cfg;
            _messaging = messaging;
            _joins = joins;
        }

        public void Start()
        {
            if (_started) return;

            SlClock.Install();          // main thread only, idempotent; also ends ClockScope.Round jobs at RoundRestarted

            // Timing.RunCoroutine(loop + WaitForSeconds) becomes one registered job.
            int every = _cfg().AnnounceIntervalSeconds;
            if (every > 0)
                _announce = SlClock.Every(TimeSpan.FromSeconds(every), Announce, "pairs.announce");

            // "foreach (Player p in Player.List) p.ShowHint(...)" every few seconds becomes a staggered visit:
            // each player is visited once per period, spread over the frames, instead of N messages in one frame.
            _onlineHint = SlClock.EveryStaggered<Player>(TimeSpan.FromSeconds(5), RefillVisit, ShowOnline, "pairs.online");

            // Timing.CallDelayed(10f, ...) becomes SlClock.After. It defaults to ClockScope.Round, so a tip
            // scheduled in one round is dropped at the restart instead of firing in the next round.
            ServerEvents.RoundStarted += OnRoundStarted;
            _started = true;
        }

        public void Stop()
        {
            if (!_started) return;
            ServerEvents.RoundStarted -= OnRoundStarted;
            _announce?.Dispose();
            _onlineHint?.Dispose();
            _tip?.Dispose();
            _announce = null;
            _onlineHint = null;
            _tip = null;
            _started = false;
        }

        private void Announce()
        {
            // EXILED checked Round.IsStarted inside the loop. LabAPI: Round.IsRoundStarted.
            if (!Round.IsRoundStarted) return;
            _messaging.BroadcastAll(_messaging.Text.Announcement, 5);
        }

        private void OnRoundStarted()
        {
            _tip?.Dispose();   // never stack two tips if RoundStarted repeats
            _tip = SlClock.After(TimeSpan.FromSeconds(10), ShowTip, "pairs.roundtip");
        }

        private void ShowTip() => _messaging.HintAll(_messaging.Text.RoundTip, 5f);

        // Called once per period, after the previous period's items are done, so refilling is safe.
        private IReadOnlyList<Player> RefillVisit()
        {
            PlayerScan.Snapshot(_visit);
            _onlineText = _messaging.Text.OnlineHint.Replace("{count}", _joins.Count.ToString());   // computed once per period, not per player
            return _visit;
        }

        private void ShowOnline(Player p)
        {
            if (p.IsDestroyed) return;   // the list is a snapshot: a player can leave mid-period
            p.SendHint(_onlineText, 4f);
        }
    }
}
