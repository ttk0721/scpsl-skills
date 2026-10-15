# Idle mode, tick rate, config keys, dummies

Verified against LabAPI 1.1.7 / SCP:SL 14.2.7 (2026-10). Part of scpsl-game-lifecycle.

## Idle mode

State: enabled flag (`idle_mode_enabled`, default true; a server may set false), a pause flag (starts true), and an active flag. Entering idle sets `Application.targetFrameRate` to `idle_mode_tickrate` (1), `Time.timeScale` to 0.01 and scales the fixed delta time. Leaving restores the configured tickrate and timescale 1.

**Pause points.** Set true at match creation, round-restart initiation, level change, shutdown, reconnect and redirect. Set **false only by the host's `CharacterClassManager.Start`**, immediately before `WaitingForPlayers`. Pausing also forces idle off. While paused, nothing (including the player-removed handler during scene teardown) can enter idle. That protects restarts.

**Enter** when any of:
- Fixed-update check: `idle_mode_time` (5000 ms default) passed, and the preauth stopwatch (restarted by the un-pause) passed `idle_mode_preauth_time` (30000 ms default), and the hub count is at most one. Un-pause to "entered idle mode" takes about 30 s.
- A player is removed and the hub count is at most one.
- A preauth was rejected while no peers are connected.

**Exit** on any new hub, on any accepted preauth step (dispatched to the main thread; about 0.14 s later) and on any delayed-connection reject.

**The hub count includes the host.** A single dummy or NPC hub keeps the server out of idle for good.

**Idle during a round.** The round-end check needs two non-dedicated hubs while `KeepRoundOnOne` is on (the default). If the last player leaves mid-round, the round does not end, and idle mode can engage while it is still "in progress". Round timers on `Time.deltaTime` freeze, and MEC `Update`, `FixedUpdate` and `LateUpdate` segments run on scaled time, so their waits stretch by about 100x. `SlowUpdate` and `RealtimeUpdate` use real time. Treat `Server.IdleModeActive` as "no data" for anything measuring; whether `Server.Tps` reads near 1 or near 100 in idle is **unverified**. See scpsl-main-thread-and-timing and measuring-scpsl-performance.

**Plugin overrides.**
- `Server.PauseIdleMode`: the game un-pauses it every round, so re-apply it in `WaitingForPlayers`.
- `Server.IdleModeAvailable` and `Server.MaxTps` write the statics that every config reload refills from the file, so your value is lost on `reload`. They are not touched by a restart, so re-apply after a reload (hook `ConfigFile.OnConfigReloaded`, a public static action), not per round.
- Do not set `Server.MaxTps` while idle is active: the setter writes the frame rate even in idle, giving a high frame rate at timescale 0.01. A value written straight to `Application.targetFrameRate` is only safe if it equals the configured server tickrate, since leaving idle re-applies the configured value. The setter also logs a line per call, so never call it per frame.

## Tick rate

`server_tickrate` is applied from every config reload. At boot the order is: config load (60), match start, then Mirror's host setup overwrites the frame rate with Mirror's send rate (60). So **a lower `server_tickrate` is inert** until the first idle exit or a config reload. Set a custom cap in the first `WaitingForPlayers` and after reloads. The Mirror manager's own `Update` is shadowed by an empty method, so `NetworkServer.tickRate` stays 60. Position send rate is `1 / clamp(ServerTickrate, 10, 60)`.

Compare frame-time against `Server.MaxTps` (or a ratio), not a constant: a server configured below 60 would otherwise look permanently degraded. See scpsl-engine-cost-model.

## Config keys: when each applies

All come from the server's `config_gameplay` in the port folder. `ReloadGameConfigs` (remote-admin `reload`, console `reload`, and boot) re-parses it. Code that calls the getter at use time sees new values at once.

| Class | Keys | Rule |
|---|---|---|
| **Applied into statics on every reload** | `server_tickrate`, `idle_mode_enabled/time/preauth_time/tickrate`, `enable_fast_round_restart`, `fast_round_restart_delay`, `online_mode`, `authentication_timeout`, `same_account_joining`, `cuffed_escapee_change_team`, geoblocking, preauth and rate-limit options, friendly-fire detector, warhead/decon broadcast texts, wave `*_enabled` and `size_percentage`, `use_reserved_slots`, whitelist, bans and reserved-slot lists | Hot-reloadable. Your overrides of the same statics are overwritten |
| **Read once at match creation** | `max_players`, `reserved_slots`, `forward_ports`, `disconnect_drop`, `enable_query`, `use_global_bans`, `config_remoteadmin`, bind IPs, `use_native_sockets`, `server_ip` | Restart the process. LabAPI's `Server.MaxPlayers`/`ReservedSlots` setters write the live statics, but the transport connection limit stays |
| **Read per round or per event** | `lobby_waiting_time` (each lobby), `map_seed` (each map generation), `team_respawn_queue`, `allow_scp_overflow` (round start), `late_join_time` (each late joiner), `end_round_on_one_player` (round summary start), `auto_round_restart_time` (each round end), `restart_after_rounds`, `full_restart_rejoin_time` | Effective on the next occurrence, so effectively reloadable |
| **Cached in a `static readonly`** | `keep_items_after_escaping` (the only one identified) | Reload is useless; process lifetime. Check for other `static readonly ... = ConfigFile.ServerConfig.Get...` before promising reloadability |

For your **own** plugin config: `labapi reload configs` re-calls `LoadConfigs()`, which replaces the `Config` object. Read `Config.X` at use time, or override `LoadConfigs()` and rebuild caches there. See plugin-boot.md.

## Dummies

Created by the game's dummy utility (LabAPI 1.1.7 has no wrapper for it; EXILED's `Npc.Spawn` uses the same call) and destroyed by `DestroyAllDummies`.

- **Create:** instantiate the player prefab, set the nickname, attach a fake connection that is marked ready and observes every spawned identity. The hub comes back in `Awake` state.
- **Identity:** on the next frame the hub's `Start` sets the user id `ID_Dummy` and instance mode Dummy; the wrapper then exists, with `IsNpc`, `IsDummy`, membership in `ReadyList`, `NpcList` and `DummyList`. Until that `Start`, `IsDummy` is false and the role-init skips the dummy action providers. A role set before `Start` still sticks but loses them; one frame is enough, EXILED waits 0.5 s for its own health-stat swap.
- **No `Joined`** ever. `Left` fires when destroyed.
- **Counted as players** by the lobby countdown, role assignment (they get roles at round start if spawned in the lobby), the hub count (idle mode, wave size, round end), and position-sync receivers.
- **Mid-round dummies** take the late-join path and become Spectators with `late_join_time: 0`. All dummies share `ID_Dummy`, so only the first can ever pass the already-spawned set. Assign roles explicitly after their `Start`.
- **Destroyed at every restart**, so recreate them after each one.
- **Simulate:** a hub with a role, spawn and loadout logic, server-side movement driven by remote-admin dummy actions.
- **Do not simulate:** handshake and bans, sockets and batching, per-connection broadcast, client-side handlers, voice, latency, client scene loading, hints. Shared RPCs are packed once, so a dummy adds only a no-op send. **Per-receiver messages (position, role sync, spawn) are packed per dummy and then dropped**, which is real server CPU. A dummy load test therefore under-measures network, join and client cost, while the serialisation it triggers is real. Report that gap.
- **Rounds with dummies:** a one-team dummy roster ends the round shortly after the 15 s guard (exact figure unverified). For tests, set `Round.IsLocked = true` (static, survives restarts).
- **Never leave a permanent dummy** on a production server unless you intend to disable idle.

## Re-verifying on a new build

- Read the idle-mode class: pause points, enter and exit conditions, the hub-count test; and grep `PauseIdleMode` for new callers.
- Confirm the config getter set in `ReloadGameConfigs` and re-grep for `static readonly` config reads.
- Confirm the host setup still overwrites the frame rate from Mirror's send rate.
- Probe: log `Application.targetFrameRate`, `Time.timeScale`, `Server.IdleModeActive` at `WaitingForPlayers`, after an empty-server wait, after a join, and after `reload`.
- Spawn a dummy and log `IsDummy`, role and `ReadyList` membership on frames 0, 1, 2 and after 0.5 s.
