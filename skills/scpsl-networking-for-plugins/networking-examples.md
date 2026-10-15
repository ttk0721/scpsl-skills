# Networking examples: walkthrough

Verified against LabAPI 1.1.7 / SCP:SL 14.2.7 (2026-10).

Complete project: `examples/networking-snippets/NetworkingSnippets.csproj` (namespace `Examples.NetworkingSnippets`; builds with 0 errors; lint: 0 errors, 0 warnings, one SL003 info note on an `IsAllowed` write the game ignores for the `None` channel). Each pattern is one class; `NetworkingPlugin.cs` wires them (per-round reset at `WaitingForPlayers`, cleanup on `Left`, one SlClock job for the periodic work) and `NetDemoCommand.cs` exercises them with `netdemo`. Facts behind every line are in toys-and-spawning.md, mirror-mechanics.md and hiding-players.md. Behaviour that comes from reading code, not from a client, is marked.

| File | Pattern | Rule |
|---|---|---|
| `Toys/ToySpawnQueue.cs` | byte-budgeted spawn slices, identity cap | 1, 2 |
| `Toys/ToyFactory.cs` | `Create(networkSpawn: false)`, configure, `IsStatic`, `Spawn()`; anchor plus static children | 3 |
| `Toys/ToyMover.cs` | thaw, move, freeze after the settle time | 3, 4 |
| `Viewers/FakeScale.cs` | per-viewer scale through an existing message | 5, 6 |
| `Visibility/GhostGate.cs` | hide a player, with the audio caveats | 9 |

## 1. Spawn cheaply

Three habits (`ToyFactory.SpawnStatic`):

```csharp
PrimitiveObjectToy toy = PrimitiveObjectToy.Create(position, Quaternion.identity, scale, null, false);
toy.Type = type;
toy.Color = color;                  // few distinct colours: one Material per exact Color, never freed
toy.Flags = PrimitiveFlags.Visible; // not Collidable: no collider anywhere
toy.IsStatic = true;                // no LateUpdate body on the server, no position updates on the client
toy.Spawn();
```

Writes before `Spawn()` cost nothing (dirty bits set before the first observer exists are cleared). With the default `networkSpawn: true` the same writes cost one extra message per toy per player. `IsStatic` does not shrink the spawn message and does not remove the toy from the per-tick identity walk, so the identity cap is the bigger lever.

Slice by bytes, not by count (`ToySpawnQueue.Tick`):

```csharp
long perToy = (long)BytesPerPrimitiveSpawn * Math.Max(1, players);   // 134 B x ready players
while (_jobs.Count > 0)
{
    Job next = _jobs.Peek();
    long cost = next.Toys * perToy;
    if (created > 0 && cost > left) break;    // always run one job, or a big one never runs
    _jobs.Dequeue();
    created += RunOne(next);                  // own try/catch: one bad job must not stall the queue
    left -= cost;
}
```

`TryEnqueue` refuses work past the cap (`MaxToys`). Read `Player.Count` once per tick; it is a LINQ count. Drive `Tick` from one SlClock job, spawn in the lobby, and drop the queue at `WaitingForPlayers` (the scene reload already destroyed the toys).

Groups: `ToyFactory.SpawnGroup` spawns one invisible anchor first, then children created with `parent: anchor.Transform`. A parent that is not spawned yet has netId 0 and the child would attach to nothing. `Spawn()` does not cascade, and a group counts as `1 + children` identities in the budget.

## 2. Move without churn

```csharp
public bool MoveTo(PrimitiveObjectToy toy, Vector3 localPosition)
{
    Vector3 target = Quantize(localPosition);              // 2 cm grid: noise cannot defeat the equality check
    if (_moving.TryGetValue(toy, out Entry entry))
    {
        if (entry.Target == target) return false;          // "go to X" every frame is a no-op
        toy.Position = target;                             // coalesces with other writes in one SyncInterval
        entry.FreezeAt = Now() + SettleSeconds();
        return true;
    }
    toy.IsStatic = false;                                  // thaw: a static toy ignores transform writes
    toy.Position = target;
    _moving.Add(toy, new Entry { Target = target, FreezeAt = Now() + SettleSeconds() });
    return true;
}
```

`Tick()` (one SlClock job, 5-10 Hz) freezes toys whose `FreezeAt` has passed. Being late only keeps a toy non-static a little longer; being early is the bug: with `SyncInterval > 0` the dirty bits stay set, so `IsStatic = true` set a frame later ships in the same message as the new position and the client never moves.

Settle time = `SyncInterval` + two send ticks + a network allowance + the smoothing tail. The tail comes from the client chase `Lerp(current, target, deltaTime * raw * 0.3)` with `raw = 256 - wrapperValue`: about `4.6 / (raw * 0.3)` seconds to get within 1% (derived from code, frame-rate independent only approximately, not measured). Smoothing 0 removes the tail. Group moves: move the one non-static anchor; static children under a moving parent move with it.

Defaults: `SyncInterval` 0.1 for many movers; 0 only for one hero object (about 3-5 KB/s per observer, estimate).

## 3. Per-player fake data, only where the API supports it

| Need | Supported by | Example |
|---|---|---|
| Per-viewer scale | existing `ScaleMessage` sent to one connection | `FakeScale` |
| Per-viewer role | `RoleSyncEvent` (public) | not compiled: role-specific spawn data, and another plugin's data in the writer makes it unsafe |
| Per-viewer SyncVar | hand-built `EntityStateMessage` | not compiled: component index and bit layout are build-specific |
| Per-viewer toy or pickup | internal spawn-to-one-connection path (reflection) | not compiled: you own cleanup on leave and restart |
| Per-viewer hint text | `Player.SendHint` | already per player |

`FakeScale.Send`:

```csharp
// An existing message type to ONE connection: a vanilla client already has the handler.
viewer.Connection.Send(new SyncedScaleMessages.ScaleMessage(scale, target.ReferenceHub));
```

Only the viewer's client model changes; the server's hitbox and physics keep the real scale. Anything that writes the REAL scale later (`Player.Scale`, a new role's scale controller) broadcasts to every ready client and overwrites the fake. There is no event for that, so `FakeScale` re-sends on `ChangedRole` and from a 5 s `Reassert` job (about 19 B per entry). Entries are dropped on `Left` because PlayerIds are recycled. A custom `NetworkMessage` type would disconnect the client.

## 4. Hide and reveal players

`GhostGate` is the small, correct version: a deliberate "ghost", not an anti-ESP layer (that needs the full send-set rules in hiding-players.md). The core:

```csharp
// Hot event: every receiver x target pair, every position tick. Flag tests only; never sets IsVisible = true.
private void OnValidatedVisibility(PlayerValidatedVisibilityEventArgs ev)
{
    if (!ev.IsVisible) return;                                  // vanilla already culled him
    if (!_hidden.Contains(ev.Target.PlayerId)) return;          // precomputed set, no geometry
    if (_exemptViewers.Contains(ev.Player.PlayerId)) return;    // spectators, overwatch, 079, SCPs, chosen staff
    ev.IsVisible = false;
}
```

It subscribes only while a ghost exists (`Activate` / `MaybeDeactivate`). The game allocates the event args for every pair even with no subscriber, so that is the only saving available.

| Caveat | What the example does |
|---|---|
| The event is only the position stream; voice and gunshots use the vanilla check | cancels the ghost's voice (`SendingVoiceMessage`) and shots (`ShootingWeapon`), both configurable |
| A hidden shooter's first shot plays at the parked transform, volume 0 | shots blocked by default; allowing them needs the Harmony prefix in hiding-players.md |
| Footsteps and item sounds go silent | cannot be fixed from a plugin; the ghost is silent by design |
| Radio voice is not range-gated | voice is cancelled, not trusted to the client range check |
| Hide only after about 1 s, so playing sounds are not cut | `HideDelaySeconds` timer on the SlClock round scope |
| Reveal is instant (client snaps) but the first 1-3 voice packets are lost | `VoiceLeadSeconds` keeps voice and shots blocked briefly after a reveal |
| A noclip target is frozen visible by the client | admission refused; turning noclip on ends the ghost |
| SCP mechanics depend on visibility | human roles only; SCP receivers exempt by default |
| Spectators, Overwatch, 079 must not be culled | exempt receiver set, refreshed on `ChangedRole` |
| PlayerIds are recycled | state dropped on `Left` and at round reset |

Known limit, not closed here: the real position still travels in role-sync data to a client that reads raw packets. Closing that needs a Harmony patch (hiding-players.md).

## Testing

Read running-a-test-server first: ask whether the owner has a server and wants local tests (no or unsure: deliver with build and lint evidence only), look defaults up in its paths file and ask when theirs differ, check the server is private, then ask whether to use an existing port or our own instance on an unused one (for example 9999). Never touch the owner's ports unasked. `netdemo` spawns toys and hides players for everybody connected: run it on a private test instance only. Verify with a vanilla client: toy moves land and stop where intended, a revealed ghost's first words are heard, a fake scale survives a role change.

## Re-verifying on a new build

- `AdminToyBase` still skips static toys on the server and gates the client on the synced `IsStatic`; `SyncInterval` prefab defaults (log them); the wrapper still inverts `MovementSmoothing`; `Create(..., networkSpawn: false)` still leaves the toy unspawned.
- Serialize one primitive spawn and count the bytes behind the 134 B constant.
- `ScaleMessage` still has a client handler that applies the scale to the target hub, and a role's scale controller still re-broadcasts the real scale on construction.
- `ValidatedVisibility` still fires for every receiver including Spectators, and its result still replaces the vanilla result; voice and gunshots still use the vanilla check.
- Rebuild `examples/networking-snippets/` against the new `LabApi.dll` and game assemblies, then run scpsl-lint over it.
