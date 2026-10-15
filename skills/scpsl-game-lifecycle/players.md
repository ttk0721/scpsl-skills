# Player lifecycle: connect, join, role, leave

Verified against LabAPI 1.1.7 / SCP:SL 14.2.7 (2026-10). Part of scpsl-game-lifecycle.

## Join pipeline

| # | Step | Thread |
|---|---|---|
| 1 | Connection request queued; a dedicated connection-request thread processes it. Version check, optional challenge round trip, rate limits | **not main** |
| 2 | If connections are delayed (from process start until the host hub starts, and again from every restart initiation): rejected with a "retry later" code. **`PlayerEvents.PreAuthenticating` is NOT raised** | not main |
| 3 | Online mode: token, bans, whitelist, geoblock, then **`PreAuthenticating`** (`CanJoin`, `IsAllowed`, `CustomReject`), accept, **`PreAuthenticated`** | not main |
| 4 | Peer connected is queued and drained on the main thread; the client is sent the scene and the map seed | main |
| 5 | Client loads the scene, sends ready and add-player; the player prefab is instantiated; every spawned identity is sent to the new client | main |
| 6 | `ReferenceHub.Awake` registers the hub; on `Start` the LabAPI `Player` wrapper is created. The hub is **Unverified**: it is in `Player.List` but not in `Player.ReadyList`. Idle mode is left. Role is lazily `None`, with no role event | main |
| 7 | Online: authentication is requested once the connection is ready; `authentication_timeout` (45 s default) rejects. Offline: the id is assigned immediately and `Joined` comes from the nickname command | main |
| 8 | Auth response is validated, then finalised: `UserId` is set, the instance mode becomes ready-client (**a late-join role may be given here**), nickname is set, permissions refreshed, **`PlayerEvents.Joined`** fires last | main |

Preauth handlers: touch only the args and your own thread-safe state. No `Player`, no Unity API, no MEC (MEC thread-safety was not checked, so avoid it). One thread serves all join requests, so a slow handler delays every joiner. `Logger.*` is fine from that thread. Marshal results to the main thread through a concurrent queue (SlKit: `MainThread.Post`). See scpsl-main-thread-and-timing.

Joiners during a restart get bounced and retry. Do not expect a preauth event for a player who connected while connections were delayed.

## Joined is not a connection event

- **Joined fires again for every player after every restart.** The new hubs are Unverified again and re-authenticate. With the default full restart, clients are expected to reconnect with a fresh preauth (inferred); with `enable_fast_round_restart` the connection persists and re-auth is accepted via a fast-reload list. Either way the new hub goes through step 8. This follows from the auth code, so treat "Joined re-fires" as derived. How clients react to the restart message is unverified (the client handler is stripped from the server build).
- **Left fires for every non-host hub** at a restart, including unauthenticated hubs and dummies (dummies earlier, when the level change destroys them; the rest when the old scene is destroyed).
- So one `Joined`/`Left` pair is one hub lifetime, which is at most one round, and not one visit by a human.

A "session" that must mean a visit has to survive that boundary. Pattern: park instead of flush.

```csharp
sealed class Visit { public string UserId; public string Nick; public double ActiveSeconds; public long JoinedUtc; public bool Parked; public long ParkedUtc; }

static readonly Dictionary<string, Visit> _visits = new Dictionary<string, Visit>(128);
static readonly List<Visit> _finished = new List<Visit>(32);

// A null or empty check is NOT enough: offline mode and dummies have non-empty ids.
static bool IsAccountId(string id) => !string.IsNullOrEmpty(id) && !id.StartsWith("ID_", StringComparison.Ordinal);

static void OnJoined(PlayerJoinedEventArgs ev)
{
    string id = ev.Player.UserId;
    if (!IsAccountId(id)) return;                            // null, "ID_Offline_<netId>_<unix>", "ID_Dummy" are not identities (see below)
    if (_visits.TryGetValue(id, out Visit v)) { v.Parked = false; return; }   // resumed after restart or fast rejoin
    _visits[id] = new Visit { UserId = id, Nick = ev.Player.Nickname, JoinedUtc = DateTime.UtcNow.Ticks };
}

static void OnLeft(PlayerLeftEventArgs ev)
{
    string id = ev.Player.UserId;                            // wrapper is still alive and complete here
    if (!IsAccountId(id) || !_visits.TryGetValue(id, out Visit v)) return;
    v.Parked = true; v.ParkedUtc = DateTime.UtcNow.Ticks;   // do not report yet; a restart-time Left lands here too
    // A periodic sweep moves visits that stay parked longer than a grace period (longer than a restart
    // plus reconnect, 60 s is a starting estimate) into _finished with left:true.
}
```

**Offline mode breaks parking.** There `UserId` is a non-empty per-connection id `ID_Offline_<netId>_<unix>`, so a null/empty guard does not filter it, and it changes with every new hub. Park and resume never match: every restart (and every rejoin) starts a new visit and leaves a stale parked one, so per-visit sessions silently degrade to per-hub. Dummies all share `ID_Dummy`, the host is `ID_Dedicated`. `UserId` is a safe visit key only for authenticated online-mode players. State this assumption in your design and in the code comment. On an offline server per-visit identity is not available at all; fall back to per-hub and say so, or key by a stable value you control, never by `UserId`.

Decide explicitly what the metric is: per-hub (resets every round, fine for per-round stats), or per-visit (needs the parking above). A report that says "session" but resets at each restart, and sends one `left:true` record per player per round, is wrong for the second meaning.

## Visits across process exit

Parking survives a scene reload, not the process. `restart_after_rounds` or a stop-next-round request ends the process: statics are gone, every connection ends, and the next boot starts with new state (new visit ids, empty parked set). Nothing will ever send a `left` record for visits that were open at that moment. Whether `Left` fires per player during shutdown was not verified, so do not count on it.

- In the `ServerEvents.Shutdown` flush, close every open or parked visit yourself and mark it **terminal**: for example `left: true` with `reason: "shutdown"`, written in the same synchronous flush that spools the data. A spool that only snapshots sessions as `online`/`parked` hands the next boot records that nobody will ever finish.
- If a receiver applies upsert-by-visit-id (latest record wins), an unclosed `online` record stays online forever. Either send the terminal record, or document that `reason: "shutdown"` ends the visit and have the receiver special-case it. Do not leave this implicit.
- A spool posted by the next process is old data. It must not reuse the new process's visit ids and must not be treated as live presence.
- `Shutdown` also fires when Unity itself quits, so the flush can run from more than one path. `Quit` is guarded, so it runs once. It never runs on SIGKILL or a crash: the receiver also needs a staleness timeout.

## Roles at join

- A new hub's first role is **`None`**, not Spectator. Lobby players stay `None` until round start.
- At round start, roles are assigned in the same frame as `RoundStarted`, **after** the event (host loopback RPC, derived). Handlers of `RoundStarted` see `None` for everyone.
- Eligible for assignment: not alive (`None` counts as dead), Spectator only if ready to respawn, and instance mode ready-client, host or dummy. **Unverified hubs (not yet authenticated) and the dedicated server are excluded.**
- Late join (after round start): the role is decided inside step 8 before `Joined`. If the user id was already in the round's "already spawned" set (a rejoin), or the late-join timer exceeds `late_join_time`, the player becomes Spectator; otherwise a human role is spawned. With the template value `late_join_time: 0` practically everyone is Spectator. The set resets at each round.
- So for a late joiner **`Spawned` and `ChangedRole` come before `Joined`, and `Nickname` is not set yet**. Do gameplay setup in `ChangedRole`/`Spawned`; use `Joined` for identity/data load only, and keep it idempotent.
- `ServerSetRole` order: `ChangingRole` (cancel or edit), role init, `Spawning`, teleport, `Spawned`, `ReceivingLoadout`, `ReceivedLoadout`, role sync to every client, `ChangedRole`. `ChangedRole` is the last signal and is the one where inventory is already filled.
- `Death` fires after the player is already Spectator; the old role travels in the args.

## Leaving

- If `disconnect_drop` is on (read once at match creation) and an alive player disconnects, the game first deals lethal damage with the unknown-cause handler: the full chain `Hurting`, `Hurt`, `Dying`, ragdoll and item-drop events, `ChangedRole(Spectator)`, `Death` all fire **before** `Left`. Stats and anti-cheat that count `Death` will see a kill. Detect it via the damage handler type in `Death` args, or by ordering. Players with god mode or spawn protection skip the chain.
- Consequently, in `Left` an alive leaver may already be Spectator when `disconnect_drop` is on. To record the role they actually played, take it from the `Death` args of that chain, or cache the role on `ChangedRole` and ignore the Spectator change when you know it is a leave.
- In `Left`: `Role`, `UserId` and `Nickname` of the wrapper are still readable (the hub is still registered; the wrapper is removed right after the event). Reading the role there beats hard-coding `Unknown`.
- Idle mode re-enters if the hub count falls to one or fewer (but not while a restart pauses it).

## Who to send things to

`Player.List` includes unauthenticated hubs, the host and NPCs. `Player.ReadyList` is authenticated players with a nickname set, plus dummies (it excludes the host and regular NPCs). Send hints, broadcasts and messages only to `ReadyList`. LabAPI documents that sending to unready hubs can soft-lock a joining client. `ReadyList` and `Count` are lazy LINQ over the live dictionary values: they allocate per call, are not snapshots, and mutating the player set during enumeration (for example a lazy `Player.Get` on a fresh dummy) can throw. Copy to your own reused list once per frame or period.

## Identity, ids, caches

- `PlayerId` is reused FIFO once at least 16 ids are free, and each restart frees all of them. A per-id cache (slot tables, hidden-pair rows, last-sent text) can hand a new player the previous occupant's row.
- Reset every per-player cache on `Joined`, on `Left`, and at round boundaries (`WaitingForPlayers`). Where a cache can safely default for an unknown id (fail open), that limits the damage but does not remove the bug.
- Prefer keys that cannot alias: `UserId` for persistent data; for per-frame arrays indexed by `PlayerId`, clear the slot in `Left` and on `Joined` before use.

## Dummies and NPCs

Covered in idle-config-dummies.md: `Joined` never fires for them, `Left` does, they count as players for the lobby, roles, wave size and idle mode, and they are destroyed at every restart.

## Re-verifying on a new build

- Read `PlayerAuthenticationManager` finalisation: is `Joined` still raised last, after the late-join role?
- Read `RoleAssigner.CheckLateJoin` and the config key `late_join_time`.
- Read `CustomNetworkManager.OnServerDisconnect` for `disconnect_drop` behaviour.
- Probe on a live server with two clients: log `Joined`, `Left`, `ChangedRole` with `UserId`, `Nickname`, `Role` and `PlayerId`; go through a join mid-round, a leave mid-round with `disconnect_drop` on and off, and a restart with both fast and full restart. This also settles the unverified restart reconnect behaviour.
