# Round flow: lobby, start, end, restart, waves, and their traps

Verified against LabAPI 1.1.7 / SCP:SL 14.2.7 (2026-10). Part of scpsl-game-lifecycle.

Most traps here are derived from code. They are marked **(derived)**. Settle the ones you depend on with a short probe plugin before building on them.

## Lobby and round start

- The host starts a 1 Hz loop. The player count is ready-clients plus host plus dummies (the dedicated server itself is excluded). The countdown only runs with more than one counted player and while the lobby is not locked; reaching the reserved-slot maximum forces a start. `lobby_waiting_time` is read each round (default 20).
- When the timer reaches -1 the game calls its force-start routine: `RoundStarting` (cancellable), timer set to -1, the round-length stopwatch starts. After a final one-second yield the host's round-started flag is set. `Round.IsRoundStarted` reads that flag, so it turns true **up to 1 s after the timer reached -1**. A different, earlier flag (`timer == -1`) exists inside the game.
- Then `RoundStarted` is raised and the round-started RPC is sent. The host's own client receives it through a loopback queue flushed later in the same frame, and that triggers role assignment (reads `team_respawn_queue` and `allow_scp_overflow` each round, spawns SCPs, then humans). **The `RoundStarted` event therefore fires before any role exists (derived).** If the RPC were queued a frame later the conclusion would hold and only the delay would change.

### Trap: cancelling `RoundStarting` does not hold the lobby (derived)

Inside the lobby loop, a cancelled force-start leaves the timer unset, so the loop writes -1 itself and exits after the same one-second yield as an uncancelled start. The start is neither held nor delayed, and the round-length stopwatch never starts, so `RoundStart.RoundLength` stays 0 for the whole round. Consumers include the warhead auto-detonate check, a cake item and the summary seconds (effect on them is code-read, not run). A cancel does hold when the start is forced from outside the loop (`Round.Start()`, a remote-admin command).

Fix: hold the lobby with `Round.IsLobbyLocked = true`, which gates the loop. It is static and survives restarts, so clear it yourself.

```csharp
// Hold the lobby until your own condition is met; release explicitly.
Round.IsLobbyLocked = true;
// ... later, from a handler or clock job:
Round.IsLobbyLocked = false;
```

## Round end

- `RoundSummary` is a scene object created per round. It resets per-round counters and reads `end_round_on_one_player` (the game keeps the round going on one non-dedicated hub when that key is false, which is the default behaviour `KeepRoundOnOne`).
- A coroutine ticks every 2.5 s. It skips when the round is locked, not in progress, less than 15 s since the coroutine started, or (with `KeepRoundOnOne`) fewer than two non-dedicated hubs. Otherwise it counts teams, sets `IsRoundEnded = teamsAlive <= 1`, and raises `RoundEndingConditionsCheck` (`CanEnd` pre-filled, last writer wins), then `RoundEnding` (cancellable), then `RoundEnded`, then waits `auto_round_restart_time` (clamped 5..1000 s, default 10) and calls the restart if the round is still flagged as ended.
- Because of the skip rule, **the round does not end when the last human leaves**: with the default setting an empty server keeps an in-progress round alive, and idle mode can start mid-round (see idle-config-dummies.md).

### Trap: `Round.End(force: true)` and the condition event (derived)

`Round.End(true)` only sets `IsRoundEnded = true`. That lets the next tick bypass the skip gate, but the tick then **overwrites** the flag with the recomputed team verdict. With two or more teams alive nothing ends. A `RoundEndingConditionsCheck` handler setting `CanEnd = true` is not enough alone either: the event is raised only when the gate passed, so while `Round.IsLocked`, the one-hub rule or the 15 s guard applies, the handler is never reached.

Fix: do both.

```csharp
static bool _forceEnd;

static void EndRoundNow()
{
    _forceEnd = true;
    Round.End(true);                       // opens the gate for the next tick
}

static void OnEndingConditions(RoundEndingConditionsCheckEventArgs ev)
{
    if (_forceEnd) ev.CanEnd = true;       // decides the verdict once the gate is open
}

static void OnWaitingForPlayers() { _forceEnd = false; }   // reset per round
```

## Round restart sequence

1. `ServerEvents.RoundRestarted` is raised first, before the "already restarting" guard, so a second initiation raises it again. Pooled objects (pickups, ragdolls) are returned.
2. If not already restarting: restarting flag set, connections delayed, fast-reload list cleared, idle paused.
3. With `enable_fast_round_restart` (default false): each non-dedicated, non-dummy user id goes into the fast-reload list and clients are told to reconnect fast. Otherwise a full restart message carries an offset (moving average of earlier restart durations).
4. The level change: `RoundRestart.OnRestartTriggered` (static), then either a **process exit** (if `restart_after_rounds` or a stop-next-round request applies, so statics die and no scene reload happens) or: dummies destroyed (their `Left` fires now, while the old world is alive), **forced `GC.Collect`** (blocking, scales with heap), round counter incremented, async scene load.
5. All clients are marked not ready; incoming client messages are not processed during the load. With fast restart the scene message is delayed by `fast_round_restart_delay` (3.2 s default).
6. When the load completes the old objects are destroyed (`Left` for every remaining non-host hub) and the new scene wakes, which repeats the boot-time sequence from map generation (timeline.md T2 on).
7. The restarting flag clears when the new host hub's `Inventory.Start` runs. Connections and idle are un-paused by the host's `CharacterClassManager.Start`.

Cost: the main-thread cost of a whole restart (GC, teardown, N prefab instantiations, N spawn bursts) was **not measured**. Map generation takes about 0.5 s (estimate).

Between steps 1 and 6 the old world is alive. Deaths and role changes still run. Do not clear state on `RoundRestarted` and then assume nothing else will happen; the first handler to see an event from the old world after your reset has stale data.

Without fast restart (verified):

- The server console runs nothing while the scene loads: input arrives after `Waiting for players`. A console command therefore cannot observe the window between `RoundRestarted` and the next lobby; prove "nothing happened in the window" with counters read before and after.
- Clients are disconnected at the restart (`Player ... disconnected`) and reconnect by themselves (incoming connections are delayed 5 s) with new player ids. Anything sent to a player right after the lobby reaches nobody until the reconnect, and state keyed by player id is stale.

## Waves

- The wave manager runs per frame only on the server once the map is generated. Wave timers count only while the round is in progress and reset on every map generation. Initial intervals: main waves about 330 s, mini-waves about 150 s, plus 10 s per spawned player; a warhead detonation sets 210 s (estimates read from defaults).
- Flow: `WaveTeamSelecting` (cancellable, `Wave` settable) then `WaveTeamSelected`, then the manager waits for eligible Spectators and the wave animation, then spawns: the wave's timers reset, `WaveRespawning` (cancellable, `Roles` editable), roles set per player, announcement, `WaveRespawned`, then the respawn token is decremented.
- Max wave size is `ceil(AllHubs.Count * size_percentage)` with `size_percentage` defaulting to 0.65. The host, dummies and unauthenticated hubs inflate it.
- Tokens, previous team, wave config refresh and "respawns left" reset on `OnClientReady`.

### Trap: cancelling `WaveRespawning` still costs (derived)

The wave's timers reset and the token is decremented unconditionally, even when the spawn list ends up empty. The wave is consumed and nobody arrives.

Cancelling `WaveTeamSelecting` instead has no token or timer cost, but the wave stays ready: the event is raised again **every frame** while that wave's timer is ready, and the first ready wave starves the others. A handler there must be cheap and consistent. If you need to restore state after cancelling `WaveRespawning`, restore tokens and timers yourself.

`WaveRespawning.Roles` is a pooled dictionary that the game recycles after the raise. Copy what you need inside the handler.

## Re-verifying on a new build

- Read the lobby coroutine and the force-start routine: does a cancelled `RoundStarting` still fall through? A direct probe settles it: cancel in a handler, log `RoundStart.RoundLength` and timer values for 10 s.
- Read the round-summary coroutine: the skip gate, the verdict overwrite and where `RoundEndingConditionsCheck` is raised relative to the gate. Probe: `Round.End(true)` with two teams alive, with and without a `CanEnd` handler, with `Round.IsLocked` on and off.
- Read `RoundRestart.InitiateRoundRestart`/`ChangeLevel` for the order of the event, guard and `OnRestartTriggered`, and the `softrestart` command for what it calls.
- Read the wave manager `Update` and spawn path for the token decrement and the cancel returns. Probe: cancel `WaveRespawning` and log tokens before and after.
- Compare the default values quoted here against the game's config template.
