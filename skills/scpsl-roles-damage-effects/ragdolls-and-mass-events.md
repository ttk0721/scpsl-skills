# Ragdolls and mass-event spikes (reference)

Verified against LabAPI 1.1.7 / SCP:SL 14.2.7 (2026-10). Behaviour described from reading the
game's logic. UNVERIFIED marks code-derived claims not run on a server. All millisecond, byte and
message-count figures are unmeasured estimates (order of magnitude only). Measure with sl-probe or
SlKit `FrameProfiler` before acting on them (measuring-scpsl-performance).

## Ragdolls

### Lifecycle

- Spawned on death by the kill step. The `SpawningRagdoll` event (`PlayerSpawningRagdollEventArgs`)
  can cancel it (the death, inventory drop and Spectator role change still continue). The event's `DamageHandler` setter is dead
  (SL003); mutating fields of the existing handler works because it is the same object. The event
  args wrap the prefab ragdoll on every death (one small allocation per death).
- `Ragdoll.SpawnRagdoll(role, position, ...)` without an owner returns null when the position is out of waypoint range.
- The ragdoll stores a reference to the damage handler and applies its hit forces when it starts.

### Never frozen or expired on the server

Clients freeze ragdolls according to their own setting. The server's freeze time is
`int.MaxValue` unless the server's stored preferences hold a value for the freeze option: the bool
has no default, so the code falls back to false. Consequence: server-side ragdolls keep their
physics bodies, joints and per-frame work (`Update`, `LateUpdate`, sleep check, bone copying while
awake) for the whole round.

Only these remove a ragdoll:

| Remover | Notes |
|---|---|
| Round restart | |
| SCP-049 revive | |
| `Ragdoll.Destroy()` | Your timer or cap. |
| Remote Admin `cleanup corpses [n]` | Newest first; needs the facility-management permission. |

`RagdollManager.FreezeTime` has a public static setter. Treat it as an experiment, not a fix:
side effects on elevator-follow, SCP-049/3114 hitbox and centre-point behaviour are UNVERIFIED, and
whether anything resets it later was not checked.

"Never freezes" depends on the host's stored preferences. A game client run by the same OS user in
the same application folder could change it (UNVERIFIED on your host).

### What to do

- Cap `RagdollManager.AllRagdolls` (a `HashSet`) and `Destroy()` the oldest on a timer driven by
  one scheduler (scpsl-main-thread-and-timing), not a coroutine per ragdoll.
- Do not create bodies in bulk. For deaths that need no corpse use `SilentDamageHandler` (no ragdoll,
  no drops, still `Hurting`/`Dying`/`Death`), or cancel `SpawningRagdoll`.
- Each new ragdoll is another object in every observer's spawn set: with N players, N spawn messages
  per ragdoll (and per dropped item) at death time.

```csharp
// One job, one clock: trim old ragdolls to a cap. Main thread only.
private static readonly List<Ragdoll> Scratch = new List<Ragdoll>(128);

private static void TrimRagdolls()
{
    Scratch.Clear();
    foreach (Ragdoll r in Ragdoll.List) Scratch.Add(r);
    int excess = Scratch.Count - MaxRagdolls;
    for (int i = 0; i < excess; i++)               // oldest-first order of Ragdoll.List is NOT verified:
        Scratch[i].Destroy();                      // keep your own creation stamps (SpawnedRagdoll) if age matters
}
// Enable(): SlClock.Every(TimeSpan.FromSeconds(10), TrimRagdolls, "ragdoll.trim");
```

## Mass events: what the engine does in one frame

Vanilla runs these as single-frame loops. Every handler you hang on them multiplies.

| Event | What happens in one frame | Estimated size |
|---|---|---|
| Round start | One `ServerSetRole(RoundStart)` per player; role assignment scans all players per assigned human role (O(N squared) lookups); cold pools `Instantiate`; loadout items `Instantiate`; N role-sync messages per role change | 60 to 180 ms for a full server, plus roughly 65 to 325 ms extra on the first big round after process start (cold pools); on the order of 10 000 role-sync messages |
| Respawn wave | `WaveRespawning` (LabAPI, can rewrite roles), then `ServerSetRole(Respawn/RespawnMiniwave)` per player in try/catch, announcements, `WaveRespawned` | about 25 to 75 ms for a wave of 30 |
| Warhead detonation | `DealDamage(WarheadDamageHandler)` for every living detonatable player in one loop: per kill `Hurting`/`Hurt`/`Dying`, ragdoll `Instantiate` + spawn, every item and ammo stack becomes a spawned pickup, `ServerSetRole(Spectator)`, `Death`. The warhead handler opts out of neither ragdoll nor drops | worst case 230 to 520 ms for about 80 victims; on the order of tens of thousands of spawn and snapshot messages (several MB) |
| Decontamination | Effect enabled for everyone in one pass; ticking effects tick together, so players with equal HP die in the same frame | not estimated; deaths cluster into one frame |

Cost shape: a role change is O(N) messages, so N role changes are O(N squared). A death into
Spectator sends the new spectator a full stat snapshot of every living player. Every `ServerSetRole`
also runs an O(N) count in the round summary. Vanilla does the batch itself, so a handful of extra
`SetRole` calls (tens of players, a few ms by these estimates) is acceptable. The guidance is a
scaling guard, not a ban.

### What not to do inside those frames

Handlers that run per player inside these loops: `ChangingRole`, `Spawning`, `Spawned`,
`ReceivingLoadout`, `ChangedRole`, `Hurting`, `Hurt`, `Dying`, `Death`, `SpawningRagdoll`,
`UpdatingEffect`, `UpdatedEffect`, `WaveRespawned`, `RoundStarted`.

| Do not | Why | Instead |
|---|---|---|
| `Player.List` scans or LINQ per call | N per call times N calls | Cache or precompute once per event |
| `SetRole` again after vanilla's (batch) | Second round of O(N) role sync | Redirect inside `ChangingRole` |
| `EnableEffect` storms, item/pickup/ragdoll creation, teleports, hints, broadcasts | Each has its own fan-out and allocation | Queue, then spread over frames |
| Wrapper allocation before a cheap filter | Per-call garbage in a burst | Type or role test first |
| I/O, HTTP, file writes, `Logger` calls | Blocks the frame; every log call walks the stack | Snapshot POCOs, off-thread (the plugin's own SlKit `Background.ForOwner` worker or `SlHttp.CreateClient` client) |
| Throwing | LabAPI catches per handler but formats a long message each time; N throws in one frame | try/catch with a rate-limited log |
| Per-player coroutines | See SL005 | One scheduler job |

Queue and spread (SlKit): collect the work in the handler (O(1)), process a bounded number per
frame through `SlClock.EveryStaggered`. The source is read once per period, so swap two buffers:

```csharp
private List<Player> _filling = new List<Player>(64);   // handlers add here (main thread)
private List<Player> _draining = new List<Player>(64);  // clock reads this

private void OnChangedRole(PlayerChangedRoleEventArgs e)
{
    if (e.Player.IsHost || e.Player.IsDummy) return;
    _filling.Add(e.Player);                              // O(1): no work yet
}

private IReadOnlyList<Player> TakePending()              // called once per period
{
    _draining.Clear();
    (_filling, _draining) = (_draining, _filling);       // swap buffers
    return _draining;
}

private static void ApplyLoadoutExtras(Player p)
{
    if (p.IsDestroyed || !p.IsAlive) return;             // may have left or died meanwhile
    // heavy per-player work here: spread by elapsed wall time over the period
}

// Enable(): SlClock.EveryStaggered<Player>(TimeSpan.FromSeconds(1), TakePending, ApplyLoadoutExtras, "roles.extras");
```

The first extras land up to one period late. For anything the player must have at spawn (position,
role, loadout items) use the supported hook (`ChangingRole`, `Spawning`, `ReceivingLoadout`)
instead, not a queue.

### Cold pools

Role and model pools start small (default 10, "create more" on overflow) and never shrink, so the
first large round after process start `Instantiate`s prefabs. Real serialized sizes are UNVERIFIED;
estimates put the extra hitch at tens to hundreds of ms, not seconds. Pre-warming means private
pool internals and reflection; usually accept the one-time cost.

## Pitfalls

| Symptom | Cause | Fix |
|---|---|---|
| Server hitches late in a ragdoll-heavy round | Ragdolls never freeze on the server | Cap and `Destroy()` old ones |
| 100+ ms spike at warhead | N kills x (ragdoll + item pickups + role change) | Don't add per-kill work; `SilentDamageHandler` only if you own the kill |
| Frame spike on round start from your plugin | Per-player work in `ChangedRole` | Queue + stagger |
| Log floods during a wave | Handler exception per player | try/catch, rate-limited log |
| Wrapper allocations every death | `SpawningRagdoll` args wrap the prefab | Subscribe only if you need it |

## Unverified

1. Real pool sizes of role and model prefabs.
2. Everything numeric above (frame times, message counts): estimates, unmeasured.
3. Side effects of changing `RagdollManager.FreezeTime`.
4. Whether the host's own server instance of a ragdoll also freezes after a `Freeze()` broadcast.

## Re-verifying on a new build

- Re-read the death kill step (ragdoll spawn, inventory drop, role set) and which handlers opt out.
- Re-read `RagdollManager` for the freeze-time default and who removes ragdolls (grep for
  `NetworkServer.Destroy` on ragdolls; check the cleanup command).
- Re-read the round-start assigner, wave spawner, warhead detonation and decontamination loops:
  still single-frame? still per-player `ServerSetRole`/`DealDamage`?
- Measure a big round with `FrameProfiler` and event markers (round start, warhead) and replace the
  estimates in this file with your numbers.
