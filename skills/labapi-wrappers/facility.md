# Facility wrappers

Rooms, doors, elevators, generators, tesla gates, hazards, respawn waves, warhead, decontamination, lights, Cassie, SCP-914, pocket dimension, structures.

Verified against LabAPI 1.1.7 / SCP:SL 14.2.7 (2026-10). Behaviour is from reading code, not from a running server, unless noted. Cost figures are estimates. UNVERIFIED marks what was not observed.

## When the map exists

A round restart reloads the scene, so every room, door, elevator, generator, tesla, light and hazard is a NEW object and every wrapper from the last round is dead. `RoundRestarted` means "restarting": old objects still exist until the async load finishes. Rebuild everything at `ServerEvents.WaitingForPlayers`.

Map generation runs before `WaitingForPlayers`. `ServerEvents.MapGenerated` fires partway through it:

| Thing | Ready at `MapGenerated`? | Ready at `WaitingForPlayers`? |
|---|---|---|
| `Room.List`, `ConnectedRooms`, `Door.List`, `Door.DoorName` (resolved in the wrapper constructor) | yes | yes |
| Room coordinate lookup (`RoomsByCoords`), so `GetRoomAtPosition` falls back to raycasts | no | yes |
| `Door.Rooms`, `Room.Doors` | no | yes |
| `Door.NameTag`, `Door.Get(string)`, `Door.Get(DoorName)` | no (set in the nametag extension's start) | yes |
| Lights, structures (generators, lockers), elevators, tesla gates | no | yes |
| `Scp914Controller.Singleton`, `AlphaWarheadController` | no / partly | yes |

`WaitingForPlayers` fires from the host player's start, so scene-object start steps have almost certainly run, but the order is not proven. If a start-registered collection (`Elevator.List`, `LightsController.List`, `Tesla.List`, `Scp914Controller.Singleton`) is empty, retry one frame later. Use `MapGenerated` only to read `Room.List` or the seed, or to change the seed or cancel generation.

Wrappers cache only when the object is active. `Get(x)` for an inactive object builds a NEW uncached wrapper every call; compare `Base`.

Some game statics survive the scene reload: the four wave wrapper objects, the door nametag table (stale entries until overwritten), `Scp914Controller.Singleton` (never cleared), the outside warhead panel reference, per-item 914 processors (on item prefabs), and the pocket item-recycle settings. Other state the game re-initialises every round: wave timers, tokens, influence and config, 914 mode, pocket exit poses and exits, elevator start floors, door and elevator locks, lights, warhead state. Apply overrides every round at `WaitingForPlayers`, never once at plugin load.

Wave tokens, size and enabled flags are also reset by the client-ready hook, which is raised from the host player's inventory start and from the scene-changed callback. Whether that runs before or after `WaitingForPlayers` is UNVERIFIED, so re-apply wave overrides again at `RoundStarted` (or after a short delay) and after a config reload.

## Rooms

- `RoomName` is a closed 42-value enum and is NOT unique. `Unnamed` is a catch-all for generic rooms; the checkpoint room exists once per zone side; tesla, office and airlock rooms repeat. Select with `Name` + `Zone` (+ `Shape`) and handle 0, 1 and many.
- Coordinates depend on the seed. Do not hard-code world positions. Store `room.Transform.InverseTransformPoint(p)` and re-resolve the room each round.
- `Room.Get(RoomName|RoomShape|FacilityZone)` is a deferred LINQ over all rooms (closure and enumerator per enumeration). Build your own `Dictionary<RoomName, Room[]>` once per round.
- `Player.Room` and `Room.GetRoomAtPosition` do an uncached lookup: one dictionary hit inside a room's single main cell, else up to two 15 m raycasts plus parent walks (Surface, pocket, shafts). Estimated 3-10 microseconds per miss. `Player.CachedRoom` reuses the game's once-per-frame per-player cache; it is null for dead players, and spectators have `Position == Vector3.zero`. Use `CachedRoom` for per-tick logic and never call `Room.Players` per tick (N uncached lookups).
- `Room.AdjacentRooms` is cached forever on first read per wrapper and includes elevator links. Read it, and call `FindPath`, only after all elevators exist. `Elevator.List.Count > 0` is a weak guard because chambers register one by one; compare with the number of elevator groups that have doors, or defer a frame.
- `RoomUtils.FindRooms(..., null)` returns a shared static list that the next call clears. Pass your own list, and clear it yourself before each call (only the null path clears).
- `Room.FindPath` is pooled (`ListPool`).

## Doors

- Locks are independent bits in `DoorLockReason`: Regular079, Lockdown079, Warhead, AdminCommand, DecontLockdown, DecontEvacuate, SpecialDoorFeature, NoPower, Isolation, Lockdown2176. Full-lock bits are AdminCommand, DecontLockdown, SpecialDoorFeature, NoPower and Lockdown2176. Warhead and DecontEvacuate are "can only open", Isolation is "can only close", the 079 bits affect SCP override only. An unknown bit means no lock at all, so you cannot invent your own.
- `Door.IsLocked` is `ActiveLocks > 0`, but its setter changes only the AdminCommand bit. `IsLocked = false` does not unlock a door held by warhead, decontamination, 079 or isolation bits, and it clears the bit for RA and every other plugin. Read `LockReason`; use `Lock(reason, true/false)` for the bit you set; do not clear game-owned bits (the game re-adds them). `SpecialDoorFeature` also makes the game's warhead and decontamination opener ignore the door.
- `Door.IsOpened = x` writes the target state directly and ignores locks, permissions and animation cooldown. It must not be used to "open a locked door for someone". Use `PlayerInteractingDoor.CanOpen = true`, which covers both the lock path and the permission path (see labapi-events).
- `Door.Get(FacilityZone)` and `Door.Get(Room)` use the first room only, allocate, and throw `InvalidOperationException` on a door with no rooms. `Door.Zone` also uses the first room but returns `Other` instead of throwing. `Door.Rooms` allocates an array every call. Iterate `room.Doors`, or build `Dictionary<Room, List<Door>>` once per round.
- Only about 38 named doors have a nametag. `DoorName` is `None` for the rest, and a door can carry a tag outside the map (`NameTag` set, `DoorName == None`). Find other doors by room, position or adjacent-room pair. Resolve names at `WaitingForPlayers`; `Door.Get(string)` can return null for a frame because last round's nametag entries linger.
- `Door.List` includes elevator doors, dummy doors, non-interactable doors, checkpoint doors AND each checkpoint's sub-doors. Filter by wrapper type before bulk operations (exclude `ElevatorDoor`, `DummyDoor`, `NonInteractableDoor`). Checkpoint sub-doors are damageable-door wrappers, so type cannot exclude them: collect every `CheckpointDoor.SubDoors` into a set and skip those bases. `Map.GetRandomDoor()` can return any of them.
- Breakable doors: `TryBreak()` returns false if the door has no broken prefab or the damage type is ignored. `IsBroken = true` deals exactly max health and fails if remaining health is above it. `Health = 0` only writes a property. Use `TryDamage(float.MaxValue, type)` with an allowed type and check the return.
- Doors run an update every frame (about 150-300 doors, estimate). Prefer `DoorLockChanged` over polling.
- A door destroyed before room registration can stay in `Door.Dictionary`; skip `IsDestroyed` doors.
- Door ids are a clock wrapping at 255; keep plugin-spawned doors few (client effect UNVERIFIED).

## Elevators

- Each elevator chamber picks a RANDOM starting floor at round start. Locks and `DynamicAdminLock` are per round. Re-apply at `WaitingForPlayers`.
- `SetDestination(level, force)` passes `force` to the game's `allowQueueing`, which is inverted from its name. `force: false` (the default) writes the destination immediately, even mid-travel, and restarts the movement sequence from the last arrived door, so the cabin can jump. `force: true` queues. Guard with `elevator.IsReady`, or pass `force: true`. Same-level calls are no-ops. `SendToNextFloor` uses the same path.
- `Elevator.Interact(player)` hard-codes panel id 0. Whether the internal panel has id 0 is UNVERIFIED; it probably works and throws only if no id-0 panel exists. Prefer `SetDestination`; wrap `Interact` in try/catch.
- `Elevator.Doors` and `Rooms` allocate per call. Locking all doors sets bits without events.
- A moving chamber rebuilds its bounds every frame and notifies SCP-106 minimap, SCP-939 and shake subscribers. Prefer `ElevatorSequenceChanged` over polling.

## Generators

- `Generator.Engaged = true` (and `Activating`, `IsOpen`, `IsUnlocked`) sets a flag only. It raises no `GeneratorActivating`/`GeneratorActivated`, grants no influence or respawn-timer reward, and sets no activator. The SCP-079 recontainer does count `Engaged` flags.
- `RemainingTime` rewrites the countdown value; it takes effect on the next update and does nothing while not `Activating` beyond the dropdown decay. Set `Activating = true` first, or adjust `TotalActivationTime`.
- Completing a generator by script (`RemainingTime` plus `Activating`) leaves no activator recorded. The generator objective then reads a null `Player` by reading of the code (NullReferenceException before `GeneratorActivated`, UNVERIFIED at runtime). To get events and rewards, drive the real path with a real player: `generator.ServerInteract(player, collider)`, or award influence yourself.
- Generator wrappers are created in the structure's start step and cached per room.

## Tesla gates

- All tesla logic is in one controller's fixed-tick loop (50 Hz): per gate, per alive player, up to two distance checks, raising `IdlingTesla` every tick while someone is in idle range and `TriggeringTesla` every tick while in trigger range until the shock starts. Keep those handlers allocation-free, branch first on a cached bool, never do room lookups inside.
- Cancelling those events keeps the per-player loop running and re-raises the event for each further in-range player every tick.
- `InactiveTime > 0` skips the gate. A finite value is decremented every tick and each write is a dirty SyncVar, so a timed disable generates traffic while counting down. A huge value (`float.MaxValue`, or any value of about 2^19 seconds or more) freezes under float rounding, so it costs one compare per tick and sends nothing after the first write. For a permanent disable use `float.MaxValue`.
- `Tesla` wrapper creation keys a dictionary by room and throws on a second gate in the same room or a null room (whether any room has two gates is UNVERIFIED). Use `Tesla.List` and compare positions.

## Hazards

Hazards run an O(players) update per hazard per frame and raise `StayingInHazard` (an allocation) per occupied hazard per frame. `Hazard.Spawn` instantiates and network-spawns in the NEXT frame's start step; do not touch network state in the same frame. `Hazard.Room` is an uncached lookup.

## Respawn waves

- Four process-lifetime wave wrappers: `PrimaryMtfWave`, `PrimaryChaosWave`, `MiniMtfWave`, `MiniChaosWave`.
- `InstantRespawn()` calls the game's spawn with no player, state or token check: it resets timers and decrements tokens even when nobody spawns, and tokens can go negative. Check that spectators are available first and restore tokens.
- `InitiateRespawn()` sets the next wave without checking the current state, so it can hijack an in-progress wave. Call it only when the wave queue is idle.
- `MaxWaveSize = 10` is stored as a percentage (`10 / all hubs`, hosts and dummies included) and re-evaluated at spawn time, so it does not cap at 10. Trim `e.Roles` in `WaveRespawning` for an exact cap.
- `MiniRespawnWave.InitiateRespawn()` can spawn a disabled mini wave. `TryGetCurrentMilestone` throws for non-staff factions.
- Per-round: tokens reset to the initial value, influence to 0, `AvailableRespawnsLeft` to 2, timers reset, config size and enabled re-read. Plain fields on the wave object (`AdditionalSecondsPerSpawn`, sergeant percent, mini-wave multiplier and roles) persist across rounds, so reset them explicitly.
- Each join and each death sends four wave updates plus influence to that client.

## Warhead

- `Warhead.Start()` first zeroes the countdown and cooldown, so calling it during a running countdown restarts it. Check `!IsDetonationInProgress`.
- `Warhead.Detonate()` is `DetonationTime = 0`, which ignores lock, cooldown and a cancelled `WarheadStarting`.
- Cancelling `WarheadDetonating` returns before the detonated flag is set, and the update retries every frame, so the event re-fires every frame. If you cancel, change state once (`Stop` or a new `DetonationTime`).
- `Warhead.Stop()` silently does nothing when not in progress, locked, or 10 seconds or less remain. Check `DetonationTime > 10 && !IsLocked`.
- `DetonationTime = x` mid-countdown is a force-time call that re-fires `WarheadStarting`/`Started`, resets cooldown and suppresses subtitles. Set it once; for "add time", compute from the current value.
- Statics throw NullReferenceException early (`TimeUntilDetonation` dereferences the singleton). Gate on `Warhead.Exists` and `WaitingForPlayers`.

## Decontamination

`Decontamination.Status = Forced` twice runs the whole routine twice (second kill coroutine, second events, second door and elevator pass); there is no re-entrancy guard. `IsDecontaminating` stays true after `Disabled`. A warhead detonation sets `Disabled` itself. Check `Status` before setting.

## Lights

- `LightsEnabled` and `FlickerLights` are independent. A pending flicker timer from `TurnOffLights(10)` or a game effect still turns lights ON when it expires, even if you set `LightsEnabled = false` later, and `TurnOnLights()` does not clear it.
- For a persistent blackout use `FlickerLights(float.MaxValue)` and restore with `FlickerLights(0)`. Neither form survives the game's own flicker calls (079 blackout, recontainer lockdown, `overcharge`, MicroHID fire, SCP-2176, a candy effect), which overwrite the timer. Re-assert from `RoomLightChanged` (new value true) or on a timer for a hard blackout.
- Controllers register in their start step, so `Map.TurnOffLights` at `MapGenerated` affects nothing. A map-wide toggle writes one SyncVar and raises one event per controller.

## Cassie

- Cassie is a serial queue: one announcement at a time, highest `priority` first, with a 1 s (no background) or 3.1 s cooldown after each. It plays only while a local host hub exists. The queue is cleared at round restart and when the announcer singleton is re-created. Unknown words are skipped silently.
- `Announcer.Message(string)` builds a payload with an EMPTY subtitle, so no subtitles show. Pass `customSubtitles`, or use `Announcer.Message(new CassieTtsPayload(text, autoGenerateSubtitles: true, playBackground: true))`.
- `Announcer.IsValid(word)` and `CollectionNames` rebuild a distinct list of the whole line database per call. Build one `HashSet<string>` from `CollectionNames` once, after `WaitingForPlayers` (the database is empty at plugin enable and between rounds, so an early set rejects every word). It holds raw line names only; suffix and modifier forms are not in it (UNVERIFIED).
- `ServerEvents.CassieAnnouncing` and `CassieAnnounced` are declared but nothing raises them for custom announcements. Wrap your own queueing helper. The SCP-termination pair works.
- The announcer runs a per-frame update on the dedicated server (up to 26 audio sources); a small constant cost. Call `Announcer.CalculateDuration` and `Message` only from the main thread, never concurrently (shared static buffers).

## SCP-914

- `Scp914.Mode` writes a per-round config entry (resets each round; whether it persists to disk is UNVERIFIED). Apply mode and knob at `WaitingForPlayers`.
- `SetItemProcessor` edits item PREFABS, which persist across rounds. Install processors once and treat them as global.
- `Scp914.Interact(code)` defaults to `Server.Host` (null when absent) and returns while the cooldown runs. Pass a real `Player`; check `SequenceCooldown <= 0`.
- Guard `Scp914Controller.Singleton` (Unity null) before any `Scp914.*` static.

## Pocket dimension

- The game re-randomises exits after EVERY successful exit and one frame after any teleport is created or destroyed, resetting all to "killer" then promoting `pd_exit_count` (default 2). `PocketTeleport.IsExit` does not persist. Decide in `LeavingPocketDimension` (cancel or `IsSuccessful`), or re-assert after the re-randomisation.
- `ForceExit(player)` teleports players who are not in the pocket, and `LeavingPocketDimension` then receives a null teleport. Guard with `IsPlayerInside` and null-check `e.Teleport`.
- `PocketTeleport.Spawn()` creates a bare unparented object with a NON-trigger sphere collider, so it never fires. Parent it to the pocket room transform and set the collider to a trigger. Whether it needs a network spawn is UNVERIFIED (it is a server-side trigger). Spawning one also triggers a re-randomisation a frame later.
- Exit poses are cleared each round; `RemoveAllExitPosesForZone` leaves a zone empty, in which case exit uses the capture position. Re-add poses per round and never leave a zone empty.
- The pocket item manager does a room lookup for every tracked pickup every frame, mostly raycasts inside the pocket (estimate 3-10 microseconds per miss). Keep populations small.

## Structures, cameras, map helpers

- `Structure.Get` returns null (and logs) for unmapped structure types. Null-check.
- `Locker.FillChambers()` never populates its list and throws or does nothing. Use `FillAllChambers()` or loop `Chambers` and call `Fill()`.
- `Map.GetRandomLocker*` and `GetRandomPickup(zone)` throw `NotImplementedException`. Other `Map.GetRandom*` are O(n) with `ElementAt` and LINQ; avoid in hot paths.

## Cost summary

The room lookup is the dominant facility cost in practice; doors, lights, generators and idle elevators total tens of microseconds per frame (estimate). The one non-trivial idle loop is tesla. Allocation notes: `Door.Rooms`, `Door.Get(Zone|Room)`, `Door.ToString()`, `Room.Get(name|zone|shape)`, `Elevator.Doors`, `Announcer.CollectionNames` and `IsValid` allocate per call; `Room.FindPath` is pooled.

## Threading

Main thread only. These are thin shells over Unity objects, raycasts and network SyncVars, and the registries are unsynchronised dictionaries mutated by game events. Enumerating them off-thread while a round restarts can throw or read torn state. Copy plain values (`RoomName`, `FacilityZone`, `Vector3`, strings) at `WaitingForPlayers`, hand those to workers, and marshal results back through your own main-thread queue.

## Re-verifying on a new build

- Enumerate what exists at `MapGenerated` versus `WaitingForPlayers` (log `Door.Rooms.Length`, `Elevator.List.Count`, `LightsController.List.Count`, `Tesla.List.Count`) and compare with the table above.
- Check `Door.IsLocked`'s setter bit, `Elevator.SetDestination`'s forwarding of `force`, `Generator.Engaged`'s setter, `Warhead.Start/Detonate/DetonationTime` and `Announcer.Message` against LabAPI source.
- Check the lock-reason enum and the lock-mode switch for new bits.
- Check whether `CassieAnnouncing` now has a raiser, and whether `Map.GetRandom*` stubs were implemented.
- Re-test the UNVERIFIED items: wave override order versus `WaitingForPlayers`, `Elevator.Interact`, a plugin-spawned `PocketTeleport`, `Scp914.Mode` persistence.
- Re-count named doors against the `DoorName` enum.
