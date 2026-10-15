using System;
using System.Collections.Generic;
using LabApi.Features.Wrappers;
using PlayerRoles;
using PlayerRoles.FirstPersonControl;
using SlKit;
using SlKit.Core;
using UnityEngine;
using Logger = LabApi.Features.Console.Logger; // UnityEngine.Logger would make bare "Logger" ambiguous

namespace Examples.AddingPerPlayerTracking
{
    internal enum Bucket : byte
    {
        Skip = 0,   // default on purpose: a brand-new HubState has not been sampled yet
        Active,
        Afk,
        Spectating,
    }

    /// <summary>
    /// The AFK clock of one account. Keyed by UserId and kept across rounds, respawns and reconnects:
    /// resetting it at a round restart would hand an AFK farmer a fresh grace period every round.
    /// Holds plain numbers only, so nothing in it can point into a destroyed scene.
    /// </summary>
    internal sealed class AccountState
    {
        public long LastMovedTicks; // last time the account was seen moving, aiming or speaking
        public long LastSeenTicks;  // last sample; a long gap means the human really left
    }

    /// <summary>Per-hub scratch: baselines for deltas. Dies with the hub and the round, so it never holds anything worth keeping.</summary>
    internal sealed class HubState
    {
        public long LastSampleTicks;
        public Vector3 Position;
        public Vector2 Look;
        public bool HasBaseline;
        public Bucket LastBucket;
    }

    /// <summary>The single owner of per-round state. Replaced, never cleaned, at WaitingForPlayers.</summary>
    internal sealed class RoundState
    {
        public readonly Dictionary<string, HubState> Hubs = new Dictionary<string, HubState>(64);
    }

    /// <summary>
    /// All state and rules. Main thread only: it reads Player wrappers and is called from SlClock
    /// jobs and LabAPI handlers. The only thing that leaves the main thread is a Batch of plain
    /// numbers.
    /// </summary>
    internal sealed class ActivityTracker
    {
        // The shutdown flush waits at most this long for the journal lock, which only a worker still
        // mid-append can hold. A hung disk then costs the exit this much, not forever.
        private const int ShutdownLockWaitMs = 3000;

        private static readonly List<Player> NoPlayers = new List<Player>(0);

        private readonly Func<TrackerConfig> _config; // a delegate, because Config is replaced on reload
        private readonly JournalStore _store;
        private readonly BackgroundWorker _worker; // the plugin's own: a slow disk here must not delay another plugin's IO
        private readonly int _periodSeconds; // fixed at Enable, like the job's period: a reload must not skew the clamp
        private readonly Dictionary<string, Totals> _totals = new Dictionary<string, Totals>(1024);
        private readonly Dictionary<string, AccountState> _accounts = new Dictionary<string, AccountState>(1024); // same growth as _totals
        private readonly FlushBook _book;
        private readonly List<Player> _sample = new List<Player>(64); // reused, so the steady state allocates nothing
        private RoundState _round = new RoundState();

        public ActivityTracker(Func<TrackerConfig> config, JournalStore store, BackgroundWorker worker, int periodSeconds)
        {
            _config = config;
            _store = store;
            _worker = worker;
            _periodSeconds = Math.Max(1, periodSeconds);
            _book = new FlushBook(_totals, store.BootId);
        }

        /// <summary>Seconds dropped by the freeze clamp, so "a hitch does not count" stays visible.</summary>
        public double DroppedSeconds { get; private set; }

        private bool IsOn
        {
            get
            {
                TrackerConfig c = _config();
                return c != null && c.IsEnabled;
            }
        }

        // UserId is an identity only for authenticated online players. Offline mode gives a
        // non-empty per-connection "ID_Offline_..." and every dummy shares "ID_Dummy", so a
        // null/empty check is not enough. A tab or newline would split the id across journal
        // fields, so an id with one is not an account either.
        public static bool IsAccountId(string id) =>
            !string.IsNullOrEmpty(id)
            && !id.StartsWith("ID_", StringComparison.Ordinal)
            && id.IndexOfAny(JournalSeparators) < 0;

        private static readonly char[] JournalSeparators = { '\t', '\n', '\r' };

        public void LoadTotals()
        {
            LoadReport r = _store.LoadInto(_totals);
            if (r.Malformed > 0)
                Logger.Warn("playtime: dropped " + r.Malformed + " damaged or cut journal line(s)");
            if (r.Duplicates > 0)
                Logger.Info("playtime: ignored " + r.Duplicates + " repeated journal line(s) (a write that was retried)");
        }

        public bool TryGetTotals(string userId, out double active, out double afk, out double spectating)
        {
            if (userId != null && _totals.TryGetValue(userId, out Totals t))
            {
                active = t.Active;
                afk = t.Afk;
                spectating = t.Spectating;
                return true;
            }

            active = afk = spectating = 0;
            return false;
        }

        // ---- lifecycle -------------------------------------------------------------------

        public void ResetRound() => _round = new RoundState();

        public void OnJoined(Player p)
        {
            if (!IsOn || !IsAccountId(p.UserId))
                return;
            // Joined fires again for everyone after every restart, so replace instead of Add.
            // Only the hub scratch is replaced. The AFK clock is in _accounts and is not touched.
            _round.Hubs[p.UserId] = new HubState();
        }

        public void OnChangedRole(Player p)
        {
            // A late joiner gets ChangedRole BEFORE Joined, when there is no entry yet. That is
            // fine: there is nothing to re-baseline and the first sample starts one.
            if (p.UserId != null && _round.Hubs.TryGetValue(p.UserId, out HubState h))
                h.HasBaseline = false; // the spawn teleport must not count as walking. The AFK clock stays: a respawn is not input
        }

        public void OnLeft(Player p)
        {
            string id = p.UserId; // the wrapper is still complete inside Left
            if (id == null || !_round.Hubs.TryGetValue(id, out HubState h))
                return;
            CreditOpen(id, h, StopwatchTime.Now, _periodSeconds);
            _round.Hubs.Remove(id);
        }

        // ---- sampling (SlClock.EveryStaggered) -------------------------------------------

        /// <summary>Called once per period, after the previous period's items are done.</summary>
        public IReadOnlyList<Player> Refill()
        {
            if (!IsOn)
                return NoPlayers;

            _sample.Clear();
            foreach (Player p in Player.ReadyList) // ReadyList: authenticated players (plus dummies); never List
            {
                string id = p.UserId;
                if (!IsAccountId(id))
                {
                    if (!p.IsDummy)
                        RateLimitedLog.Warn("playtime.offline", () => "player without an account id (offline mode?): not tracked");
                    continue;
                }

                _sample.Add(p);
            }

            return _sample;
        }

        /// <summary>One player, once per period, spread over the frames by SlClock.</summary>
        public void Sample(Player p)
        {
            if (p.IsDestroyed) // the list is a snapshot; the player may have left since Refill
                return;
            TrackerConfig cfg = _config();
            if (cfg == null || !cfg.IsEnabled)
                return;

            string id = p.UserId;
            if (!IsAccountId(id))
                return;

            // Adopt: do not depend on having seen Joined (plugin enabled late, handler threw).
            if (!_round.Hubs.TryGetValue(id, out HubState h))
                _round.Hubs[id] = h = new HubState();

            long now = StopwatchTime.Now;
            AccountState account = TouchAccount(id, now, cfg);
            Bucket bucket = Classify(p);

            if (bucket == Bucket.Skip)
            {
                h.LastBucket = Bucket.Skip;
                h.LastSampleTicks = now;
                h.HasBaseline = false;
                return;
            }

            // SCP-079 and other non-FPC roles have no position to judge: they count as active.
            if (bucket == Bucket.Active && p.RoleBase is IFpcRole)
                bucket = JudgeAfk(p, h, account, now, cfg);

            // Credit the interval only when the previous sample was also a counted state. The
            // first interval after a role change is dropped: at most one period of error, in
            // exchange for never crediting time spent in Overwatch or the lobby.
            if (h.LastBucket != Bucket.Skip)
                Credit(id, bucket, ClampSeconds(h.LastSampleTicks, now, _periodSeconds));

            h.LastBucket = bucket;
            h.LastSampleTicks = now;
        }

        private static Bucket Classify(Player p)
        {
            switch (p.Role)
            {
                case RoleTypeId.Spectator:
                    return Bucket.Spectating; // counted separately: waiting is not playing and not AFK
                case RoleTypeId.Overwatch:    // staff observing
                case RoleTypeId.Filmmaker:
                case RoleTypeId.None:         // lobby: nothing to judge yet
                    return Bucket.Skip;
                default:
                    return Bucket.Active;
            }
        }

        /// <summary>
        /// Finds or starts the AFK clock of an account. Called on every sample, so the clock outlives
        /// hubs and rounds. A new account, or one unseen for longer than IdleCarryOverSeconds, starts
        /// as present: someone who really reconnected after minutes is not the idle hub of a minute ago.
        /// A round restart is a gap of seconds to a minute or two (not measured here), well inside the
        /// default window, so it carries the clock over.
        /// </summary>
        private AccountState TouchAccount(string id, long now, TrackerConfig cfg)
        {
            if (!_accounts.TryGetValue(id, out AccountState a))
            {
                _accounts[id] = a = new AccountState { LastMovedTicks = now, LastSeenTicks = now };
                return a;
            }

            double away = StopwatchTime.ToTimeSpan(now - a.LastSeenTicks).TotalSeconds;
            if (away > cfg.IdleCarryOverSeconds)
                a.LastMovedTicks = now;
            a.LastSeenTicks = now;
            return a;
        }

        private static Bucket JudgeAfk(Player p, HubState h, AccountState account, long now, TrackerConfig cfg)
        {
            // Position and LookRotation are plain reads of cached values. The setters are what send messages.
            Vector3 pos = p.Position;
            Vector2 look = p.LookRotation; // (pitch, yaw). Rotation (a quaternion) has a different pitch convention.

            // Movement needs two readings from the same world. A fresh baseline (after a spawn, a
            // reconnect or a scene reload) has nothing to compare with, so it is NOT movement and
            // the idle clock keeps running. Treating it as "assume present" resets the clock at every
            // respawn and every round restart, and an AFK farmer then keeps earning credit.
            bool moved = false;
            if (h.HasBaseline)
            {
                float move = cfg.MoveThresholdMeters;
                float turn = Mathf.Abs(Mathf.DeltaAngle(look.x, h.Look.x)) + Mathf.Abs(Mathf.DeltaAngle(look.y, h.Look.y));
                moved = (pos - h.Position).sqrMagnitude >= move * move || turn >= cfg.LookThresholdDegrees;
            }

            // Polling ServerIsSending in the sample we already run costs nothing extra. The
            // alternative, subscribing SendingVoiceMessage, raises 100 times per second per
            // speaker. A muted talker keeps ServerIsSending alive, hence the IsMuted check.
            if (!moved && cfg.VoiceCountsAsActivity)
            {
                var voice = p.VoiceModule;
                moved = voice != null && voice.ServerIsSending && !p.IsMuted;
            }

            h.Position = pos;
            h.Look = look;
            h.HasBaseline = true;
            if (moved)
                account.LastMovedTicks = now;

            double idleSeconds = StopwatchTime.ToTimeSpan(now - account.LastMovedTicks).TotalSeconds;
            return idleSeconds >= cfg.AfkAfterSeconds ? Bucket.Afk : Bucket.Active;
        }

        // ---- accounting ------------------------------------------------------------------

        // Durations come from the same Stopwatch that schedules the job. A freeze (or a 1 fps
        // idle second) must not be credited as played time, so the delta is clamped and the
        // dropped part is counted.
        private double ClampSeconds(long fromTicks, long toTicks, int samplePeriod)
        {
            double seconds = StopwatchTime.ToTimeSpan(toTicks - fromTicks).TotalSeconds;
            double max = 2.0 * Math.Max(1, samplePeriod);
            if (seconds <= max)
                return seconds;
            DroppedSeconds += seconds - max;
            return max;
        }

        private void CreditOpen(string id, HubState h, long now, int samplePeriod)
        {
            if (h.LastBucket == Bucket.Skip || h.LastSampleTicks == 0)
                return;
            Credit(id, h.LastBucket, ClampSeconds(h.LastSampleTicks, now, samplePeriod));
            h.LastSampleTicks = now; // never credit the same interval twice
        }

        private void Credit(string id, Bucket bucket, double seconds)
        {
            if (seconds <= 0)
                return;
            if (!_totals.TryGetValue(id, out Totals t))
                _totals[id] = t = new Totals();

            switch (bucket)
            {
                case Bucket.Active: t.Active += seconds; t.PendingActive += seconds; break;
                case Bucket.Afk: t.Afk += seconds; t.PendingAfk += seconds; break;
                case Bucket.Spectating: t.Spectating += seconds; t.PendingSpectating += seconds; break;
                default: return;
            }

            _book.MarkDirty(id);
        }

        // ---- flushing --------------------------------------------------------------------
        //
        // Why a failed or lost write cannot double-count or drop seconds (the full argument is on
        // FlushBook): seconds leave the pending fields inside a named Batch, the Batch stays in the
        // book until the worker confirms it, and every re-send (a retry, or the shutdown flush)
        // repeats the same id and content. The journal may therefore see a line twice; the reader
        // counts it once. The worker is never trusted to finish: at shutdown it is a background
        // thread that dies with the process and nobody drains its callbacks, so the main thread
        // writes whatever is unconfirmed itself.

        /// <summary>Periodic SlClock job (main thread). Packs plain numbers, the worker does the IO.</summary>
        public void FlushAsync()
        {
            Batch[] due = _book.CollectDue(); // earlier failures again, unchanged, then the new seconds
            if (due.Length == 0)
                return;

            bool queued = _worker.TryRun(
                () => { _store.Append(due); return due.Length; },   // worker: plain data only
                written => _book.Settled(due),                       // main thread: confirmed
                error =>                                             // main thread
                {
                    _book.Failed(due); // same batches, same ids: the next tick sends them again
                    RateLimitedLog.Warn("playtime.flush", () => "journal write failed, will retry: " + error.Message);
                });

            if (!queued)
                _book.Failed(due); // worker queue full: nothing was written, retried on the next flush

            if (_book.DroppedBatches > 0)
                RateLimitedLog.Error("playtime.dropped", () => "journal has been failing for hours, " + _book.DroppedBatches + " batch(es) of seconds were dropped");
        }

        /// <summary>ServerEvents.Shutdown: the last call, so write on this thread, bounded, and do not trust the worker.</summary>
        public void FlushFinal()
        {
            long now = StopwatchTime.Now;
            int period = _periodSeconds;
            foreach (KeyValuePair<string, HubState> kv in _round.Hubs)
                CreditOpen(kv.Key, kv.Value, now, period);

            // Everything not confirmed: failed batches, batches still on the worker (it may have
            // written them already, which the batch id makes harmless) and the newest seconds.
            // The count is bounded by FlushBook.MaxRetained plus the worker queue capacity.
            List<Batch> all = _book.CollectForShutdown();
            if (all.Count > 0)
            {
                try
                {
                    if (!_store.TryAppend(all, ShutdownLockWaitMs))
                        Logger.Error("playtime: the journal lock was not free within " + ShutdownLockWaitMs + " ms, " + all.Count + " batch(es) of seconds were not written");
                }
                catch (Exception e)
                {
                    Logger.Error("playtime: final journal write failed: " + e.Message);
                }
            }

            if (DroppedSeconds > 0)
                Logger.Info("playtime: " + DroppedSeconds.ToString("F0") + " s of frozen time was not credited");
        }
    }
}
