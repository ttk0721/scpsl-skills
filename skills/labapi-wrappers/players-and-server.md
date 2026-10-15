# Player, Server and Round wrappers

Verified against LabAPI 1.1.7 / SCP:SL 14.2.7 (2026-10). Costs below are reasoning from the code, not measurements, unless stated. Anything marked UNVERIFIED was read but not observed in a running server.

## Lifetime and identity

- A `Player` is 1:1 with a `ReferenceHub`. It is created when the hub starts, or lazily by the first `Player.Get(hub)`, including for a hub that exists but has not started yet. It is removed from `Player.Dictionary` in the hub's destroy step, after `PlayerEvents.Left` has fired.
- The wrapper object outlives the hub. `IsDestroyed` is `!ReferenceHub`, a cheap managed check. After destruction `Rotation` throws a NullReferenceException and `IpAddress` can throw; fields that only read managed state (`UserId`, `Nickname`) probably still return old values (UNVERIFIED).
- `Left` is built with `Player.Get(hub)`, so the wrapper is alive and complete inside `Left`. `UserId` and `Nickname` are readable there. Read the IP at join, not in `Left`: the transport peer entry is removed before the deferred destroy runs `Left`, so `IpAddress` can throw `KeyNotFoundException` for a real client without an IP override (derived from callback order, not run).
- A round restart is a scene reload. Every hub, including host and dummies, is destroyed and recreated, and every old wrapper becomes `IsDestroyed`. See scpsl-game-lifecycle for what that does to your sessions.
- `Player` has no `Equals` override, so equality is reference equality. It is sound only because the dictionary keeps one wrapper per live hub. A stale wrapper and the new wrapper of a rejoined player are different objects with the same `UserId`.
- `PlayerId` is recycled. Freed ids are re-issued first-in-first-out only after 16 are queued, so with 17 or more hubs a restart frees all of them and the next round may hand the same numbers to other people. Key by `UserId`.
- `UserId` special values: host `ID_Dedicated`, every dummy `ID_Dummy`, offline-mode clients `ID_Offline_...`. Key NPCs by `NetworkId` and skip dummies in `UserId` maps.
- `Server.Host` is assigned while the host wrapper is created and can be null early. `Player.Get(ICommandSender)` returns null for the server console (its sender id is not a user id); use `Server.Host` or `sender is ServerConsoleSender`.

```csharp
// Keep keys, not wrappers.
readonly Dictionary<string, Session> _sessions = new();   // key: UserId

void OnJoined(PlayerJoinedEventArgs e) { _sessions[e.Player.UserId] = new Session(e.Player.UserId); }
void OnLeft(PlayerLeftEventArgs e)
{
    var id = e.Player.UserId;                 // may be null for a connection that never authenticated
    if (id != null) _sessions.Remove(id);
}
void Tick()
{
    foreach (var s in _sessions.Values)
    {
        var p = Player.Get(s.UserId);         // O(1) on a cache hit, null if gone
        if (p == null || p.IsDestroyed) continue;
        // use p here, do not store it
    }
}
```

## Collections

| Kind | `List` | `ReadyList`, `Count` | `NpcList` | `DummyList` | `UnauthenticatedList` |
|---|---|---|---|---|---|
| Dedicated host hub | yes | no | no | no | no |
| Authenticated player | yes | yes | no | no | no |
| Connection not yet authenticated | yes | no | no | no | yes |
| Dummy after its first frame | yes | yes | yes | yes | no |
| Dummy in the frame it was spawned | yes | no | yes | no | no (it is a "regular NPC") |

- `Player.List` is the live `Dictionary.Values` view: no copy, one boxed enumerator per `foreach`. Everything else (`ReadyList`, `NpcList`, `GetAll`, `Count`) is a lazy LINQ `Where` over it, so each call allocates an iterator, and `Count` re-runs the whole filter.
- There is no snapshot anywhere. Joins, leaves and lazy wrapper creation all mutate the dictionary and invalidate an open enumeration. A plain synchronous `foreach` is normally safe because the engine schedules hub start, destroy is deferred to end of frame, and `Kick` completes later. It is not safe across a `yield`, and it is not safe if the body does a lazy `Player.Get` on a hub that has no wrapper yet (for example a dummy you just spawned). Snapshot with `ToArray()` when the body can yield or create wrappers.
- `Count` and `Server.PlayerCount` include dummies, exclude the host and exclude connecting clients. For humans use `GetAll(PlayerSearchFlags.AuthenticatedPlayers)`. `Player.ConnectionsCount` counts transport peers.
- `PlayerSearchFlags` documentation mentions `Player.AuthenticatedList`. It does not exist; the equivalent is `ReadyList`.
- Do not enumerate these every frame. scpsl-lint SL011 flags it; poll at 1-5 Hz or use `SlClock.EveryStaggered`.

## Lookup cost

| Call | Cost and traps |
|---|---|
| `Get(ReferenceHub)` | O(1). The hash is a native icall on the hub's `gameObject`. Returns null for a destroyed hub. In per-pair or per-frame code use `ReferenceHub` directly. |
| `Get(GameObject)`, `Get(NetworkIdentity)`, `Get(uint netId)` | O(1) for a player object. A non-player `GameObject` costs a dictionary miss plus a native `GetComponent`. The netId overload throws if neither server nor client is active. |
| `Get(string userId)` | Cache hit is O(1). A miss, and any lookup of an offline id (negative results are never cached), is an O(N) scan with closure and enumerator allocation plus one `UserId` evaluation per player. |
| `Get(int playerId)` | Always O(N) with a closure. For an O(1) path use the game's `ReferenceHub.TryGetHub(id, out hub)` then `Player.Get(hub)`. |
| `GetByNickname`, `GetByDisplayName`, `GetByName` | O(N log N): a culture-sensitive sort, then the first prefix match, not the best match. Includes host, dummies and unauthenticated hubs. |
| `TryGetPlayersByName` | Throws NullReferenceException while any client is connecting but unauthenticated (null synced nick). Filter `ReadyList` yourself. |
| `Get(IEnumerable<ReferenceHub>)` | Rents a pooled list that LabAPI never returns; return it with `ListPool<Player>.Shared.Return` or accept the allocation. |
| `Get(ICommandSender)` | Goes through the `UserId` path, so the console resolves to null after a full scan. |

- `DisplayName` is not a clean name. A custom display name gets a colored `*` suffix appended, so `GetByDisplayName(x, requireFullMatch: true)` never matches those players. Compare `Nickname` or `UserId`. `Nickname` is sanitised (angle brackets replaced) and capped at 48 characters.
- `UserId` allocates for authenticated players (it strips a salt suffix). Read it once per use and treat null as "not authenticated". Many identical lookups per tick: keep your own `Dictionary<string, ...>` maintained from `Joined` and `Left`.

## Readiness timeline (online mode)

1. Pre-authentication runs on the connection-request thread (`PlayerEvents.PreAuthenticating`). Touch no wrappers there; marshal with `MainThreadDispatcher.Dispatch`.
2. The hub exists, so a wrapper exists: `UserId == null`, `IsReady == false`, `Nickname == "(null)"`.
3. Authentication sets `UserId`. That triggers late-join role assignment, so `ChangingRole` and `ChangedRole` can fire for a joining player while `IsReady` is still false and `Nickname` is still `"(null)"`, before `Joined`. This only happens for players who connect after the round-start role assignment, not in the lobby. Then `NickSet` becomes true, `IsReady` becomes true, and group and permission refreshes run.
4. `Joined` fires last. It is the point where permissions and badge are settled. Do identity work there, or guard with `IsReady` and a non-null `UserId`.
5. `Joined` never fires for the host or dummies. `Left` fires for every non-host hub, including dummies and connections that never authenticated. Capture state at `Joined` (a `HashSet<string>` of joined ids) so `Left` handlers can tell the difference.
6. A dummy becomes `IsDummy` only on its first frame. In the frame you spawn it, it sits in `RegularNpcList`, not `ReadyList`. Defer dummy setup by one frame.

## Property side effects

| Member | What it really does |
|---|---|
| `Role = x`, `SetRole` | Full role change: `ChangingRole` (cancellable), new role object, spawnpoint teleport, loadout, one role-sync message to EVERY hub (O(N) per change, O(N^2) for a mass change), `ChangedRole`. The `Role` setter always uses reason `RemoteAdmin` with `RoleSpawnFlags.All`. Health and AHP reset regardless of flags. Use `SetRole(role, reason, RoleSpawnFlags.None)` to keep position and inventory. Never set a role from a role-changed handler without a guard. |
| `Health`, `MaxHealth`, `HumeShield`, `StaminaRemaining` | Stat write, dirty flag, one message per stat per frame to the owner and dead spectators. No clamp to max, no death path: `Health = 0` leaves a living player at 0 HP. Set `MaxHealth` before `Health`. Stat types outside the six defined modules throw. |
| `ArtificialHealth` set | Kills all AHP processes then adds one; the getter reads a stale value until the next frame. |
| `Kill()`, `Damage*` | Go through the damage path: return false (or silently do nothing for the void `Kill()`) under god mode or spawn protection. Fire `Hurting`, `Dying`, `Death`. |
| `Position` get | Cached vector, cheap. Zero vector for non-first-person roles (spectators). |
| `Position` set, `Move` | No-op for non-first-person roles. Otherwise writes the transform and sends one position-override message to that client per call. |
| `LookRotation` | The correct way to aim: vertical in -88..88, positive is up. Sends one message. |
| `Rotation` set | Passes Euler X straight in as the vertical angle, so pitch is inverted or clamped for most quaternions. Prefer `LookRotation`. Getter throws on a destroyed hub. |
| `Scale` set | Writes a static dictionary keyed by hub (never cleaned: slow leak), the transform, and one message to every ready hub. Returns `Vector3.zero` and ignores sets for non-first-person roles. Re-apply after `ChangedRole`. |
| `DisplayName` set | `ChangingNickname` (cancellable), then a SyncVar delta to all observers. `Nickname` is read-only. |
| `CustomInfo`, `InfoArea`, `GroupColor`, `GroupName` | SyncVar writes, coalesced. No validation, no events. Do not change permissions. |
| `UserGroup` set | Heavy: cancellable event, up to three encrypted console messages, permission refresh, badge update, remote-admin RPC, loop over all hubs. Null revokes local permissions. |
| `IsOverwatchEnabled` set | A full role change. |
| `IsDisarmed`, `DisarmedBy` | Edits a global list and sends the whole list to every authenticated hub on every call. `IsDisarmed = true` never expires. `DisarmedBy = other` is dropped when the disarmer is 90 m away or loses weapons. |
| `AddItem`, `RemoveItem`, `AddAmmo`, `SetAmmo` | See items-and-pickups.md. `Player.Ammo` is the live dictionary: direct writes never reach the client. Use `SetAmmo` or `AddAmmo`. |
| `SendConsoleMessage` | One encrypted message per call, never batched. |
| `SendHint*` | Throws `InvalidOperationException` for the host. One message per call, allocates a parameter array. Send only to `ReadyList` players. See scpsl-hints-and-hud. |
| `SendBroadcast`, `Server.SendBroadcast` | Per player: targeted RPC. Server-wide: one RPC to all ready observers. |
| `Mute(isTemporary: false)`, `Ban`, `Server.BanUserId` | Synchronous file write on the main thread. |
| `HasReservedSlot`, `PermissionsGroupName` (and so LabAPI string-permission checks) | Throw for a player whose `UserId` is still null. Check `IsReady` first. |
| `EnableEffect<T>`, `GetEffect<T>` | Dictionary lookup by type, then the effect's own state change. `TryGetEffect(string)` is a linear scan. |
| `Room` | Uncached room lookup per call (see facility.md). `CachedRoom` uses the game's once-per-frame cache and is null for dead players. |
| `CurrentSpectators` | O(N) scan, returns a pooled list you must return. |

## Kick, Ban, Disconnect

- All three are asynchronous. The client confirms the disconnect and leaves after about one round trip; roughly 3 seconds (150 fixed frames) is only the fallback for an unresponsive client. The player stays in `ReadyList` and keeps receiving events meanwhile. Keep a "kicked" set if you must ignore them immediately.
- A dummy is never removed this way. Use `NetworkServer.Destroy(hub.gameObject)`.
- `Ban(reason, 0)` with a live hub is a kick and the bypass-bans check never runs. For a positive duration, a target holding the bypass flag makes `Ban` return false. Check the returned bool.
- With `disconnect_drop` (default on) the server kills a living leaver first, so `Hurting`, `Dying` and `Death` fire for players who are leaving. Do not count those as kills.

## Server

- `Server.Tps` is `round(1 / Time.smoothDeltaTime)`, the same expression as the vanilla `tps` command. It is smoothed and scaled by `timeScale`, and the engine's delta cap (reported as 0.333 s, UNVERIFIED here) would floor it near 3. It hides hitches and is not a TPS in idle mode. Measure wall-clock frame time instead; see measuring-scpsl-performance.
- `Server.MaxTps` is the configured target, not a measurement. Its setter writes the engine frame-rate target immediately on a dedicated server, so setting it while idle spins the CPU at the new rate with game time still scaled by 0.01. Set it only when `!Server.IdleModeActive`. Leaving idle mode re-applies the stored value.
- Idle mode: starts when the last hub leaves or after a no-connection timeout, drops the frame target to 1 and `timeScale` to 0.01. A dummy keeps the server out of idle mode. Any new connection exits it. Treat `IdleModeActive` as "no data" for anything timed. `PauseIdleMode = true` or `IdleModeAvailable = false` exits idle at once.
- `MaxPlayers` and `ReservedSlots` write the join-gate slot count. They do not change the transport's hard connection cap, which is computed once from config.
- `RunCommand` runs on the calling thread, may return null, and a leading `!` spawns its own thread for central-server commands. Call it from the main thread.
- `Restart(port)` sends a redirect then restarts, which sends a second restart message (client reaction UNVERIFIED). `Shutdown()` is idempotent.
- `SendAdminChatMessage` dereferences `Host` and logs a failure if there is no encryption key. Check `Server.Host != null`.
- `Whitelist` and `ReservedSlots` wrappers read-modify-write their files on the calling thread.

## Round

- `IsRoundStarted` is null-safe but lags the game's own "timer is -1" state by up to about a second. `IsRoundEnded`, `KeepRoundOnOne` and `ExtraTargets` throw NullReferenceException before the first round summary object exists (for example in `Enable()`), and may return a stale value between scenes. Read `IsRoundStarted` or wait for `WaitingForPlayers`.
- `Round.Start()` has no "already started" guard and returns void, dropping the cancel result. Calling it mid-round fires `RoundStarting` again and resets `Round.Duration`. Guard with `!Round.IsRoundStarted` and the game's own round-start flag, or call `CharacterClassManager.ForceRoundStart()` if you need the bool.
- `Round.End(force: true)` only sets the ended flag. The next 2.5 s end-condition tick recomputes it from the team count, so with two or more teams alive the forced end is silently undone unless a `RoundEndingConditionsCheck` handler sets `CanEnd = true`. That handler alone suffices when the tick's own guards pass (round started, not locked, `KeepRoundOnOne` satisfied, 15 s of unscaled time since the summary object started). `End(true)` additionally bypasses that guard. Clear your own flag after the round ends or it forces every later round. EXILED documents the opposite behaviour; the runtime result here is untested.
- `Round.Restart(fast, overrideRestartAction, action)` permanently assigns the static stop-next-round setting when `overrideRestartAction` is true. `RestartSilently` forces it to do-nothing, cancelling an admin's scheduled restart or stop. Save and restore it. Calling restart twice repeats the restarted event and the pool reset. A mid-round restart can raise `RoundRestarted` twice, so the handler must be idempotent (see scpsl-game-lifecycle).
- `Round.Duration` is wall-clock (a stopwatch), unaffected by `timeScale`, reset at every scene load, and keeps running after the round ends.
- Round counters (`TotalDeaths`, `EscapedClassD`, ...) are static and reset when the summary object starts. Escapes count only for the `Escaped` role reason with a non-disarmed player, not for `RemoteAdmin`.
- `CanRoundEnd` and `ScpTargetsAmount` are LINQ over all hubs per call.

## Traps (symptom, cause, fix)

| Symptom | Cause | Fix |
|---|---|---|
| NullReferenceException or MissingReferenceException on a stored `Player` | Wrapper outlived its hub, or a round restart | Store `UserId`, re-resolve, drop caches on `Left` and `WaitingForPlayers` |
| "Collection was modified" in a loop over `List` or `ReadyList` | Yield in the loop, or a lazy `Get` on a new hub | `ToArray()` snapshot first |
| `Player.Count` is one too high, or below `ConnectionsCount` | Counts dummies, excludes the host and connecting clients | `GetAll(AuthenticatedPlayers)` for humans |
| `Nickname` is `"(null)"` and `IsReady` false inside role events | Late-join role assignment runs before the nickname is set | Do identity work in `Joined` |
| `Joined` not run for a player whose `Left` runs | `Left` fires for unauthenticated hubs, dummies | Track joined ids |
| `Player.Get(commandSender)` null in console | Console sender is not a user id | `Server.Host` |
| Hint or broadcast throws, or client stalls | Host has no hint display; unauthenticated clients may not have handlers (precaution) | Send only to `ReadyList`, never the host |
| Timers and MEC waits run 100x slow | Idle mode `timeScale` 0.01 | Wall-clock deadlines (scpsl-main-thread-and-timing) |
| All dummies look like one player in a map | Shared `ID_Dummy` | Key by `NetworkId` |
| Scale resets or leaks across rounds | Static scale dictionary keyed by hub | Re-apply after `ChangedRole` |

## Cost summary

Wrapper creation is one dictionary add plus one reflection call per registered `CustomDataStore`, once per hub. `Role` changes cost O(N) messages each. `Position`, `Scale`, `IsDisarmed`, `UserGroup` and `SendConsoleMessage` are uncoalesced per call, so do not call them per frame per player. Stats and SyncVars coalesce: writing the same SyncVar 100 times in a frame sends one delta. All per-call nanosecond figures are estimates.

## Threading

Every `Player`, `Server` and `Round` member is main-thread only: plain dictionaries, native hash calls, unlocked network writer pools. `PreAuthenticating` is the one event raised off the main thread, and its args carry strings, not wrappers. In `Task` or thread-pool code capture `UserId` and ids, then dispatch back before `Player.Get`. Whether each Unity getter throws or corrupts off-thread is UNVERIFIED; treat it as undefined. In this library, `SlKit` offers `MainThread.Post` and a per-plugin worker (`Background.ForOwner(...).TryRun`) for that hand-off.

## Re-verifying on a new build

- Re-check that `Player.ReadyList` is still a lazy `Where` over the live `Dictionary.Values` and that `Player.Get(int)` still scans (LabAPI `Player.cs`, public on GitHub).
- Check `Player.Rotation` setter against `LookRotation`, and the `Role` setter's reason and flags.
- Check `Round.End`, `Round.Start`, `Round.Restart` bodies and whether the round-summary object is still assigned only in its start step.
- Grep the game assembly for `smoothDeltaTime` users if `Server.Tps` changes.
- Run sl-probe on a staging server: join, leave, kick, round restart, and log `IsReady`, `UserId` and `Nickname` inside `ChangedRole`, `Joined` and `Left`.
- UNVERIFIED items worth one in-game test: `IpAddress` inside `Left`, `Round.End(true)` with two teams alive, `Server.Restart(port)` client behaviour.
