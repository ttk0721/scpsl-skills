# Timeline: what is valid when

Verified against LabAPI 1.1.7 / SCP:SL 14.2.7 (2026-10). Part of scpsl-game-lifecycle.

"Derived" means traced from code and not observed on a running server. "Unverified" is stated where it could not be confirmed. Durations are estimates measured with a heavy plugin set, not guarantees.

## One table

| Moment | What happens | Valid to use | Not yet, or already gone |
|---|---|---|---|
| **T0 process boot** | The LabAPI loader runs inside the first scene's `Awake`: LabAPI config, wrapper init, `labapi` commands, dependencies, plugin DLLs, then every plugin's `LoadConfigs`/`RegisterCommands`/`Enable`, then `ServerEvents.PluginsEnabled`. Once per process. | `ConfigFile.ServerConfig` (already loaded), `Server.Port`, your own config, Harmony patching, static event subscriptions | `Player.List` empty, `Server.Host` null, no network, no `Round.*`, no rooms. The default permission provider is registered only AFTER all plugins enabled. A `GameObject` you create here lives in a scene that is later unloaded (Unity behaviour inferred, not observed) |
| **T1 match creation** (menu scene) | Game config reloaded a second time, slots, ports, remote-admin permissions set up, connections delayed, idle paused | `Server.MaxPlayers`, permissions | No map, no hubs, listener not up |
| **T2 Facility activation** | `MapGenerating` (cancel or change seed), synchronous generation on the main thread, `MapGenerated`, then registration stages | Room GameObjects exist at `MapGenerated` | At `MapGenerated`: rooms-by-coordinates and doors-by-room are still empty (they fill in later stages). Items, lockers and generator contents are placed seconds later |
| **T3 host hub created** | First `OnClientReady` (before the host hub exists) | nothing of yours | `Server.Host` not set yet |
| **T4 host hub `Start`** | Connections un-delayed, idle un-paused, vanilla `ServerWaitingForPlayers` subscribers, then **`ServerEvents.WaitingForPlayers`**. Fires EVERY round. | The new world: map registries, host wrapper, `Round.IsRoundStarted == false` | Last round's `Player`, hub, door, room references. Items and lockers. Relative order of the host's `Start` callbacks is unverified |
| **T5 lobby** | Players connect and authenticate. A 1 Hz loop counts down `lobby_waiting_time` | `PlayerEvents.Joined`, `Left`, `Player.ReadyList` | Lobby players hold role `None`: no `Spawned`, no `ChangedRole` for them |
| **T6 round start requested** | `RoundStarting` (cancellable), timer set to -1, then up to 1 s later `Round.IsRoundStarted` turns true | | Cancelling does not hold the lobby (see round-flow.md) |
| **T7 `RoundStarted`** | Event raised first. Role assignment runs later in the SAME frame: per player `ChangingRole`, `Spawning`, `Spawned`, `ReceivingLoadout`, `ReceivedLoadout`, `ChangedRole` | In `ChangedRole`: role, position, inventory | In the `RoundStarted` handler every role is still `None` (derived) |
| **T8 in round** | Deaths, waves, late joins, leaves | Everything | A late joiner's `ChangedRole`/`Spawned` precede `Joined`; their nickname is still unset there |
| **T9 round end** | Condition check every 2.5 s, `RoundEndingConditionsCheck`, `RoundEnding`, `RoundEnded`, then `auto_round_restart_time` | `Round.IsRoundEnded` | A forced end with two or more teams alive |
| **T10 restart initiated** | `RoundRestarted` (can fire twice), pools returned, connections delayed, idle paused, `RoundRestart.OnRestartTriggered`, dummies destroyed (their `Left` fires here), forced `GC.Collect`, async scene load | The OLD world is still alive for the load duration: deaths and role changes still run | Do not treat it as "everything is already dead". `softrestart` raises neither `RoundRestarted` nor sets `IsRoundRestarting`: it is not a scene restart but a process exit (see Process exit), so an open round sees no `RoundEnded` or `RoundRestarted` |
| **T11 scene swap** | Old objects destroyed: `Left` for every remaining non-host hub (dummies already left at T10), wrappers removed, ids freed. Then T2 again | | Every cached hub, `Player`, `GameObject`; `Server.Host` is replaced |
| **Process exit** | `restart_after_rounds`, a stop-next-round request or `softrestart` (aliases `sr`, `srestart`) exits the process; `RoundRestart.OnRestartTriggered`, then `ServerEvents.Shutdown` fires. After `softrestart` LocalAdmin starts a new process, plugins load from disk again and players are told to reconnect | A synchronous flush; players stay connected roughly 0.6-2 s | No scene reload: statics die with the process, so open sessions never get a later `left` and must be closed in the flush. A crash or SIGKILL fires nothing |

## Boot order in detail

1. Process arguments are parsed, `config_gameplay` is loaded on first touch of the config class.
2. The first scene's `Awake` sets tickrate 60, requires a port argument, runs the whole LabAPI loader synchronously on the main thread, then hooks scene loading. Slow or throwing work in `Enable()` therefore delays or breaks server start. An exception that escapes the loader itself (not the per-plugin catch) would skip the scene-load hook, so no match would be created (derived from code, never triggered).
3. A menu scene load triggers match creation. It reloads the game config (second time), sets slots, reserved slots, `disconnect_drop`, transport limits and remote-admin permissions, and starts the lobby coroutine.
4. The lobby coroutine optionally resolves the external IP (online mode with `server_ip: auto`; on failure it retries every 180 s and boot stalls there), waits for the central public key, starts the Mirror host, waits for the `Facility` scene, and registers on the public list.
5. Facility activation generates the map inside `SeedSynchronizer.Awake`, the host hub starts, connections open, `WaitingForPlayers` fires.

Estimates: first config line to "Waiting for players" about 6 s, of which Facility load plus first map generation about 2.8 s. Later rounds: map generation about 0.5 s (blocking the main thread). The window between "connections no longer delayed" and "Waiting for players" is an upper bound of about 0.4 s for vanilla subscribers plus handlers; it is not a handler measurement.

## Map generation timeline

`SeedSynchronizer` is a scene object, so it regenerates every round. In order:

1. Read `map_seed` (below 1 means random).
2. `ServerEvents.MapGenerating`: cancel, or change the seed. Cancelling means no facility.
3. Generate zones (an exception in one zone is swallowed), set up room connectors.
4. **`ServerEvents.MapGenerated`**: room GameObjects have run `Awake`. `SeedSynchronizer.MapGenerated` (the static bool) is still false.
5. Stages in order: room coordinate registration, parent-room registration (doors by room), relative waypoints, decorations, culling caches, spawnable structures, static batching.
6. `SeedSynchronizer.OnGenerationFinished` (static event): everything registered. LabAPI's own `Generator` cache hooks this for the same reason.
7. Items, lockers and generator contents are placed from `Update` after a configured delay (the prefab value is unverified). Some are held back until a door opens, an SCP-079 camera activates or the pocket is exited. `ItemSpawning`/`ItemSpawned` and pickup wrappers therefore appear seconds after `WaitingForPlayers`.

Wave timers reset when generation finishes, and the wave manager does not run until the map is generated.

## Re-verifying on a new build

- Grep the game's `ServerStatic.Awake` for the loader call and for what is hooked after it; confirm plugins still enable before the first scene finishes.
- Confirm `CharacterClassManager.Start` (host) still orders: connections un-delayed, idle un-paused, then `WaitingForPlayers`.
- Diff the `SeedSynchronizer` generation stages (`MapGenerationPhase` enum) against the list above; a new stage can move when rooms or doors become queryable.
- Probe on a live server: log the frame number and `Player.List.Count` inside `Enable`, `PluginsEnabled`, `MapGenerated`, `WaitingForPlayers`, `RoundStarted` (and the role of every player there), `ChangedRole`.
- Re-check the event catalog for new round or map events (`tools/catalog/labapi-events.json`).
