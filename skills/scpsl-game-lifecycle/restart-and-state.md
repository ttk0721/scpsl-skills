# What dies and what survives a round restart

Verified against LabAPI 1.1.7 / SCP:SL 14.2.7 (2026-10). Part of scpsl-game-lifecycle.

A round restart is a **non-additive scene reload**. The process, the network manager object and the plugin assemblies stay. Everything that lives in the scene is destroyed and rebuilt.

## Dies every restart

| Item | Consequence for plugins |
|---|---|
| Every `ReferenceHub`, the host's included | `Left` fires for each non-host hub when the old scene is destroyed (dummies earlier, when the level change destroys them) |
| `Player` wrappers and their `CustomDataStore` | A `CustomDataStore` lives exactly as long as the hub, so it is also lost on every disconnect and rejoin within a round |
| `Server.Host` | Nulled, then set to the new host wrapper |
| Rooms, doors, lockers, generators, pickups, ragdolls, any scene object | Cached `Room`/`Door`/`Pickup` wrappers are dead |
| Round-summary and round-start singletons, `SeedSynchronizer` | New seed, `MapGenerated` goes false then true |
| `PlayerId` and per-hub identities | Ids are freed and reallocated; see players.md |
| A `GameObject` you created in the boot scene | Unloaded with that scene (inferred from Unity single-mode loading, not observed). LabAPI itself never marks anything `DontDestroyOnLoad` |

## Survives

| Item | Consequence |
|---|---|
| All plugin assemblies, **static fields**, **static event subscriptions**, Harmony patches | Handlers keep running with stale references unless you reset them |
| MEC `Timing.Instance` (it is marked `DontDestroyOnLoad`) | **Coroutines keep running across restarts** and touch destroyed hubs. Kill by handle, or `CancelWith(gameObject)` |
| `Application.targetFrameRate`, `Time.timeScale`, idle-mode statics | An override you set is not reset by a restart (but see idle-config-dummies.md) |
| `Round.IsLocked`, `Round.IsLobbyLocked` | Survive, so a lock you set stays set until you clear it |
| The per-user recent-role history used by the human spawner | Never cleared in-process |
| Wave list and timer objects | Their values reset on map generation |
| Network-layer tables, the netId counter | `netId` only grows within a process |
| PlayerLoop edits (so SlKit's clock entry) | Survive scene loads (standard Unity behaviour, not checked in the game's code) |

## Reset by vanilla through `CustomNetworkManager.OnClientReady`

Wave tokens, previous-team record, the "already spawned" set used for late join, queued roles, wave config refresh, item and ability caches, many handler registrations. This is how the game handles its own statics: roughly 85 subscription sites reset on that public static event. Your statics are not on that list.

`OnClientReady` is raised **at least twice per round** on the server (once from the client scene change before the host hub exists, once from the host's `Inventory.Start`, and once more on first start). A handler must be idempotent.

## Which event do I reset on

| Need | Use | Why |
|---|---|---|
| Rebuild per-round state (caches, rooms, doors, wave data) | `ServerEvents.WaitingForPlayers` | The new world and the host wrapper exist. It fires every round, so build idempotently and subscribe nothing twice |
| Stop old-round timers and coroutines | `ServerEvents.RoundRestarted` **and** `RoundRestart.OnRestartTriggered` | `RoundRestarted` is raised BEFORE the game's re-entrancy guard, so a second initiation raises it again. `softrestart` calls the level change directly and raises no `RoundRestarted` at all; only `OnRestartTriggered` (a static event, once per real level change) sees it |
| Per-player cleanup | `PlayerEvents.Left` | The hub is still in the hub list and the wrapper is alive, but the connection is gone. Also fires for unauthenticated hubs and dummies |
| Run before the host hub's `Start` | `CustomNetworkManager.OnClientReady` | Fires twice; avoid unless necessary |
| Detect "teardown in progress" | `RoundRestart.IsRoundRestarting` | Set when a restart starts, cleared when the new host hub's `Inventory.Start` runs. The game itself uses it to skip loadouts and item removal during teardown. Not set on the `softrestart` path |

Not every `Left` is a real leave: at a restart, `Left` fires for every non-host hub (see players.md).

## Patterns

State container that is dropped, not cleaned:

```csharp
// One object per round. Replace the reference; never reuse entries.
sealed class RoundState
{
    public readonly Dictionary<string, PlayerStat> ByUserId = new Dictionary<string, PlayerStat>(64);
    public bool Active;
}

static RoundState _round = new RoundState();

static void OnWaitingForPlayers()   // ServerEvents.WaitingForPlayers
{
    _round = new RoundState();      // idempotent: a second call just replaces it
}

static void OnRestartStarted()      // wire to RoundRestarted AND RoundRestart.OnRestartTriggered
{
    _round.Active = false;          // idempotent: both may fire, RoundRestarted may fire twice
}
```

Subscribing to a static event that points at a per-round object (a door, a room, a wave timer): unsubscribe in `OnDestroy`, or in `WaitingForPlayers`/`RoundRestarted`. The scene object dies, the delegate does not, and the handler count grows each round. Vanilla does exactly this for its own distributors.

With SlKit: `SlClock.After(...)` defaults to `ClockScope.Round`, and `SlClock.Install()` (which every registration runs) wires `RoundRestarted` so `Round` jobs end there, with no extra call. It inherits the `RoundRestarted` limitation above, so it will not see a `softrestart`; `ClockScope.Server` jobs survive restarts.

## Identity across rounds

Key persistent data by `UserId`, and only for authenticated online-mode players. Offline mode gives a per-connection id of the form `ID_Offline_<netId>_<timestamp>`, every dummy shares `ID_Dummy`, and unauthenticated hubs have none. Neither `PlayerId` nor `netId` is an identity: `PlayerId` comes from a FIFO free list that reuses ids once at least 16 are free, and every hub rebuild frees and reallocates them. At a restart all ids are freed at once, so recycling is likely.

## Re-verifying on a new build

- Grep the game for `DontDestroyOnLoad` to see whether the network manager object and MEC are still persistent; check the MEC controller creation too.
- Grep for `OnClientReady +=` and re-count the subscription sites; confirm it is still raised from both the scene-change callback and the host `Inventory.Start`.
- Read `RoundRestart.InitiateRoundRestart` and `ChangeLevel`: is `RoundRestarted` still raised before the guard, and what does `softrestart` call?
- Probe: a plugin with a static counter, a MEC coroutine that logs a tick, and a `Joined`/`Left` logger; run a full restart with two clients connected and check what survived (counter, coroutine) and what `Left` reports.
