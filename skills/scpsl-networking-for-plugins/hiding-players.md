# Hiding players from other players (anti-ESP, ghost mode)

Verified against LabAPI 1.1.7 / SCP:SL 14.2.7 (2026-10). Client behaviour is read from the game assembly, not observed on a client; a stripped client body (for example footstep audio) is only confirmed by a test with a vanilla client.

A small compiled "ghost" (position stream plus the voice and shot caveats, no Harmony): networking-examples.md.

Rule of thumb: send everything the receiver can see **or hear**. "Only what he sees" is wrong.

## What the vanilla client does with an omitted target

| Fact | Consequence |
|---|---|
| Every player model missing from a receiver's `FpcPositionMessage` gets `IsInvisible = true`, and the third-person motor parks its transform at (0, 6000, 0) every frame. | No grace period, no freeze at the last position. Hiding is instant. |
| A target that reappears is sent as a full state (the per-pair "previously sent" entry was reset to default). | The client snaps. No interpolation from the sky. Look-ahead only has to cover network latency. Rotation catches up by lerp in roughly 0.1-0.2 s (estimate). |
| Exception: a noclip target. | The client freezes it **visible** at the old position. Never hide noclip targets. |
| Footsteps, item sounds, proximity voice and gunshot audio are positioned on that same transform. | They go silent or play at (0, 6000, 0). |
| The client drops proximity voice from a speaker that fails its own local range check, computed from the parked position. | A hidden speaker is inaudible even if the server forwards the packets. A receiver wearing SCP-1344 passes the check, but the playback source still hangs at the parked position. Radio voice is not range-gated, but its nearby proximity copy is lost too. |
| Gunshot audio: the server computes `shooterVisible` from the **vanilla** visibility check. Only when the shooter is vanilla-invisible does it append the absolute shooter position. | An event-hidden shooter is vanilla-visible, so no position is appended and the client plays the shot at the parked transform (channel volume falls to zero). Any plugin that hides through the event alone has this bug. |
| The shot RPC leaves the server **immediately**; `FpcPositionMessage` follows later in `LateUpdate`. | A reactive reveal ("a shot was seen, keep him sent from the next sweep") is too late for the FIRST shot: the RPC already went out with no position, the shooter is still hidden, so the shot plays at (0, 6000, 0) at volume 0. A hold armed by a shot event only helps from shot 2. See "Shots" below. |

## The per-pair path (why the LabAPI event is the wrong hook)

`FpcServerPositionDistributor` builds one position message per receiver every tick and walks every target:

1. vanilla visibility check (36 m, 70 m if the observer is on the surface; SCP-1344 ignores the distance flags);
2. the LabAPI `ValidatedVisibility` event (`IsVisible` settable; its result replaces the vanilla result);
3. position/rotation delta against the last sent state (a dictionary write per pair);
4. `GetVisibleRole` (vanilla only, ignores the event), `RoleSyncEvent`, and `RoleSyncInfo` only when the per-pair visible role changed.

Consequences:

- The event only affects the position stream. Voice, gunshots and `GetVisibleRole` use the vanilla check.
- Setting `IsVisible = true` for a vanilla-culled target **includes** a >36 m target (costs bytes).
- Receivers without the custom-visibility role (Spectator, Overwatch) are never culled: they get all N-1 entries every tick (~38 KB/s per spectator with 90 targets, estimate).
- Dummies are receivers and targets here (CPU only), not in the Broadcast term.
- The event class is allocated per pair per tick even with no subscriber. See labapi-events for the HOT rating and **REQUIRED SUB-SKILL:** scpsl-engine-cost-model for the budget. The place to do this properly is a Harmony transpiler/prefix on the distributor (scpsl-harmony-patching), not a handler that does geometry per pair.

## Rules: who must be sent

The layer only **narrows** the vanilla result; it never reveals. Sent set = PVS union audibility radius pas[T] union mechanic exceptions.

| Case | Rule |
|---|---|
| Receiver is Spectator, Overwatch, SCP-079, Filmmaker | Vanilla (everyone). Spectators switch target locally. |
| Receiver is SCP-939; SCP-096 Distressed/Enraged; SCP-106 stalking/submerged | Skip the layer. They have their own client-checked perception or need far targets. |
| Receiver wears SCP-1344 | No PVS (x-ray), but cap at vanilla 36/70 m. |
| Same room, Surface, Pocket, unknown room (lookup failed, elevator, schematic outside the map) | Vanilla distance only (fail open). |
| Rooms linked by open portals | Visible if the lower-bound portal path D_room <= 36 m + margin (graph, door state and distance rules: "Portal graph" below). Portal open = RoomConnector (always), glass EZ doors (`CanSeeThrough`), `TargetState` true OR `GetExactState() > 0` (also while closing), destroyed door. NOT `Door.IsOpened` alone. Margin ~4-5 m for humans (estimate: (vR+vT)(RTT/2 + 2/tickrate + 0.05) + 2 m). Room cells are ~15 m, so 36 m is only ~2-3 hops: a 1-hop test hides players in real corridor sightlines. Build the room graph lazily after round start (connectors register in `Start`). |
| Target makes footsteps (or is airborne, landed, or stepped in the last 1 s) | Send to receivers within the footstep radius + margin. Walk/sprint(x2) radii: D, scientist, 3114 skeleton 8/16 m; MTF, guard, Tutorial 12/24 m; CI 30/60 m; SCP-049, zombie, 939, 106 35/70 m; SCP-096 52/104 m; SCP-1507 fixed 30 m; SCP-173 has its own scraping audio (~37 m). A disguised SCP-3114 steps with the stolen role's radius, not 8 m. 939 steps only while sprinting. |
| Target speaks (Proximity, Radio, Scp1576) | Send to receivers within R_voice + margin for 3 s after the last packet. Server routes proximity to 36 m; the prefab plays about 22 m. Whether it is audible beyond 22 m depends on the rolloff curve: UNVERIFIED. |
| Target fires a firearm | Do not rely on a reactive hold. Make the shot RPC always carry the shooter position (prefix on `AudioModule.ServerSend`, see "Shots"), and also hold the shooter's audio radius for 3 s after the shot event. |
| Sound-emitting items (usable 15 m, MicroHID 30/45 m, throw 10 m for 2 s, radio in hand ~13 m, SCP-127 12 m, Jailbird charge) | Send within range for 3 s after each event. Never flip a target visible-to-hidden while its sound plays. |
| Target is not a human role (SCPs, 1507, 3114) | Keep vanilla in stage 1: 173 and 096 observation, 049 Sense, zombie bloodlust are server raycast/visibility mechanics; hiding an SCP they consider visible freezes 173 or makes an honest player a 096 target while his client shows nothing. 049 Sense target stays visible while `HasTarget`. |
| Target in noclip, in a moving elevator, or without room data | Never hide. |
| Pair that damaged each other in the last 3 s | Stay visible (if vanilla passes). |
| Hysteresis | Reveal immediately (the client snaps). Hide only after ~1.0 s of continuous "not needed". |
| Voice reveal lead | A previously hidden speaker loses the first 1-3 packets (~10 ms each) because voice is forwarded before the client processes the reappearance. Reveal speakers early (a "voice gate", ~35 ms added latency, estimate) or accept the clipped start. |

Per-pair raycasts are not the primary mechanism. They are a second stage or a shadow-mode audit only: a hidden pair can cost on the order of 12 Linecasts (estimate: both directions x 3 heights x current + look-ahead point), so a 120-pair cap is up to ~1440 rays/frame (Linecast cost on this map unmeasured; 1-3 us is an estimate). Cheap flat arrays indexed by PlayerId (20-50 ns per pair, estimate) beat dictionaries here.

## Shots

The shot RPC is sent at once; the position stream follows in `LateUpdate`. Deciding "is the shooter needed" at your next sweep (or any event-then-next-tick scheme) therefore never fixes the first shot. Remove the race at the source:

- Harmony prefix on the private `AudioModule.ServerSend(..., bool shooterVisible)` that sets `shooterVisible = false`. The shooter's `RelativePosition` is then always appended (+7 B per shot per receiver). The client reads it only when the shooter model is invisible, and the autosync payload is length-prefixed, so the extra bytes are harmless to a receiver who sees the shooter.
- Then still hold the shooter in the sent set for ~3 s after a shot (prefix on `AudioModule.ServerSendToNearbyPlayers` records the event), so a clip already playing is not re-parented to the parked transform when the shooter flips to hidden mid-sound.
- The shot RPC goes to receivers within `audioRange` + 20 m. Use the event's own `audioRange` (it is per firearm and sound) as the hold radius.

## Portal graph and door state

- **Build:** once per round, lazily after round start (not on map generation: `RoomConnector` registers in `Start`). Portals = `RoomCullingConnection.AllInstances` with a valid link, plus every `DoorVariant` with exactly two rooms and no connection (AABB from its colliders). Keep each portal's 3D AABB and kind (door or `RoomConnector`). Build your own multimap, not `SmallestInstancesByPair`.
- **Validate, then fail open:** every connection has a valid link and every room of a zone has at least one portal. If validation fails or building throws, disable the layer for the round (vanilla result for everyone) and log once. Never skip the one bad door and carry on: a missing portal silently hides players who are in a real sightline.
- **Door state:** a portal is open when `RoomConnector`, or `CanSeeThrough` (EZ glass doors), or `TargetState || GetExactState() > 0`, or the door is destroyed. LabAPI `Door.IsOpened` is `TargetState` only. `TargetState` is a SyncVar that flips at once; `GetExactState()` (LabAPI `Door.ExactState`) follows the animation and is real on a dedicated server.
- **Closing:** on a close action put the door on a "closing" list; every tick close its portal only when `GetExactState() == 0`. Animation lengths (read from animation data, clip lengths not re-verified): EZ/HCZ doors ~1.4 s, LCZ ~1.3 s, HCZ BulkDoor ~5.9 s. A 1 s hide hysteresis plus the 5 m margin does NOT replace this: with `IsOpened` plus 1 s, a target seen through a half-open door is hidden ~0.3 s before a normal door is shut and ~4.9 s before a BulkDoor is shut.
- **Opening:** react to `TargetState` (open the portal at once). No prediction is needed: the door is server-authoritative and the position entry reaches the client before its door animation starts.
- **Distance D_room:** graph nodes are open portals; an edge joins two portals that share a room; its weight is the MINIMUM distance between the two portal AABBs, not centre to centre. D_room(A, B) = 0 when A == B or an open portal joins them directly, else the shortest path from any portal of A to any portal of B (multi-source Dijkstra, cut at R + margin ~41 m), infinity if none. This is a true lower bound because a sight line between consecutive portal crossings joins points of those AABBs. Centre-to-centre is not: portals are 1.4 m doors up to 6 m BulkDoors, so centre paths run several metres longer over 2-3 hops and hide real sightlines. Recompute only when the open-portal set changes, at most once per tick.

## Position leaks outside the position stream

Omitting a target from `FpcPositionMessage` hides only that stream. These carry the real position too:

- `RoleSyncInfo.Write` always appends the target's **real** role's public spawn data (for FPC roles a `RelativePosition` plus rotation), even when the visible role was spoofed to Spectator/Overwatch. It is sent on every role change to everyone (`PlayerRoleManager.SendNewRoleInfo`), from `WriteAll` whenever the per-pair visible role changes (for example at the 50 m / 110 m boundary) and, via `RoleSyncInfoPack`, on join with the real roles of all non-obfuscated players. `RoleSyncInfoPack` bypasses `GetVisibleRole` and `RoleSyncEvent`.
- The client applies the position for a real role, ignores it when `WaypointId == 0`, and does not apply it for a spoofed Spectator (raw bytes still leak to a packet-reading cheat, not to one that reads Unity objects).
- Fix: patch `FpcStandardRoleBase.WritePublicSpawnData` for pairs the layer hides, writing `WaypointId = 0` (1 B) and rotation, with the (receiver, target) pair set in a thread-static context by a prefix on `RoleSyncInfo.Write`. Covers `SendRole`, `SendNewRoleInfo` and `RoleSyncInfoPack`. Sanitize **only** pairs actually hidden: a wrongly hidden newly-spawned model otherwise sits at the default spot until the next position tick (<= 16 ms).
- Other leaks: `LastHumanTracker` (position of the last human to everyone), SCP-096 target list, SCP-3114 status, `TransmitterPositionMessage` per transmitting radio.
- Limit: nothing here stops a cheat that reads memory.

## Rollout

1. Shadow mode first: compute, do not apply. Log the hidden-pair percentage by reason and zone, toggles per pair per minute, graph validation result, audibility gaps (every footstep, voice packet, `AudioModule` event or throw whose emitter would be hidden from a receiver inside that sound's range), and the false-negative audit below. The top risk is an invisible enemy in a real sightline (windows, grates, galleries outside the portal graph).
   - **The audit must be live in shadow mode, never off by default.** With no mask or no rays, shadow mode produces no false-negative signal and the gate "audit clear-ray ratio near zero" cannot be evaluated.
   - Per tick, sample ~20 "to hide" pairs. Cast multi-point rays (head, chest, feet of both players) in both directions with `VisionInformation.VisionLayerMask` (Door + Default). EZ door glass sits on a layer outside that mask, so windows do not block the ray, which is the point. A single eye-to-eye ray misses corner peeks.
   - Log every clear ray with both rooms and both positions.
   - Gate to leave shadow mode: 0 clear rays, 0 audibility gaps, no pair toggling more than once per second.
2. Reset per-player caches on join/leave and round boundaries (PlayerIds are recycled). Unknown ids fail open.
3. Check for other patches on the same methods (UCR, UCI, EXILED GhostMode patch the distributor and `PlayerEvents.On*`); patches have no dedup, and an exception in a per-pair patch stops position sync for everyone. Re-verify with `GetPatchInfo` at lifecycle points.
4. Canary with an on/off switch in config, A/B rounds, one week without regression before full enable.

## Re-verifying on a new build

- Decompile and check that omitted targets still get `IsInvisible` and the (0, 6000, 0) park (`FpcPositionMessage`, `FpcMotor`).
- `AudioModule` gunshot path: still `shooterVisible` from the vanilla check, position appended only when vanilla-invisible, and `ServerSend` still has the `shooterVisible` parameter your prefix overrides.
- Door state: `GetExactState` still follows the animation on a dedicated server (animators still always-animate) and animation lengths still match.
- `RoleSyncInfo.Write` still calls `WritePublicSpawnData` of the real role; `RoleSyncInfoPack` still bypasses `GetVisibleRole`.
- Footstep distances per role and the voice/item radii (they are role and prefab data).
- Test with a vanilla client: hidden player's footsteps, shots and voice at the radii above.
