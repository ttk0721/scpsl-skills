---
name: scpsl-networking-for-plugins
description: "Use when an SCP:SL LabAPI plugin spawns or moves AdminToys, pickups or schematics, sends hints/broadcasts/audio to many players, adds SyncVar/RPC traffic, or hides players (anti-ESP, ghost, invisibility); or when you see bandwidth spikes, join lag, rubber-banding after a map load, or hidden players going silent. Mirror, ValidatedVisibility, IsStatic, SyncInterval."
---

# SCP:SL networking for plugin authors

Verified against LabAPI 1.1.7 / SCP:SL 14.2.7 (2026-10). Code reading, not packet captures; estimates are marked.

## Overview

Every plugin action is a Mirror message in one reliable per-connection stream shared with positions and voice. Almost no interest management: spawns and ClientRpcs reach everyone. Cost is players x objects x frequency; cut the multiplier.

## When to Use

- Spawning, moving, despawning toys, pickups, schematics; scale or teleport calls.
- Per-frame hints, broadcasts, console text, plugin audio; per-player visuals; fake SyncVars; hiding players.
- Small change (one toy, one hint): apply only the matching rule below. No process, no test server.
- NOT for hint layout (scpsl-hints-and-hud), voice routing (scpsl-voice-and-audio), event cost (labapi-events), Harmony (scpsl-harmony-patching), CPU budgets (scpsl-engine-cost-model).

Details: mistakes-detail.md, wire-cost-table.md, mirror-mechanics.md, toys-and-spawning.md, hiding-players.md, networking-examples.md.

## Rules

1. Every spawned object is a recurring cost; budget identities per round.
2. Spawn/despawn in byte-sized slices (slice x ~134 B x players per frame).
3. `IsStatic = true` on anything that does not move. To move one, go non-static and stay so until the update was sent (>= `SyncInterval`), applied and smoothed.
4. Write a SyncVar only on real change (exact equality). SyncList ops and RPCs do not coalesce.
5. No per-observer SyncVars via Mirror. Use `RoleSyncEvent`, `Connection.Send` of an existing message type, or a hand-built EntityState/Rpc.
6. Never define a `NetworkMessage`. Use hints, console, Server Specific Settings or existing RPCs.
7. Never refresh a hint or broadcast every frame: diff, rate-limit, narrow the audience.
8. Plugin audio: set `ValidPlayers` (O(1) predicate, refreshed <= 10 Hz).
9. Hide a player only with the full send-set rules in hiding-players.md: shots (force the position in), door state (`ExactState`, not `IsOpened`), validated portal graph, live shadow-mode audit.
10. Main thread only for `Connection.Send`, `Spawn`, `Destroy` (scpsl-main-thread-and-timing).
11. Keep unreliable payloads under ~1000 B (MTU likely 1024, unverified).

## Common Mistakes

| Wrong | Right |
|---|---|
| Anti-ESP from line of sight plus a 12 m radius, "sound is not affected". | Omission silences footsteps, voice, gunshots. Send PVS union audibility radius, add a voice reveal lead, force the shot position in. |
| Keeping a shooter visible after a shot event is seen by the next sweep or tick. | A hidden shooter's FIRST shot is silent. Harmony prefix on `AudioModule.ServerSend` forcing `shooterVisible = false`. |
| Door open from `Door.IsOpened` (target state) plus hysteresis "covering" the close animation. | Portal open = `CanSeeThrough \|\| TargetState \|\| GetExactState() > 0`; close only at `GetExactState() == 0`. |
| Room graph with door-centre distances, unregistered doors silently skipped, only `n < 10` sanity checks. | Edge weight is min AABB-to-AABB portal distance. Validate every link and zone-room portal; on failure disable the layer for the round (fail open). |
| Shadow mode whose audit is off by default (mask 0) or one eye-to-eye ray. | Audit ~20 to-hide pairs per tick: head/chest/feet both ways, `VisionInformation.VisionLayerMask`, log every clear ray. |
| Grace and look-ahead for a "frozen" hidden target. | No freeze: instant park, snap back; look-ahead covers latency only. Never hide noclip targets. |
| Assuming omission from `FpcPositionMessage` removes the position. | `RoleSyncInfo` (incl. join-time `RoleSyncInfoPack`) carries the real position even when the role is spoofed. Sanitize hidden pairs (WaypointId 0, 1 B). |
| Hiding through `ValidatedVisibility` alone. | Voice and gunshots use the vanilla check: vanilla-visible but absent on the client. |
| Re-freezing a moved toy next frame. | Wait for send, apply and smoothing (rule 3). |
| Subscribing to hot visibility/voice events "because it is cheap". | Args plus two `Player.Get` allocate per pair per frame even unsubscribed. Answer from a cache. |

## Examples

`examples/networking-snippets/` (compile and lint only): `ToySpawnQueue` (byte-budgeted slices), `ToyMover` (thaw, move, freeze after settle), `FakeScale` (per-viewer scale), `GhostGate` (hide, audio caveats). Key parts: networking-examples.md. To try them on a server, follow running-a-test-server: ask first, own port, never the owner's.

## Related

scpsl-roles-damage-effects, measuring-scpsl-performance. scpsl-lint: SL012, SL011, SL016.
