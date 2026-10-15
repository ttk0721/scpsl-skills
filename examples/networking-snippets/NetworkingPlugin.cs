using System;
using System.Collections.Generic;
using LabApi.Events.Arguments.PlayerEvents;
using LabApi.Events.Handlers;
using LabApi.Features.Wrappers;
using LabApi.Loader.Features.Plugins;
using SlKit;
using UnityEngine;

namespace Examples.NetworkingSnippets
{
    /// <summary>
    /// Wires the four network-cost patterns together so each can be exercised with the
    /// <c>netdemo</c> command on a test server:
    ///   ToySpawnQueue  byte-budgeted spawns, identity cap
    ///   ToyMover       thaw, move, freeze after the update was sent and applied
    ///   FakeScale      per-viewer scale through an existing message type
    ///   GhostGate      hide a player from other players, with the audio caveats
    /// One SlClock job pumps the queue and the mover; nothing runs per frame.
    /// </summary>
    public sealed class NetworkingPlugin : Plugin<NetworkingConfig>
    {
        private static readonly TimeSpan PumpPeriod = TimeSpan.FromMilliseconds(100);
        private static readonly TimeSpan ReassertPeriod = TimeSpan.FromSeconds(5);

        private static readonly Vector3[] GroupOffsets =
        {
            new Vector3(-1f, 0f, -1f), new Vector3(1f, 0f, -1f), new Vector3(-1f, 0f, 1f), new Vector3(1f, 0f, 1f),
        };

        private bool _subscribed;
        private readonly List<IDisposable> _jobs = new List<IDisposable>(2);

        // Per-round state. Replaced at WaitingForPlayers, never reused across rounds.
        private ToySpawnQueue _queue;
        private ToyMover _mover;
        private FakeScale _fakeScale;
        private GhostGate _ghosts;
        private readonly List<PrimitiveObjectToy> _discs = new List<PrimitiveObjectToy>(16);
        private ToyGroup _group;

        public override string Name => "NetworkingSnippets";
        public override string Description => "Example: cheap toy spawns, quiet moves, per-viewer scale, ghost players.";
        public override string Author => "scpsl-skills";
        public override Version Version => new Version(1, 0, 0);
        public override Version RequiredApiVersion => new Version(1, 1, 7);

        internal static NetworkingPlugin Instance { get; private set; }

        internal ToySpawnQueue Queue => _queue;
        internal ToyMover Mover => _mover;
        internal FakeScale FakeScale => _fakeScale;
        internal GhostGate Ghosts => _ghosts;
        internal ToyGroup Group => _group;
        internal IReadOnlyList<PrimitiveObjectToy> Discs => _discs;

        public override void Enable()
        {
            if (_subscribed)
                return; // idempotent

            try
            {
                Instance = this;
                SlClock.Install();
                BuildRoundState();

                // Per-lifecycle events only; the hot ones live inside GhostGate and exist only while a ghost does.
                ServerEvents.WaitingForPlayers += OnWaitingForPlayers;
                ServerEvents.Shutdown += OnShutdown;
                PlayerEvents.ChangedRole += OnChangedRole;
                PlayerEvents.Left += OnLeft;
                _subscribed = true;

                _jobs.Add(SlClock.Every(PumpPeriod, Pump, "networking.pump"));
                _jobs.Add(SlClock.Every(ReassertPeriod, Reassert, "networking.reassert"));
            }
            catch
            {
                Unsubscribe();
                throw;
            }
        }

        public override void Disable() => Unsubscribe();

        // ---- demo operations used by the command --------------------------------------------

        /// <summary>Queues up to <paramref name="count"/> static discs on a ring around a point. Returns how many were queued (the identity cap may stop it).</summary>
        internal int QueueDiscs(Vector3 center, int count)
        {
            int queued = 0;
            for (int i = 0; i < count; i++)
            {
                float angle = i * Mathf.PI * 2f / Math.Max(1, count);
                Vector3 position = center + new Vector3(Mathf.Cos(angle) * 3f, 0f, Mathf.Sin(angle) * 3f);
                if (!_queue.TryEnqueue(1, () => _discs.Add(
                        ToyFactory.SpawnStatic(position, new Vector3(0.6f, 0.05f, 0.6f), PrimitiveType.Cylinder, ToyFactory.Cyan))))
                    break;
                queued++;
            }
            return queued;
        }

        /// <summary>Queues one anchor with four static children. False when a group exists or the cap would be exceeded.</summary>
        internal bool QueueGroup(Vector3 position)
        {
            if (_group != null)
                return false;
            return _queue.TryEnqueue(1 + GroupOffsets.Length, () =>
                _group = ToyFactory.SpawnGroup(position, GroupOffsets, new Vector3(0.4f, 0.4f, 0.4f), ToyFactory.Amber));
        }

        // ---- lifecycle ----------------------------------------------------------------------

        private void BuildRoundState()
        {
            NetworkingConfig cfg = Config ?? new NetworkingConfig();

            _queue = new ToySpawnQueue(cfg.MaxToys);
            _mover = new ToyMover
            {
                SyncInterval = cfg.MoveSyncIntervalSeconds,
                Smoothing = cfg.MoveSmoothing,
                NetworkAllowance = cfg.MoveNetworkAllowanceSeconds,
            };
            _fakeScale = new FakeScale();
            _ghosts = new GhostGate(new GhostOptions
            {
                HideDelaySeconds = cfg.GhostHideDelaySeconds,
                VoiceLeadSeconds = cfg.GhostVoiceLeadSeconds,
                BlockShots = cfg.GhostBlockShots,
                MuteVoice = cfg.GhostMuteVoice,
                ExemptScps = cfg.GhostExemptScps,
            });
            _discs.Clear();
            _group = null;
        }

        private void OnWaitingForPlayers()
        {
            // The scene reload destroyed every toy and player: drop references, do not destroy one by one.
            _ghosts?.Dispose();
            BuildRoundState();
        }

        private void OnShutdown() => Unsubscribe();

        private void Unsubscribe()
        {
            _ghosts?.Dispose(); // drops the hot-event subscriptions and timers, also when the plugin is disabled at runtime
            foreach (IDisposable job in _jobs)
                job.Dispose();
            _jobs.Clear();

            if (_subscribed)
            {
                ServerEvents.WaitingForPlayers -= OnWaitingForPlayers;
                ServerEvents.Shutdown -= OnShutdown;
                PlayerEvents.ChangedRole -= OnChangedRole;
                PlayerEvents.Left -= OnLeft;
                _subscribed = false;
            }

            if (ReferenceEquals(Instance, this))
                Instance = null;
        }

        // ---- periodic work ------------------------------------------------------------------

        private void Pump()
        {
            _mover.Tick(); // always: a toy thawed before a config switch-off must still be frozen again

            NetworkingConfig cfg = Config;
            if (cfg == null || !cfg.IsEnabled)
                return;

            if (_queue.Pending > 0)
                _queue.Tick(cfg.SpawnBytesPerTick, Player.Count); // Player.Count is a LINQ count: once per tick, only when work exists
        }

        private void Reassert() => _fakeScale.Reassert();

        private void OnChangedRole(PlayerChangedRoleEventArgs ev) => _fakeScale.OnTargetRoleChanged(ev.Player);

        private void OnLeft(PlayerLeftEventArgs ev)
        {
            if (ev.Player != null)
                _fakeScale.Forget(ev.Player.PlayerId);
        }
    }
}
