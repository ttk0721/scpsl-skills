# Evidence status: what is measured, counted, guessed

Verified against LabAPI 1.1.7 / SCP:SL 14.2.7 (2026-10).

The numbers in this skill come from reading the game code, from a handful of microbenchmarks, and from server logs at one player. There is no profile of a full server. Use this file to decide how hard to lean on a figure, and what to measure to replace it.

Tags: **[M]** measured, **[C]** counted or read from code, **[E]** estimate, **[U]** unverified.

## Measured [M]

| Figure | What was measured |
|---|---|
| 481-628 networked identities at 1 player, vanilla map | live count through a probe plugin |
| 25k-object schematic loaded in 3.1 s | same probe, 1 player |
| Join crypto about 5.3 ms on the main thread | stopwatch around key exchange and signature checks |
| Synchronous telemetry POST: 35-50 ms stall each minute, up to 15 s on network failure | logs and frame-time CSV |
| Custom-role plugin's event prefix: 5.9 ms/frame at 0 roles, 17 ms/frame at 15 roles | isolated microbenchmark on Mono 6.12; whether the prefix runs inside `WriteAll` is [U] |
| Prefix overhead on `CurrentRole` read +17 ns, `GetRoleId` +10 ns | isolated microbenchmark |
| 5,145 log warnings in one second | count in a server log (the CPU cost per line is [E]) |

## Counted from code [C]

Pair counts N x (N-1); 100 voice packets/s per speaker; position send rate `1 / clamp(tickrate, 10, 60)` and Broadcast every frame at or below 60 TPS; fixed 50 Hz physics step and 0.333 s maximum delta time; about 89 Update/LateUpdate methods on the player prefab; wire sizes (133 B per toy spawn, 48-76 B per toy update, 6 B per despawn, 2-13 B per position entry); who is in which loop (dummies in `WriteAll` only, host loopback in Broadcast only); which events build args with no subscriber; `GetInvocationList` only with a subscriber; Boehm collector with 3 ms incremental slices; exact equality for sync-var change detection.

## Estimates [E] (reasoned, not timed)

Per-pair cost (0.35-0.7 us), per-pair-in-Broadcast cost (20-40 ns), the ms/frame totals derived from them, garbage rates, voice CPU, per-callback costs (0.05-0.15 us), per-toy `LateUpdate` (0.3-0.5 us), per-log-line cost (5-30 us), spike frame costs (warhead, round start), GC cycle spacing, the "clean game 15-35 ms, heavy stack 30-65 ms" frame at 100 players, and the whole target section. Treat each as plus or minus a factor of two.

## Unverified [U]

- Per-step PhysX cost, `CharacterController.Move` cost, one humanoid `Animator.Update` with IK. These are the three biggest unmeasured items in the budget.
- `NetworkServer.spawned.Count` at 100 players on your maps (vanilla estimate 700-1,000).
- Whether the visibility-event prefix of a popular custom-role plugin runs inside `WriteAll` (inlining of the 12 B invoker).
- Whether the interest-management object is null at runtime (scene scan only).
- Prefab `SyncInterval` defaults for toys (0.1 for primitives, 0 for text, speaker and similar) came from an asset scan, not code.
- Effective peer MTU is most likely 1024 (discovery never enabled), so unreliable messages are capped near 1,013 B; log the first peer's MTU to confirm.
- Real Opus frame sizes (the 120 kbps figure is an average target) and real simultaneous speakers.
- Exact hint byte sizes on the wire. (Client hint behaviour, one slot with no queue, was measured: scpsl-hints-and-hud.)
- Whether the Boehm environment variables (free-space divisor, initial heap) are honoured and what they buy; whether incremental mode does partial collections.
- Cost of one caught handler exception end to end (10-100+ us is a bracket) and of one `Logger` call.
- Whether `Quaternion` sync vars compare exactly like `Vector3` (the `Vector3` case was checked).
- Linecast cost on your map (1-3 us is a guess).
- A full-load frame profile (none exists).

## Common misconceptions

- The args object is built per raise even with no subscriber, but `GetInvocationList()` allocates only when at least one handler is subscribed. 
- "`server_tickrate` never takes effect" is true only for servers with idle off or never empty: it applies after the first idle exit or config reload (default `idle_mode_enabled` is on).
- The effective MTU is most likely 1024, not 1432 (see above).
- Boehm cycle trigger is about 0.33 x live heap in incremental mode (not 0.67).
- The 50 Hz fixed step is not a "death spiral" at realistic step costs; the cost is steady, per step, independent of TPS.
- Dummies are receivers and targets in `WriteAll` but are not in the Broadcast connection list.
- Voice is 100 packets/s per speaker. The frame size gives 100.
- The cost of a log-warning storm is the formatting and native write per line, not regex passes: the regexes apply to the LabAPI `Logger` path (console formatter), not to `Debug.Log`.

## How to replace an estimate with a measurement

1. Install `FrameProfiler` from SlKit: wall-clock frame period and busy time at the frame boundaries, p50/p95/p99/max, share of frames over 50 and 100 ms, CSV once per second with event marks. Compare with Mirror's `actualTickRate` and `fullUpdateDuration.average`; keep `Server.Tps` only as a labelled column.
2. Use the `sl-probe` plugin for whole-frame timing (it is built on `FrameProfiler`). It has no per-path timers: for the position distributor, Broadcast, voice receive, animator, movement and spawn, add a sampled Stopwatch pair around each path yourself, plus per-second counters (O, connections, speakers, packets/s, ragdolls, pickups, GC collection count, heap size).
3. Run synthetic load with 60-100 dummies on a copy of the server, with a few real clients for visual checks. Dummies are targets and receivers in `WriteAll` but generate no client traffic and no voice, so measure voice and inbound traffic only in production.
4. Do A/B within a session: 60 s on, 60 s off, repeated; compare the cost of that one path and the whole frame. Decide per module after enough rounds, bucketed by player count.
5. Record mass-event frames (round start, wave, warhead, schematic load, join) with the frame time of the event frame and the next one.

See measuring-scpsl-performance for the procedure.

## Re-verifying on a new build

- Re-run the section "How to replace an estimate" at 60 and 100 dummies and rewrite the [E] and [U] tags with what you measure.
- Re-count: Update/LateUpdate methods on the player prefab, pairs per send, hot events (catalog), per-toy wire size.
- Re-check the corrections list above: raise path for `GetInvocationList`, tickrate override order (log `Application.targetFrameRate` before and after the server starts), MTU, voice frame size.
- If the game moves to another collector or runtime, drop section 7 of tick-budget.md and re-derive it.
