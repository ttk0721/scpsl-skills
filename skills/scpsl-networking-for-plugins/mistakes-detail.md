# Networking: full rationale for rules and Common Mistakes

Moved out of SKILL.md to save tokens. SKILL.md keeps the short rule or row; this file keeps the full wording, numbers and reasons.

## Quick Reference (wire cost per player)

| Action | Wire per player |
|---|---|
| Spawn 1 toy | ~134 B once, synchronous, O(players) |
| Idle toy, static or not | 0 B/s; static still costs a per-tick server walk |
| Moving primitive | 48-76 B per delivered update (~0.5-0.8 KB/s at interval 0.1, estimate) |
| Move a static toy | 0 B, silently ignored |
| `Player.Scale` set | ~19 B to everyone, no coalescing |
| Server-wide RPC | N repacks, same order as N targeted sends |
| Plugin audio | up to ~15 KB/s per listener |
| Custom `NetworkMessage` | client disconnects |

## Rule details

- Rule 2: slice x ~134 B x players per frame (50 toys x 100 players is ~670 KB in one frame).
- Rule 3: to move a static toy, go non-static and stay non-static until the update was sent (>= `SyncInterval`), applied and smoothed, or move a non-static parent.
- Rule 4: exact equality, no epsilon. Writes inside one `syncInterval` coalesce; SyncList ops and RPCs do not.
- Rule 5: per-observer values via `RoleSyncEvent`, `Connection.Send` of an existing message type, or a hand-built EntityState/Rpc (best effort; re-send on join and role change).
- Rule 6: carry data in hints, console, Server Specific Settings or existing RPCs.
- Rule 8: `ValidPlayers` must be an O(1) predicate, refreshed <= 10 Hz.
- Rule 9: full send-set rules are in hiding-players.md: shots (force the position in), door state (`ExactState`, not `IsOpened`), validated portal graph, live shadow-mode audit.
- Rule 11: peer MTU most likely 1024, unverified.

## Common Mistakes, full rows

| Wrong | Right |
|---|---|
| Anti-ESP from line of sight plus a 12 m radius, "sound is not affected". | Omission parks the model at (0, 6000, 0) on the client: footsteps (8-52 m by role, doubled sprinting), proximity voice (36 m) and gunshot audio go silent. Send PVS union audibility radius, add a voice reveal lead, and force the shot position in (next row). |
| Keeping a shooter visible after a shot event is seen by the next sweep or tick. | The shot RPC leaves at once, the position stream only in `LateUpdate`. A hidden shooter's FIRST shot is silent (parked at (0, 6000, 0), volume 0). Harmony prefix on `AudioModule.ServerSend` forcing `shooterVisible = false` makes the RPC always carry the position; the hold only covers a clip already playing. |
| Treating a door as open from `Door.IsOpened` (target state) with hysteresis "covering" the close animation. | `TargetState` flips at once, `GetExactState()` follows the animation (~1.3-1.4 s doors, ~5.9 s HCZ BulkDoor). Portal open = `CanSeeThrough \|\| TargetState \|\| GetExactState() > 0`; close it only at `GetExactState() == 0`. IsOpened plus 1 s hides a target ~0.3 s (BulkDoor ~4.9 s) before the door is shut. |
| Room graph with door centres as distances, doors that fail to register silently skipped, only `n < 10` sanity checks. | Edge weight is the minimum AABB-to-AABB distance of portals (centres are not a lower bound). Validate every link and that every zone room has a portal; on failure or exception disable the layer for the round (fail open). |
| Shadow mode whose audit is off by default (mask 0) or one eye-to-eye ray. | No false-negative signal, so the rollout gate is unevaluable. Audit ~20 to-hide pairs per tick: head/chest/feet both directions, `VisionInformation.VisionLayerMask`, log every clear ray. |
| Designing grace and look-ahead for a "frozen" hidden target. | No freeze: instant park, instant snap back; look-ahead covers latency only. Noclip targets freeze visible; never hide them. |
| Assuming omission from `FpcPositionMessage` removes the position. | `RoleSyncInfo` (role change, role-visibility change, join) always carries the real role's spawn data with a `RelativePosition`, even when the visible role is spoofed; the join-time `RoleSyncInfoPack` also bypasses `GetVisibleRole` and `RoleSyncEvent`. Sanitize hidden pairs (WaypointId 0, 1 B). |
| Hiding through `ValidatedVisibility` alone. | Voice and gunshots use the vanilla check, so the target is vanilla-visible but absent on the client. |
| Re-freezing a moved toy next frame. | Wait for send, apply and smoothing (rule 3). |
| Subscribing to hot visibility/voice events "because it is cheap". | The args object plus two `Player.Get` are allocated per pair per frame even with zero subscribers; a subscriber adds `GetInvocationList`. Answer from a per-frame cache. |

## Re-verifying on a new build

- Re-measure the Quick Reference sizes with a packet capture or a counting transport on a test server: spawn of one toy (~134 B), a moving primitive (48-76 B per update), `Player.Scale` (~19 B to everyone), audio (up to ~15 KB/s per listener). The byte counts are the part most likely to move with a Mirror or game update.
- Re-check that a custom `NetworkMessage` still disconnects the client, and that a static toy still ignores movement (rule 3).
- Re-check the omission behaviour behind the anti-ESP rows: that an omitted player is parked far away on the client and footsteps, proximity voice and gunshot audio then go silent, and that the footstep and voice ranges still match the figures in the rows.
- Re-check that `RoleSyncInfo` and the join-time pack still carry the real role's position data when the visible role is spoofed, and that they still bypass `GetVisibleRole` and `RoleSyncEvent`.
- Re-check the door rows: `TargetState` vs the exact animated state, and the close durations (about 1.3-1.4 s for most doors, about 5.9 s for the HCZ bulk door).
- Re-check that the hot visibility and voice events still allocate their arguments per pair per frame with zero subscribers.
- Rule 11 (peer MTU, most likely 1024) stays unverified until measured on the wire.
