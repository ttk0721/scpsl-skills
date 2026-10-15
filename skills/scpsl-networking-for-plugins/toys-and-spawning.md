# Toys, pickups, spawning and per-observer data

Verified against LabAPI 1.1.7 / SCP:SL 14.2.7 (2026-10). Compiled versions of the spawn, move and per-viewer patterns: networking-examples.md.

## Spawn and despawn

- `NetworkServer.Spawn` assigns an id, then adds every ready connection (including the host loopback) as an observer. Each observer gets a serialized `SpawnMessage` **inside the Spawn call**. Cost is O(ready connections) on the main thread.
- A joining player receives every spawned identity. Join time scales with total spawned objects (vanilla 481-628 identities; map schematics up to 25k).
- Despawn sends a 6 B destroy message per observer. A re-show is a full new spawn.
- `Pickup.Create` and toy `Create` already spawn (networkSpawn defaults to true). A second `Spawn` only logs a warning.
- Slice by bytes: `slice x ~134 B x ready players` per frame. 50 toys x 100 players is ~670 KB queued in one frame, in front of the next position message, and clients instantiate the whole slice in one frame. Head-of-line estimate: the reliable window is 64 packets (~65 KB per RTT at a 1024 MTU), so a 228 KiB burst needs ~3.5 RTTs, on the order of 150-200 ms of delayed positions and voice at 50 ms RTT (estimate, not measured).
- `SpawnableCullingParent` culls client-side only; it does not reduce spawn bytes.
- Hidden-from-everyone-but-one ("per-player toy"): set `visible = ForceHidden` before spawn, then send the spawn message to one connection (needs reflection, `SendSpawnMessage` is internal). You own cleanup on disconnect and round restart.

```csharp
// Spawn in byte-budgeted slices: call SpawnTick from one SlKit SlClock job. Never one frame, never per-toy coroutine.
private readonly Queue<Action> _spawnQueue = new Queue<Action>(1024);
private const int BytesPerSpawn = 134;          // PrimitiveObjectToy, per player
private const int BudgetBytesPerFrame = 200_000; // tune; see the head-of-line estimate

private void SpawnTick()
{
    int players = Player.Count;                  // once per tick; counts dummies too (no bytes), so it errs on the safe side
    int slice = Math.Max(1, BudgetBytesPerFrame / (BytesPerSpawn * Math.Max(1, players)));
    for (int i = 0; i < slice && _spawnQueue.Count > 0; i++)
        _spawnQueue.Dequeue()();
}
```

## Moving toys

- `IsStatic = true`: the server `LateUpdate` does not read the transform (no SyncVar change), and the client skips position updates while its synced `IsStatic` is true. A transform write to a static toy is a silent no-op.
- To move: set `IsStatic = false`, change the transform, and keep it non-static until the move was captured by a server `LateUpdate`, sent by a `Broadcast` that `syncInterval` allows, delayed by the network, applied by one client frame, and (when `MovementSmoothing > 0`) converged. "One server frame and one client frame" is only enough at `SyncInterval = 0`. With `SyncInterval > 0` the dirty bits stay set, so `IsStatic = true` set a frame later ships in the same message as the new position and the client never moves.
- To move a group of static toys, parent them to one non-static anchor. `Parent` works on static toys. Spawn the parent first (it must have a `NetworkIdentity`). If you may unparent later, set `Parent` after `Spawn()` instead of through `Create(parent)`: the first unparent of a toy parented before spawn is not replicated (derived from code, not tested on a client).
- `SyncInterval`: prefab values are 0.1 for primitives and light sources and 0 for text, speaker, camera, waypoint toys (from an asset scan, not from code; read `AdminToy.SyncInterval` at runtime). One toy at 0 costs ~2.9-4.6 KB/s per observer (estimate). Default budget choice: 0.1 s or more for many moving toys; 0 only for a single hero object.
- Per-frame writes on a throttled toy cost CPU only: writes inside one interval coalesce into one message with the last value.
- `MovementSmoothing` is an exponential chase toward the target, not interpolation between snapshots, and the LabAPI wrapper inverts the byte (wrapper 255 = raw 1 = slowest). Pick it from the lag you want to hide.
- Update size: position only ~48 B, position + rotation ~64 B, + scale ~76 B per delivered update, per observer.
- `TextToy`: `Arguments` is a `SyncList<string>`; `Add`/`Clear` record an op even for identical content, and prefab interval is 0. Compare before touching, never `Clear()` + `Add()` per frame.

## Pickups

- Spawn ~95 B per player (unverified: fields not re-summed).
- In motion: ~50 B every 0.25 s while awake (4 Hz x players), then one rewrite on sleep.
- Static pickups and toys are the only safe candidates for custom interest management.

## Per-observer data

| Need | Mechanism | Note |
|---|---|---|
| Different role per observer | `FpcServerPositionDistributor.RoleSyncEvent` | per pair; also sanitize spawn data (hiding-players.md) |
| Per-viewer scale | `ScaleMessage` to one connection | `Player.Scale` itself goes to everyone |
| Fake SyncVar / fake RPC | hand-built `EntityStateMessage` / `RpcMessage` via `player.Connection.Send` | overwritten by the next real dirty write of that var; joiners get the real initial state; re-send on join, role change, respawn |
| Hide an object per player | custom `NetworkServer.aoi` | hide = client-side Destroy |
| Hint muting per player | `HintDisplay.SuppressedReceivers` | |

Never hide through a custom `aoi`: room objects, doors, elevators, lockers, scene objects, waypoints, player hubs, ragdolls, SCP-244, speakers, camera toys, in-flight projectiles, `WaypointToy`. A hide is a client Destroy and a re-show a full spawn (~95 B for an object that cost 0), so a custom `aoi` can **increase** bandwidth for static objects; override `SetHostVisibility` as a no-op if you must. Any `InterestManagementBase` component sets both server and client `aoi` in `OnEnable`. Mirror never rebuilds observers on its own.

Malformed fake EntityState data gives client warnings and misparsed state, not a disconnect. An unknown RPC hash logs a client error; an unknown netId for an RPC is ignored.

## Scale and teleport

- `Player.Scale` has no equality check: every set, even to the same value, packs once and goes to every ready client (~19 B). Animating scale per frame is ~1.1 KB/s per receiver per animated player (estimate). Joiners get one scale message per FPC player.
- `Player.Position` setter sends one `FpcPositionOverrideMessage` (14 B) to the owner; others learn it from the position stream (a waypoint change costs one full 7 B entry). Avoid per-frame calls.

## Re-verifying on a new build

- `AdminToyBase`: server `LateUpdate` still skips static toys; client still gates on synced `IsStatic`; `SyncInterval` defaults per prefab (log them).
- `NetworkServer.aoi` is still null at runtime (log it); nothing assigns it.
- `Pickup.Create` / `AdminToy.Create` spawn defaults; wrapper inversion of `MovementSmoothing`.
- Spawn message size: serialize one primitive and count bytes.
