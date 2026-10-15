# Tick budget at 40-100 players

Verified against LabAPI 1.1.7 / SCP:SL 14.2.7 (2026-10).

Tags: **[M]** measured on a real server, **[C]** counted or read from code (rates, pair counts, structure), **[E]** estimate (time costs reasoned from code or a microbenchmark), **[U]** unverified. Untagged time figures are [E]. All figures assume a vanilla map unless stated. No full-load live profile exists, so treat every [E] as a planning number and measure (see measuring-scpsl-performance).

## 1. Frame budgets and pair counts

| TPS target | Frame budget |
|---|---|
| 60 | 16.7 ms |
| 30 | 33 ms |
| 20 | 50 ms |

Pair counts for the position path are N x (N-1), counting dummies as receivers and targets [C]. Time uses the vanilla estimate of 0.35-0.7 us per pair (arithmetic on that estimate):

| Players | Pairs per send | Time per send |
|---|---|---|
| 40 | 1,560 | 0.5-1.1 ms |
| 60 | 3,540 | 1.2-2.5 ms |
| 80 | 6,320 | 2.2-4.4 ms |
| 100 | 9,900 | 3.5-6.9 ms |

Doubling the player count roughly quadruples this term. In a typical mid-round the real count is lower, about 6,000-8,000 at 100 players, because not every hub is an FPC target [E].

Measured on one desktop (Ryzen 9 9950X3D, 14.2.7, alive dummies plus one real client on the same
machine, sl-probe busy time) [M]:

| Players | Real frame rate | Busy per frame, mean | Mirror update, mean |
|---|---|---|---|
| 61 | 46.7 fps | 21 ms | 16 ms |
| 121 | 6.8 fps | 147 ms | 145 ms |
| 241 | 3.7 fps | 269 ms | 276 ms |

- Past about 100 players the network side falls off a cliff: from 61 to 121 players the Mirror update
  cost grew about ninefold. Measure at the real peak count; extrapolating from a quiet server is useless.
- Spreading the players over the map does not change the measured cost at 241 (role spawn points all over the
  facility, same numbers): every pair is processed whatever the distance, so position is not a lever.
- `Server.Tps` reads 50 while the server runs at 3.7 fps.

## 2. Position path: O(N^2) every frame

- `FpcServerPositionDistributor` sends one `FpcPositionMessage` per receiver per send. Send rate is `1 / clamp(server_tickrate, 10, 60)` from the configured value, so when frames run at or below the configured rate it runs every frame [C] (see section 9 for when they do not).
- The message serializes lazily inside the send, so `WriteAll` runs for every receiver, dummies included. A dummy's send is a no-op, but the work is already done [C].
- Per pair inside `WriteAll` [C]: a visibility check (only for receivers with a custom visibility role), a `PlayerValidatedVisibilityEventArgs` plus event dispatch, a sync-data computation with a nested-dictionary write, a visible-role computation (which repeats the visibility check), a pooled writer, the role-sync hook, and a role send only when the per-pair role changed.
- Vanilla cost: 0.35-0.7 us per pair, 3-7 ms per frame at 100 players, about 600k allocations/s at 60 TPS (about 200k at 20 TPS) [E].
- Lowering the frame rate that really applies does not shrink the per-frame cost. It only lowers the per-second cost, because there is still one send per frame [C].
- Receivers without a custom visibility role (spectators, overwatch) are never culled and get all N-1 entries every send, about 38 KB/s at 90 targets [E, from the entry sizes]. Entries cost 2-13 B each, 0 B when the target is omitted [C].
- Vanilla range cull: players beyond about 36 m (70 m for an observer on the surface) are omitted [C].
- Plugin load in the same loop (popular community stacks, [E]): an event-prefix patch on the visibility event invoker 5.9 ms/frame at 0 custom roles up to 17 ms/frame at 15 roles in an isolated microbenchmark [M], but whether it actually runs inside `WriteAll` is [U] (the invoker is 12 B of IL and may be inlined); the EXILED ghost-mode transpiler 0.8-2 ms/frame; a prefix on the `CurrentRole` getter about 2 ms/frame (+17 ns per read [M], about 13.7 reads per pair [E]).
- A plugin hook per pair costs `pairs x ns`: 100 ns per pair is about 1 ms per frame at 100 players. See plugin-action-costs.md for the budget.

## 3. Mirror Broadcast: O(P x O) every tick

- Every tick, for each ready connection, Mirror walks every identity the connection observes and asks it to serialize. Serialization is cached per identity per frame, but the walk and the checks run for every pair even when nothing is dirty [C].
- Connections include the host loopback (the server runs as a host), so the count is P + 1. Dummies are not in the connection list, so they are not part of this term. The host hub is not part of the position term [C].
- Interest management is effectively "everyone sees everything" (a custom interest manager that returns true, probably not even attached [U]). Every spawned identity goes to every ready connection, and every `ClientRpc` goes to everyone [C].
- O in vanilla at 1 player: 481-628 networked identities [M]. At 100 players O is estimated at 700-1,000 (player hubs, doors, pickups, lights, lockers, ragdolls) [E]; the real number is [U] until you log `NetworkServer.spawned.Count`.
- Cost per pair about 20-40 ns [E]. At 100 players that is about 3 us per tick per extra identity (101 x ~30 ns, arithmetic). Vanilla: 1.4-4 ms per tick at O 700-1,000 (about 85-240 ms/s at 60 TPS, 28-80 ms/s at 20 TPS). Map-editor schematics: 10k objects at least 23-46 ms per tick, 25k objects about 50-100 ms (arithmetic on the same per-pair estimate, not measured). At 60 TPS that is unreachable; at 20 TPS Broadcast alone can eat most or all of the frame.
- Static toys cost nothing on the wire but still pay this walk. `IsStatic` only removes the per-frame server `LateUpdate` of the toy (see plugin-action-costs.md).
- Dirty tracking: a SyncVar setter marks dirty only when the value changes under `EqualityComparer<T>.Default` (exact comparison, no epsilon for `Vector3`). Throttled components (`syncInterval > 0`) coalesce writes into one message per interval; SyncList operations and RPCs never coalesce [C].
- Send rate versus tick: `NetworkServer.tickRate` is 60 in the scene data [C], so Broadcast runs every frame at 60 TPS or below.

## 4. Per-frame callbacks

- The player prefab alone has about 89 active Update/LateUpdate methods (about 70 are status effects that are usually inactive, plus 6 HDRP volumes), about 8.9k calls per frame at 100 players [C]. With models, voice playbacks, toys and ragdolls the total is about 10-15k callbacks per frame, 1-3 ms [E].
- Per-frame callbacks cost mostly the native-to-managed transition, so disabling a component (`enabled = false`) is the only way to remove it. A Harmony prefix does not remove the transition [C].
- Ragdolls never freeze on the server (the freeze setting resolves to off), so each human corpse keeps about 3 callbacks per frame (0.1-0.2 us each) until the round ends; a sleeping one is cheap, an awake one also runs a bone-matching pass for a few seconds [C]. Each human death also clones a visual model (about 686 GameObjects), roughly 0.2-3 ms per death (estimates disagree) [E].
- Pickups register individually in static update multicasts, so adding and removing them is O(n) [C].
- Non-static toys run a server `LateUpdate` per frame (about 0.3-0.5 us each [E]): 4,000 still but non-static toys cost 0.6-1.4 ms/frame [E].

## 5. Animator, movement, physics

- The server runs a manual `Animator.Update` with IK for every humanoid model, throttled by distance and a 13 Hz floor: about 1,500 per second at 60 TPS and 1,000 per second at 20 TPS at 100 players [C]. At an assumed 30-150 us each that is 45-225 ms/s at 60 TPS and 30-150 ms/s at 20 TPS. The unit cost is unmeasured [U]. Hitbox poses, footsteps and SCP-939 detection depend on it, so it cannot be thinned without changing gameplay.
- The movement loop runs `CharacterController.Move`, a downward `SphereCast` (also for standing players) and about 2,000 static hitbox collider pose syncs per frame, about 1-3 ms [E].
- Physics is a fixed 50 Hz step independent of TPS, maximum delta time 0.333 s [C]. Steps per frame: 0.83 at 60 TPS, 1.67 at 30, 2.5 at 20, up to about 16 after a 333 ms hitch. Managed work per step is about 0.1-0.2 ms (5-10 ms/s) [E]; PhysX step cost is [U].
- Do not change `Time.fixedDeltaTime`. The movement tracer assumes 0.02 s and above 0.025 s the backtrack sample count becomes zero and logs an error on every SCP attack [C]. Do not turn off `Physics.autoSyncTransforms` globally: lag-compensated shots move transforms and query immediately. Do not change `queriesHitTriggers` globally.
- A "death spiral" from the 16-step catch-up needs a step cost of 20 ms or more, about 100x the managed part, so it is unlikely [E].

## 6. Voice

- Rate: 480 samples at 48 kHz means 100 packets/s per speaker, independent of TPS [C].
- For each packet the server loops over every hub: a receive check, an args allocation (even with no subscriber), and a separate pack and send per receiver. Cost is O(speakers x receivers) [C].
- Vanilla: 35-130 ms/s at 10-20 speakers, about 70-130 ms/s and 12.8 MB/s of garbage at 20 speakers [E]. With a heavy plugin stack (event prefixes, per-event handlers) 120-275 ms/s at 10 speakers, which is 6-14 ms per frame at 20 TPS [E].
- Proximity goes only to receivers within 36 m (70 m on the surface). Radio and intercom go to everyone. Plugin audio (`AudioTransmitter`) goes to all authenticated players unless `ValidPlayers` is set [C].
- Bandwidth: 120 kbps encoder target is about 150 B per frame, up to about 15 KB/s per speaker-receiver pair (planning figure; real frame sizes [U]). If all 101 hubs received 10 speakers, that is 76-126 Mbit/s [E]. Uplink can be a harder limit than CPU. Voice rides the same reliable stream as game state, so a large burst delays voice and positions together (see scpsl-networking-for-plugins).
- A patch that removes per-receiver args allocation and repacking saves 40-60% of voice CPU but not bandwidth [E].

## 7. GC and allocation

- Boehm collector, incremental with 3 ms slices (boot config) [C]. No parallel-mark support was found in the library, so marking is presumably single-threaded over the whole live heap; Unity documents it as non-generational, and whether the incremental mode does partial collections is [U].
- A cycle starts after about 0.33 x live heap bytes are allocated (upstream formula with incremental mode, divisor 3) [E]. If a cycle cannot finish incrementally it ends stop-the-world.
- The game calls `GC.Collect` only at scene load and before a round restart. Do not add more (scpsl-lint SL010).
- Biggest vanilla sources [E]: visibility args per pair (14-24 MB/s at 60 TPS, 5-8 MB/s at 20 TPS), per-packet transport closures (1-5 MB/s), voice args, closures in weapon code. Vanilla total is about 30-45 MB/s, a few MB/s after perfect patches.
- Tuning the collector with environment variables (free-space divisor, initial heap) is [U]; the binary appears to read them, nobody has tested the effect. More free space means more RAM per instance.

## 8. Mass-event spikes

Each spike is one frame (or a few) that blows the budget. A plugin that does work on the same event lands on top of it. All [E] unless noted.

| Event | Frame cost | Main contributors |
|---|---|---|
| Warhead detonation | 45-160 ms at 30-40 victims, up to ~300 ms at 80 | all deaths in one loop; 250-520 pickup and ragdoll spawns, each serialized per observer (25-130 ms); ragdoll model clones; new-spectator snapshots O(N^2) (16-36 ms). The parts come from separate estimates and overlap, so plan with the frame total. Afterwards every new pickup and ragdoll adds to the Broadcast walk |
| Round start at 100 players | 60-180 ms | role assignment in one frame; first round after process start adds 65-325 ms (pools grow, ~40k GameObjects) |
| Map schematic load on a full server | seconds | 25k objects took 3.1 s at 1 player [M]; every spawn is serialized per connection |
| Player join | ~5 ms main thread for key exchange and signature checks [M], plus ban-file parse, plus a full spawn replay (O identities) | wave-state sync sends four messages to everyone on every join and death |
| Hint storm from a HUD plugin refreshing everyone on each spawn or role change | 40-80 ms at round start, 10-20 ms on mass death | about 20k hint messages in one frame, plus as many MEC coroutines when the EXILED hint postfix is installed |
| Periodic synchronous HTTP on the main thread (a plugin's telemetry) | 35-50 ms every minute [M], up to 15 s on network failure | blocking `PostAsync().GetResult()` |
| Full GC | unmeasured [U] | after big allocation bursts |

Round restart is a scene reload (see scpsl-game-lifecycle): hubs are destroyed and recreated, so per-session work (60 flushes, 60 HUD starts) lands in the first frames of the next round.

## 9. Tickrate semantics

- `Server.MaxTps` is the configured `server_tickrate`; the config load writes it to `Application.targetFrameRate`, and Mirror's `StartHost` then resets that to the scene send rate of 60 [C]. The target actually in force is `Application.targetFrameRate`, so read that for alerts.
- The configured value comes back after the first idle-mode exit or a config reload. With `idle_mode_enabled` on (default), any server that emptied once runs at the configured value afterwards; a server that never idles, or one that stays full, effectively runs at 60 whatever it is set to [C].
- A plugin that sets the target in its `Enable` is overwritten by both the config load and `StartHost`. Apply it from `WaitingForPlayers`, outside idle mode, and re-apply after a config reload. The standalone `tools/tickrate-fix` plugin does this for the server owner (only `-1` and `0` become its `cap`; a larger value the owner set is kept, and the plugin is off until the owner enables it); a plugin should not do it as a side effect of its own.
- Pitfalls: `server_tickrate` of 120 or 144 does nothing until the first idle exit or config reload, and then doubles the CPU target, so cap it. `-1` means uncapped (one core at 100%) and clamps position send rate to 10 Hz. A configured 30 with frames running at 60 sends positions at 30 Hz while everything else runs at 60 Hz.
- Idle mode: `timeScale` 0.01 and about 1 FPS when the server is empty. `Server.Tps` is meaningless there, and Update-segment MEC waits stretch about 100x. Treat idle as "no data" (scpsl-main-thread-and-timing).
- `Server.Tps` and the `tps` command show `round(1 / smoothDeltaTime)`: smoothed, clamped by the maximum delta time, and scaled by `timeScale`. It hides hitches and cannot read much below 3.

## 10. What patches and plugins cannot fix

Exact (byte-for-byte) patches of the hot paths are estimated to cut the clean-game steady state by 30-45% of main-thread time (position path 3-7 ms to 1-2 ms, Broadcast to 0.1-0.3 ms, voice -40-60%, callbacks -0.5-1.5 ms, garbage 30-45 MB/s to a few MB/s), and a plugin-heavy server by 50-70%. Mass-event spikes shrink 40-70%. What stays [E]:

1. Animator and IK (hitboxes, footsteps and 939 detection depend on them).
2. The 50 Hz PhysX step, `CharacterController.Move`, the hitbox pose syncs.
3. The native-to-managed transition for components that cannot be disabled.
4. The protocol O(N^2): every receiver must get a position message with all visible targets, because the client marks missing targets invisible. Voice is O(speakers x receivers) sends.
5. One main thread. Unity, Mirror and LabAPI cannot be parallelized from a plugin.
6. The Boehm collector. You can allocate less and collect less often, nothing else.
7. Mass-event spikes, which can only be shortened, not removed, without changing gameplay.
8. Harmony dispatch cost: every prefix adds a few ns per call, so patches on methods called millions of times per second must be transpilers or in-place swaps.

## 11. Realistic TPS targets

- **60 TPS at 80-100 players: not realistic**, even after all exact patches. Position path, callbacks, movement loop and animator together are 8-20 ms of a 16.7 ms budget, with no room for GC or spikes [E].
- **30 TPS (33 ms): plausible in steady state after patches**, vanilla map, moderate speakers. No headroom for spikes [E].
- **20 TPS (50 ms): realistic in steady state** if all hold: the tickrate override actually applies; plugin overhead on hot paths is neutralised; O is at most about 2-3k (or the Broadcast walk is patched) and map schematics are static; the number of simultaneous speakers is reasonable and uplink is at least 150-200 Mbit/s; measurement confirms animator update at or under 150 us, PhysX step at or under 3 ms [E, conditional].
- **Estimated frame at 100 players, 20 TPS target:** clean game 15-35 ms (fits); a server with a heavy custom-role, custom-item and EXILED stack 30-65 ms (borderline or over) [E]. Plugin overhead on hot paths is comparable to or larger than all of the game's own hot spots on such a server [E].
- **Define "stable" as** p95 frame <= 50 ms, p99 <= 80 ms, frames over 100 ms only at mass events. Warhead, round start, schematic load and full GC will still exceed 50 ms for single frames.
- At 20 TPS positions go out at 20 Hz, Broadcast at 20 Hz, animation at about 10 Hz. The vanilla client copes (idle mode and low-tickrate servers work the same way), but players feel more input delay; the 0.4 s hit backtrack covers it.
- Alerting: compare real frame rate with `Application.targetFrameRate` (ratio), never a literal 30. See plugin-action-costs.md for a sketch.

## Re-verifying on a new build

- Log at `WaitingForPlayers` and once per minute: `NetworkServer.spawned.Count` (O), the connection count, `Application.targetFrameRate`, `Server.MaxTps`, `Time.fixedDeltaTime`, `Time.maximumDeltaTime`, `Physics.autoSyncTransforms`, `NetworkServer.actualTickRate`, `NetworkServer.fullUpdateDuration.average`.
- Recount per-prefab callbacks with reflection over the player prefab (Update/LateUpdate/FixedUpdate methods on active components) and compare with 89.
- Check that `PlayerEvents.ValidatedVisibility` and the voice events are still raised per pair and per listener and that args are still built with no subscriber (a cheap allocation counter around a no-subscriber run).
- Check the hot-event list in the event catalog (labapi-events) against the new LabAPI.
- Confirm the send-rate clamp (10-60) and whether `StartHost` still resets the frame target (log `Application.targetFrameRate` before and after the server starts).
- Time the position path with a sampled Stopwatch pair around `WriteAll` (a Harmony prefix and postfix, so shadow it behind a flag) and the whole frame with `FrameProfiler` from SlKit at 60 and 100 (dummy) players; replace the [E] in sections 1-2 with [M] if you can.
- Boot config GC settings (`gc-max-time-slice`) and the collector (Boehm versus a newer one) change section 7.
