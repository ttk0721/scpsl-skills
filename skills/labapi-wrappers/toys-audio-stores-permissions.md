# AdminToys, audio, CustomDataStore and permissions

Verified against LabAPI 1.1.7 / SCP:SL 14.2.7 (2026-10). Behaviour is from reading code, not from a running server, unless noted. Cost figures are estimates. UNVERIFIED marks what was not observed. For voice-chat and proximity audio see scpsl-voice-and-audio.

## AdminToys

### What exists

| Wrapper | Notes |
|---|---|
| `PrimitiveObjectToy` | `Type`, `Color`, `Flags` (collidable, visible) |
| `LightSourceToy` | intensity, range, color, shadow, shape, spot angles |
| `SpeakerToy` | `ControllerId`, `IsSpatial`, `Volume`, `MinDistance` (1), `MaxDistance` (15) |
| `TextToy` | format string plus argument list, resolved on the client |
| `CapybaraToy`, `InteractableToy`, `WaypointToy`, `ShootingTargetToy`, `CameraToy` | see traps |
| `SpawnableCullingParent` | not an `AdminToyBase`; bounds-based client culling parent |

Every toy is one network identity with synced `Position`, `Rotation`, `Scale` (all LOCAL), `MovementSmoothing` and `IsStatic`.

### Creating

- `XToy.Create(..., networkSpawn: false)`, set everything, then `toy.Spawn()`. Dirty state set before the first observer exists is discarded, so you pay one spawn message. With the default `networkSpawn: true`, every property you set afterwards ships as one extra combined state message per toy and observer (not one per property).
- Set position, rotation and scale BEFORE `Spawn()`. They are copied into the synced fields once at server start, and afterwards only by the per-frame update of non-static toys.
- `Create` does not run the remote-admin spawn defaults (grey color, sphere, positioned at the admin). You get raw prefab defaults; always set `Color`. The prefab's default color is UNVERIFIED.
- `Spawn()` does not cascade to children.
- Spawning has host-side effects: a primitive builds a collider and material on the server too, an interactable builds a collider.
- Cost estimate: roughly 130 bytes per toy per player reliable on spawn, all in one frame when spawned in a loop. Spread large batches over frames.

### Static and moving

- `IsStatic = true` on every toy that does not move. It skips the per-frame update on server and clients. It does not shrink the spawn message and does not remove the per-tick observer iteration, which stays O(players x spawned identities). A non-static idle toy sends nothing but still runs three compares per frame. (Estimate, unmeasured: 0.6-1.4 ms per frame for 4000 toys.)
- A static toy's client never applies position updates. To move one: set `IsStatic = false`, change the transform, wait, then set `IsStatic = true`. Never flip to static in the move frame. One frame is only the server-side minimum: the network sends once per send interval, so the new position and `IsStatic = true` can share a message, which the client then never applies. Wait at least a couple of send ticks plus latency. With `MovementSmoothing != 0` the client lerps only while non-static, so freezing early stops the toy mid-glide; wait several smoothing time constants, or set smoothing to 0 first. The exact safe delay is UNVERIFIED.
- The wrapper inverts `MovementSmoothing`: user value 255 is the slowest, 1 effectively snaps. Smoothing runs on clients only.
- Parenting: parent only to spawned objects with a network identity (another toy, a culling parent, a player), and spawn the parent first. A parent without an identity or not yet spawned gives netId 0, so the child appears at the origin or unparented on clients. If you may unparent later, spawn first and set `Parent` after `Spawn()`: a toy created with a parent and later unparented sends no change message (logic only, UNVERIFIED at runtime).

### Costs and traps

| Trap | Cause | Fix |
|---|---|---|
| `ShootingTargetToy.Create` / `CameraToy.Create` give an unexpected variant | First prefab with the component wins (3 target and 5 camera variants) | Instantiate the prefab you want from the client prefab list and wrap it with `AdminToy.Get(base)` |
| `CapybaraToy.CollidersEnabled = false` changes nothing for clients | Wrapper writes the raw field, not the synced property | `toy.Base.NetworkCollisionsEnabled = value` |
| `InteractableToy.OnSearchAborted` fires when the search completes | Wired to the searched event | Use `toy.Base.OnSearchAborted` or the `OnPlayerSearchToyAborted` event |
| Animated or rainbow `PrimitiveObjectToy.Color` bloats memory | One cached material per exact color, never freed | Use a small fixed palette; never animate `Color`; do not change `Type` after creation (it rebuilds the collider) |
| Camera toys count up | Instance counter only increments within a round | Do not churn camera toys |
| `WaypointToy` ids run out | Ids 1..254 shared with map waypoints; returns 0 and logs an error when full; scale unsupported | Create few |
| Wrapper for a disabled toy re-created on each `Get` | Uncached when inactive | Compare `Base` |

### Speakers

Each `SpeakerToy` costs about 94 KB for its playback buffer, a native decoder, an audio clip and a per-frame update, on the server AND every client regardless of distance. Destroyed speakers leave the buffer as garbage. Budget them.

## Audio: SpeakerToy and AudioTransmitter

### Contract

- PCM is mono `float[]`, 48 000 Hz, nominal range -1..1. Frames are 480 samples (10 ms), Opus at up to 120 kbit/s, 100 messages per second per transmitter. LabAPI multiplies samples by about 1.41 before encoding to cancel a client-side -3 dB loss. Whether peaks above about 0.7 clip is UNVERIFIED; treat 0.7 as conservative headroom, and listen-test a full-scale sine before normalising a library.
- Pad every clip with zeros to a multiple of 480 samples. By hand-tracing the transmit loop (not in game), a clip that is not a multiple leaves the coroutine alive forever, `IsPlaying` stays true, and the loop keeps encoding silence. Track clip duration yourself and call `Stop()` after it (`Timing.CallDelayed(duration, ...)`).
- `SpeakerToy.Play(controllerId, samples, queue, loop)` uses a static transmitter per id, created on first use and never disposed (max 256 ids). `Play` enqueues the array by reference (no copy) and always overwrites `Looping`. `queue: false` interrupts; the default appends.
- `Skip(n)` throws `InvalidOperationException` if `n - 1` exceeds the queued clips. Clamp it.
- `CurrentSampleCount` keeps the last clip's length after it ends; do not use it for end detection.

### Network behaviour

- Every audio frame goes to EVERY authenticated hub, including the host and dummies, on a reliable ordered channel (the script default; the shipped channel table is UNVERIFIED), unless `ValidPlayers` is set. Up to roughly 16 KB/s per player per stream at the bitrate cap (estimate). Always set `ValidPlayers` for anything not meant for everybody. The predicate runs per hub every 10 ms: keep it allocation-free, exception-free and cheap, and test `IsHost`/`IsNpc` inside it.
- Clients cull by `MaxDistance` from their camera even when `IsSpatial = false`. The default 15 means far players hear nothing from a "global" speaker. Set `MaxDistance` deliberately (at least the map extent for a global one).
- `ControllerId` is shared by every speaker with that id, and there is one transmitter per id. Allocate unique ids per logical channel across plugins. A second `new AudioTransmitter(id)` with overlapping recipients garbles into the same buffer; split recipients with `ValidPlayers`. Sharing one id across speakers saves server encode and send, but each in-range speaker still decodes every message on every client.
- A main-thread stall beyond about 0.5 s makes the transmitter send all missed frames in one tick; the client buffer holds 0.5 s and clears on overflow, so audio is skipped rather than delayed.
- Any exception in the transmit loop (for example `ValidPlayers` throwing) is logged and the coroutine ends with the queue left intact. Call `Stop()` before replaying.
- The host loopback copy is decoded by the server's own client if a speaker is within range, wasted work on a dedicated server (estimate 2-6 ms per second per stream with decode).
- Clip memory: 192 KB per second of mono 48 kHz audio (a 3-minute track is about 35 MB). Encode cost per frame is UNVERIFIED; measure with a stopwatch.
- Decode and resample files off-thread (pure managed), then hand the finished `float[]` to the main thread through your own `ConcurrentQueue` drained by something that already runs on the main thread. Do not call `Timing.*` from the worker for the hand-off. SlKit `MainThread.Post` fits.

## CustomDataStore

```csharp
public sealed class MyStore : CustomDataStore<MyStore>
{
    public MyStore(Player owner) : base(owner) { }   // must be public, must never throw
    protected override void OnInstanceCreated() { }  // inside the constructor, before registration
    protected override void OnInstanceDestroyed() { } // leave, or UnregisterStore
}
// Enable():  CustomDataStoreManager.RegisterStore<MyStore>();
// Use:       var s = MyStore.Get(player);
// Shutdown:  CustomDataStoreManager.UnregisterStore<MyStore>();
```

- One instance is created per `Player` wrapper through reflection, for EVERY hub including host and NPC dummies, when the wrapper is built. It is destroyed when the wrapper is removed, after `Left`.
- A store lives for one hub's lifetime: it survives role changes and respawns, is lost on leave or reconnect, and is never persisted. Expect it NOT to survive a round restart, since the scene reload recreates hubs (destroy-versus-reconnect order UNVERIFIED in game). Save to disk yourself, keyed by `UserId`, from `OnInstanceDestroyed` or `Left`, and reload after rejoin.
- A throw in the constructor or `OnInstanceCreated` aborts `new Player(...)`, leaves `Server.Host` unset and skips the other stores for that player. Never throw; guard `owner.IsHost` and `owner.IsNpc`.
- Calling `Get(player)` inside `OnInstanceCreated` recurses, because the instance is stored after the constructor returns. Use `this` or `Owner`.
- The wrapper exists before authentication, so `UserId` is null in the constructor. Initialise identity-dependent data after `Joined`.
- A missing public `(Player)` constructor gives `MissingMethodException` at join.
- A disabled plugin's stores keep being created until `UnregisterStore<T>()`. `Disable()` is never called by LabAPI or the game, so call it from a `ServerEvents.Shutdown` handler (or on your own teardown path).
- Cost: reflection per registered store per join and leave; `Get` is a linear list scan plus two dictionary lookups.

## Permissions

Two unrelated systems.

| | Game permissions | LabAPI permissions |
|---|---|---|
| Form | `PlayerPermissions` bitmask from the remote-admin config groups | strings via `IPermissionsProvider` |
| Check | `sender.CheckPermission(PlayerPermissions.X, out response)`; the game's own check is "any bit" | `player.HasPermission("a.b")`, `HasPermissions`, `HasAnyPermission` |
| `Player.HasPermission(PlayerPermissions)` | LabAPI's version is "all bits" (`HasFlag`); the game's is "any bit" | Pass a single flag so they agree |

### LabAPI provider

- `DefaultPermissionsProvider` reads `permissions.yml`. The file is per OS user, not per port. Its default content is one empty `default` group, and the constructor rewrites the file at every start, dropping comments.
- Resolution: the player's remote-admin `Members` group name, then the group in the file. Players who hold remote-admin rights by other routes (global or staff flags, override password, a plugin calling `SetGroup`) look like "no group" to LabAPI. A group name present in `Members` but absent from the file resolves to an empty group, not to `default`: add every remote-admin group, at least with `inherited_groups: [default]`.
- Matching: `.*` is root; exact string; a wildcard covers exactly one more segment (`a.*` matches `a.b`, not `a.b.c`, not `a`). List `a.b.*` too. Inherited groups are followed with no cycle guard: an inheritance cycle recurses without bound (effect UNVERIFIED, likely fatal). Validate before saving.
- `ReloadAllPermissionsProviders` does not re-read the file. Restart to apply edits, or write your own provider. Removing `.*` leaves the group root until restart.
- `AddPermissions` and `RemovePermissions` edit the player's GROUP, shared by everyone in it, without dedupe, and rewrite the file synchronously. They are group editors, not per-player temporary grants.
- With several providers, `HasPermissions` (all) must be satisfied by ONE provider, not the union; `HasPermission` passes if any provider grants it; Add and Remove go to every provider.
- On an `ICommandSender`, the server console is always allowed and an unresolved player sender is denied.
- The default provider is registered after all plugins are enabled and before `OnPluginsEnabled`. During `Enable()` a permission query silently returns false and `Server.DefaultPermissionProvider` is null. Query from `OnPluginsEnabled` or later. Registering your own provider in `Enable()` is fine. A malformed `permissions.yml` makes the provider constructor throw and `OnPluginsEnabled` is never raised (game-side effect UNVERIFIED).
- Check `player.IsReady` and a non-null `UserId` before any LabAPI permission call: the group lookup throws for a null id.
- `PlayerEvents.GroupChanged` does not tell you LabAPI permissions changed (it tracks the in-game group, not the `Members` key).
- Cost: a LINQ pass and closures per check. Fine per command; do not run per frame per player. Cache booleans per player at join and refresh after your own changes. `AddPermissions` writes a file on the main thread.
- Command-side use of these checks: see scpsl-commands-and-permissions.

## Misc extensions

- `LinqExtensions.MinBy/MaxBy` enumerate the source up to three times and call the selector twice per element. Materialise first, or use a loop in hot code.
- `PriorityQueue<T>` is a non-stable min-heap, not thread-safe; `PriorityQueuePool<T>` is a thread-safe pool.
- `Logger.Debug(msg, canBePrinted)` still evaluates `msg`. Logging costs a stack walk; avoid it on hot paths.
- `Player.GetAll()` default includes dummies, and `ValidPlayers` and store code also see the host and NPCs.

## Threading

Everything here is main-thread only (Unity objects, network writer pool, MEC, static dictionaries, a shared static audio buffer). Only pure managed work, such as decoding a file to `float[]`, may run elsewhere.

## Re-verifying on a new build

- Check `AdminToy.Create<T>` still picks the first prefab, and whether the capybara and interactable wrapper bugs are fixed.
- Check `AudioTransmitter` frame loop: the multiple-of-480 termination, the sqrt-2 gain, the send path and `ValidPlayers` handling. Test in game: play a 1000-sample clip, count audio messages afterwards, and check whether an all-zero frame encodes to 2 bytes or fewer.
- Measure encode time per frame and real encoded sizes with a stopwatch.
- Check `PluginLoader` ordering of provider registration versus `OnPluginsEnabled`, and whether `ReloadPermissions` re-reads the file.
- Confirm the store lifecycle across a round restart by logging `OnInstanceDestroyed` and `OnInstanceCreated`.
- Re-test the static-toy move delay with a real client at your send rate.
