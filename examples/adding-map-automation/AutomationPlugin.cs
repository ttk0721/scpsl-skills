using System;
using System.Collections.Generic;
using Interactables.Interobjects.DoorUtils;
using LabApi.Events.Arguments.WarheadEvents;
using LabApi.Events.Handlers;
using LabApi.Features.Console;
using LabApi.Features.Wrappers;
using LabApi.Loader.Features.Plugins;
using RoundRestarting;
using SlKit;
using Color = UnityEngine.Color;

namespace Examples.AddingMapAutomation
{
    /// <summary>
    /// Round-scoped facility scripting: sealed doors, a tesla grace period, parked elevators, a red
    /// warhead alert and a few static room markers. The shape matters more than the features:
    ///   WaitingForPlayers  resolve the map once (retry while it is still registering), spawn toys
    ///   RoundStarted       apply overrides, schedule their undo on the round clock
    ///   round end/restart  drop every reference; the scene is destroyed, never "clean up" per object
    /// Everything runs on the main thread. Nothing here runs per frame: the only recurring job is
    /// the spawn drain, and it removes itself when the queue is empty.
    /// </summary>
    public sealed class AutomationPlugin : Plugin<AutomationConfig>
    {
        private const int MaxIndexAttempts = 10;
        private static readonly TimeSpan IndexRetryDelay = TimeSpan.FromMilliseconds(100);
        private static readonly TimeSpan SpawnTick = TimeSpan.FromMilliseconds(100);

        // Red alert tint. Color.clear is the LabAPI value that removes the override.
        private static readonly Color AlertColor = new Color(1f, 0.15f, 0.1f, 1f);

        private bool _subscribed;

        // Per-round state. Plain references that die with the round; replaced, never reused.
        private MapIndex _map;
        private ToySpawner _spawner;
        private IDisposable _spawnJob;
        private readonly List<IDisposable> _jobs = new List<IDisposable>(8);

        public override string Name => "AddingMapAutomation";
        public override string Description => "Example: round-scoped doors, tesla, elevators, lights and static toys.";
        public override string Author => "scpsl-skills";
        public override Version Version => new Version(1, 0, 0);

        // A constant, never LabApiProperties.CompiledVersion (that one cannot fail the check).
        public override Version RequiredApiVersion => new Version(1, 1, 7);

        public override void Enable()
        {
            if (_subscribed)
                return; // idempotent: an EXILED-style reload must not stack handlers

            try
            {
                SlClock.Install(); // also ends ClockScope.Round jobs at ServerEvents.RoundRestarted

                // Subscriptions last. Every event here is once-per-round or rarer, none is hot.
                ServerEvents.WaitingForPlayers += OnWaitingForPlayers;
                ServerEvents.RoundStarted += OnRoundStarted;
                ServerEvents.RoundRestarted += ResetRound;
                RoundRestart.OnRestartTriggered += ResetRound; // `softrestart` raises no RoundRestarted
                WarheadEvents.Started += OnWarheadStarted;
                WarheadEvents.Stopped += OnWarheadStopped;
                ServerEvents.Shutdown += OnShutdown;
                _subscribed = true;
            }
            catch
            {
                Unsubscribe();
                throw; // swallowing would let the loader print "enabled" for a dead plugin
            }
        }

        // Never called by LabAPI or the game. Kept as the same cleanup for hosts that do call it.
        public override void Disable() => Unsubscribe();

        // ---- lifecycle ----------------------------------------------------------------------

        private void OnWaitingForPlayers()
        {
            ResetRound(); // idempotent: also covers a restart path that raised neither reset event

            AutomationConfig cfg = Config;
            if (cfg == null || !cfg.IsEnabled)
                return;

            TryBuildMap(0);
        }

        private void TryBuildMap(int attempt)
        {
            // Read Config again: this can run after a reload, and the kill switch must win.
            AutomationConfig cfg = Config;
            if (cfg == null || !cfg.IsEnabled)
                return;

            try
            {
                MapIndex map = MapIndex.Build(cfg, out string missing);
                if (missing != null && attempt < MaxIndexAttempts)
                {
                    // Not registered yet (Start-time registration). Wait a frame or so and rebuild
                    // from scratch rather than patching a half-filled index.
                    _jobs.Add(SlClock.After(IndexRetryDelay, () => TryBuildMap(attempt + 1), "automation.index-retry"));
                    return;
                }

                if (missing != null)
                    Logger.Warn("AutomationPlugin: map still incomplete after " + attempt + " retries, continuing without: " + missing);

                _map = map;
                StartSpawning(cfg, map);
            }
            catch (Exception ex)
            {
                // A throw here would leave _map null: fail closed (no automation this round), loudly.
                Logger.Error("AutomationPlugin: building the map index failed, automation is off for this round: " + ex);
            }
        }

        private void StartSpawning(AutomationConfig cfg, MapIndex map)
        {
            if (!cfg.RoomMarkers || map.MarkerRooms.Count == 0)
                return;

            // Lobby time is the cheap time to spawn: joiners receive the objects inside their normal
            // join burst, instead of a mid-round spike on everybody.
            _spawner = new ToySpawner(map.MarkerRooms);
            _spawnJob = SlClock.Every(SpawnTick, DrainSpawns, "automation.toys", ClockScope.Round);
        }

        private void DrainSpawns()
        {
            AutomationConfig cfg = Config;
            ToySpawner spawner = _spawner;
            MapIndex map = _map;
            if (cfg == null || !cfg.IsEnabled || spawner == null || map == null)
            {
                StopSpawnJob();
                return;
            }

            // Player.Count is a LINQ count: read it once per tick, not per toy.
            map.SpawnedToys += spawner.Tick(cfg.SpawnBytesPerTick, Player.Count);
            if (spawner.Pending == 0)
                StopSpawnJob(); // no idle job after the queue is empty
        }

        private void StopSpawnJob()
        {
            _spawnJob?.Dispose();
            _spawnJob = null;
            _spawner = null;
        }

        private void OnRoundStarted()
        {
            AutomationConfig cfg = Config;
            MapIndex map = _map;
            if (cfg == null || !cfg.IsEnabled || map == null)
                return;

            if (cfg.LockDoors && map.DoorsToLock.Count > 0)
            {
                LockDoors(map);
                _jobs.Add(SlClock.After(TimeSpan.FromSeconds(cfg.DoorLockSeconds), UnlockDoors, "automation.unlock-doors"));
            }

            if (cfg.DisableTesla && map.Teslas.Count > 0)
            {
                SetTeslaInactive(map, float.MaxValue);
                _jobs.Add(SlClock.After(TimeSpan.FromSeconds(cfg.TeslaOffSeconds), EnableTesla, "automation.enable-tesla"));
            }

            if (cfg.ParkElevators)
                ParkElevators(cfg, map);
        }

        // Round boundary: drop everything. Used for RoundRestarted, OnRestartTriggered, the next
        // WaitingForPlayers and Shutdown, so it must be safe to call any number of times.
        private void ResetRound()
        {
            foreach (IDisposable job in _jobs)
                job.Dispose();
            _jobs.Clear();
            StopSpawnJob();

            MapIndex map = _map;
            _map = null;
            if (map != null)
            {
                // The scene reload destroys the toys and doors with it. Destroying 500 toys one by one
                // here would only send destroy messages to clients that are about to reconnect.
                Logger.Info("AutomationPlugin: round state dropped (toys " + map.SpawnedToys + ", doors locked " + map.LockedByUs.Count + ").");
            }
        }

        private void OnShutdown()
        {
            ResetRound();
            Unsubscribe();
        }

        private void Unsubscribe()
        {
            if (!_subscribed)
                return;
            ServerEvents.WaitingForPlayers -= OnWaitingForPlayers;
            ServerEvents.RoundStarted -= OnRoundStarted;
            ServerEvents.RoundRestarted -= ResetRound;
            RoundRestart.OnRestartTriggered -= ResetRound;
            WarheadEvents.Started -= OnWarheadStarted;
            WarheadEvents.Stopped -= OnWarheadStopped;
            ServerEvents.Shutdown -= OnShutdown;
            _subscribed = false;
        }

        // ---- doors --------------------------------------------------------------------------

        private static void LockDoors(MapIndex map)
        {
            foreach (Door door in map.DoorsToLock)
            {
                if (door.IsDestroyed)
                    continue;
                // The bit RA and other plugins also use. There is no free bit to own: an unknown
                // DoorLockReason value means "no lock" to the game.
                door.Lock(DoorLockReason.AdminCommand, true);
                map.LockedByUs.Add(door);
            }
        }

        private void UnlockDoors()
        {
            MapIndex map = _map;
            if (map == null)
                return;
            foreach (Door door in map.LockedByUs)
            {
                // Clear only the one bit we set. `door.IsLocked = false` would do the same, but a
                // door held shut by the warhead or decontamination must stay that way: never
                // clear the game's own bits.
                if (!door.IsDestroyed)
                    door.Lock(DoorLockReason.AdminCommand, false);
            }
            map.LockedByUs.Clear();
        }

        // ---- tesla --------------------------------------------------------------------------

        private static void SetTeslaInactive(MapIndex map, float seconds)
        {
            foreach (Tesla tesla in map.Teslas)
            {
                if (tesla.Base != null)
                    tesla.InactiveTime = seconds;
            }
        }

        // float.MaxValue is the cheap permanent disable: the per-tick decrement rounds away, so
        // the SyncVar is written once and never again. A finite value counts down and sends
        // traffic every tick, so we set the huge value now and 0 later instead of "60".
        private void EnableTesla()
        {
            MapIndex map = _map;
            if (map != null)
                SetTeslaInactive(map, 0f);
        }

        // ---- elevators ----------------------------------------------------------------------

        private static void ParkElevators(AutomationConfig cfg, MapIndex map)
        {
            foreach (Elevator elevator in map.Elevators)
            {
                if (elevator.CurrentDestinationLevel == cfg.ParkLevel)
                    continue; // same-level calls are no-ops anyway
                // The second argument is NOT "force": LabAPI forwards it as the game's
                // allowQueueing. true = queue behind a running trip, false = write the destination
                // now, which restarts a moving cabin from its last arrived door and makes it jump.
                elevator.SetDestination(cfg.ParkLevel, true);
            }
        }

        // ---- lights -------------------------------------------------------------------------

        private void OnWarheadStarted(WarheadStartedEventArgs ev) => TintLights(AlertColor);

        private void OnWarheadStopped(WarheadStoppedEventArgs ev) => TintLights(Color.clear);

        private void TintLights(Color color)
        {
            AutomationConfig cfg = Config;
            MapIndex map = _map;
            if (cfg == null || !cfg.IsEnabled || !cfg.WarheadAlertLights || map == null)
                return;

            // One SyncVar write per controller, equality-suppressed, twice per round at most.
            // The colour override is independent of the on/off flicker timers that 079 and
            // other effects overwrite, which is why it is used instead of a blackout.
            foreach (List<LightsController> controllers in map.LightsByZone.Values)
            {
                foreach (LightsController controller in controllers)
                {
                    if (controller.Base != null)
                        controller.OverrideLightsColor = color;
                }
            }
        }
    }
}
