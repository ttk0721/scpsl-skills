# Measurement protocol

Verified against LabAPI 1.1.7 / SCP:SL 14.2.7 (2026-10). Figures marked ESTIMATE come from code reading, not from a profiler.

## 0. Order of work

1. Install the frame probe (frame-probe.md). Change nothing else.
2. Collect a baseline.
3. Find the hot spot with measurements, not theory.
4. Change one thing, behind a runtime switch.
5. Compare against the baseline with the A/B method below.
6. Keep the switch until the verdict is in.

## 1. What "stable" means

Pick the thresholds before the experiment. A defensible definition for a 20 TPS target (50 ms period), a proposal, not a game constant:

| Metric | Threshold |
|---|---|
| p95 frame period | <= 50 ms |
| p99 frame period | <= 80 ms |
| frames over 100 ms | only during mass events |
| busy / period | well below 1 (no headroom means the next spike is a hitch) |

Mass events that exceed 50 ms even on a healthy server (ESTIMATES): detonation 45-300 ms, round start 60-180 ms (more for the first round after process start), schematic loading with players present, full GC. Mark them and report steady-state and event frames separately. Map generation also blocks the main thread synchronously (0.50 s, first-run JIT included), and a round restart adds a full `GC.Collect` and an async scene load (restart cost end to end is UNVERIFIED). Scale the thresholds for other targets: a 60 TPS target has a 16.7 ms period. Code-reading estimates for 80-100 players, assuming the known hot paths had been optimised, put 60 TPS out of reach (roughly 8-20 ms of per-frame cost against a 16.7 ms budget) and 30 TPS plausible in steady state with no room for spikes. They put 20 TPS within reach in steady state only if five conditions hold: the tickrate override is fixed, plugin overhead on hot paths is neutralised, the networked object count is bounded, speaker count and uplink (at least about 150-200 Mbit/s) are adequate, and measurement confirms the animator, physics step and character-controller assumptions. All ESTIMATES; use them to choose a target to test, not as a promise.

A raw average, a mean TPS or a single minimum is never a stability statement.

## 2. Baseline

- Run the unchanged server long enough to cover the load you care about. A reasonable plan is one unchanged week before each wave of changes. Shorter is fine for a quiet server, but state the length.
- Record per round: median p95, median p99, % frames over 50 ms and over 100 ms (steady state), max, player count distribution, round length, exceptions logged.
- Record the environment: server version, plugin list and versions, `server_tickrate`, observed `targetFrameRate`, host CPU and uplink.
- Do not start the baseline from a cold process if you want steady numbers. Tag the first round after boot.

## 3. Per-round A/B on a live server

1. Every optimisation sits behind a runtime switch (a config flag or console command). Disabling must not need a restart.
2. Assign the arm at `WaitingForPlayers`, randomly per round, never mid-round. Write the arm id to the CSV.
3. Log player count, round number and event marks with every sample.
4. Reduce each round to one row: median p95, % over 50 ms, exceptions. **The round is the unit**, not the frame (consecutive frames are correlated).
5. Bucket rounds by player count, for example 70-79, 80-89, 90-100 on a 100-slot server. Pick buckets from your own distribution so each holds several rounds.
6. Collect at least 15 rounds per arm (a suggested minimum). Compare medians per bucket. Accept only if the median p95 and % over 50 ms drop, there are no new exceptions and no player complaints (hit registration, visibility, voice, HUD).
7. Confounders to control or tag: map seed, number of SCPs, warhead use, speakers count, first-round warm-up, a plugin hot-reload, other plugins changing between rounds.
8. Never run several experimental changes in the same round unless arms are crossed deliberately.

## 4. A/B inside one session (test copy only)

For a test server with scripted load: 60 s on, 60 s off, 10 repetitions, alternating. Compare per-path ms per frame and whole-frame period. Same bucket rule for player count. Do this on a copy, not on production.

## 5. Load tests with dummies

Dummies are created with the game's dummy utility (`NetworkManagerUtils.Dummies.DummyUtils.SpawnDummy`; EXILED's `Npc.Spawn` calls the same). LabAPI 1.1.7 has no wrapper for it.

**What dummies DO simulate**
- A full hub: role, spawn point, loadout, stats, hitboxes, animators.
- Server-side movement driven by scripted dummy actions.
- Receiving position data: each dummy is a receiver in the per-pair position pass, and the per-receiver messages (positions, role sync, spawn) are packed and then discarded. That serialisation is real server CPU.
- Counting as a player for the lobby countdown, role assignment and hub-count checks.

**What dummies do NOT simulate**
- Preauth and auth handshake (key exchange and signature checks cost about 5.3 ms per join on the main thread, 7.1 ms with a badge, in Mono; a mass rejoin is invisible to dummies).
- Sockets, the transport layer, batching and encryption.
- Per-connection Broadcast: dummy connections are not in the connection list, so the per-connection scan of every spawned object is skipped for them.
- Voice traffic, client-to-server commands, latency and jitter.
- Client scene-loading phases and client-side hint or UI handling.
- Real uplink saturation.

A dummy test therefore under-measures network, join and client cost. Measure voice and incoming traffic cost only on production, with a baseline. State the gap in the report.

**Setup rules**
- Create dummies at `WaitingForPlayers` or later. Wait one frame for the hub's `Start` before assigning a role if you need dummy actions. Assign roles explicitly: a dummy created after round start takes the late-join path and becomes a spectator (with `late_join_time` 0; in the lobby the late-join path does not apply).
- Lock the round (`Round.IsLocked = true`) or use a multi-team roster. A one-team roster ends the round within roughly 15-30 s (exact figure UNVERIFIED).
- Dummies are destroyed on every round restart. Recreate them.
- Each new dummy triggers a spawn burst to that connection: every spawned object is serialised for it. ESTIMATE 3-8 ms per dummy on vanilla, one frame, more on maps with tens of thousands of objects. Spawn gradually and mark the burst.
- A leftover dummy keeps the server out of idle mode. Destroy them after the test.
- Scenarios worth scripting: walking, shooting between dummies, round start with a full roster, a respawn wave, detonation with dozens of deaths, loading a large schematic, a plugin reload.
- Add 2-5 real vanilla clients for visual verification.

## 6. Native profile with perf

Harmony and per-method `Stopwatch` hooks cannot see engine-native cost inside a phase: animator update and IK, character controller moves, callback dispatch. (PlayerLoop markers around `FixedUpdate.PhysicsFixedUpdate` can time the physics step as a whole.)

```
perf record -g -p <pid of the SCPSL.x86_64 process> -- sleep 60
perf report --sort dso,symbol
```

- Record with a full server (60 s). Group by shared object: the Unity player library, the physics libraries, the Boehm GC runtime library.
- Managed (JIT) frames show as anonymous addresses. Whether the embedded Mono honours the `--jitmap` option through `MONO_ENV_OPTIONS` and writes a `/tmp/perf-<pid>.map` file is UNVERIFIED, so try it on a test copy.
- On Linux, reading another process may need `perf_event_paranoid` lowered or root (general Linux knowledge).
- Use it to answer "which of animator, physics, GC, managed code" before choosing a fix. Take two profiles (idle load and full load) to see what scales with players.

## 7. Claims about a plugin's own cost

A statement like "X costs Y per frame" needs one of:

| Evidence | How |
|---|---|
| MEASURED in the plugin | Run the work inside an `SlClock` job and read `SlClock.Stats` (last, average, max microseconds), or wrap it in `Stopwatch` with a ring buffer |
| MEASURED in isolation | A microbenchmark of the single primitive (one `Physics.Linecast`, one `Player.Get`) on the real map, then multiply |
| ESTIMATE | State per-unit cost, count and assumptions. Count the worst case through every loop and cap. Never a bare "a few microseconds" |

Worked example of the arithmetic: a pair test that casts in both directions, at 3 heights, to 2 points makes 12 casts per pair. A cap of 120 pairs per frame allows 1440 casts. At an assumed 1-3 microseconds per cast that is 1.4-4.3 ms per frame (ESTIMATE; the per-cast cost is unmeasured). Compare it with the existing cost of the path you hook, not with a vague "1 ms budget".

Run new per-frame logic first in shadow mode (compute, do not apply), log how often it would act, and audit a sample. See reviewing-scpsl-plugins.

## 8. Allocation claims

"Allocates nothing" is a measurable claim and usually false for anything that sends a hint, builds an event argument or runs LINQ.

1. List every allocation on the path by reading the code: the cached string is one allocation among many; a hint send allocates a parameter object, an array and a `TextHint`; `Player.ReadyList` and `Count` are LINQ `Where`/`Count` over the player dictionary, so each call allocates an iterator (and a boxed enumerator when enumerated through `IEnumerable`) and is O(N): cache the list per frame.
2. Measure: same players, same duration (minutes), feature on vs off. Compare `GC.CollectionCount(0)` per minute and the slope of `GC.GetTotalMemory(false)` between collections. Treat the numbers as coarse; the method has not been validated against this Mono build.
3. Boehm marks the whole live heap each cycle, so a high allocation rate shows up as periodic long frames, not as a steady cost. Correlate with the frame probe.
4. A cache that removes the smallest allocation does not make the path allocation-free. Report what remains.

## 9. Production safety

- Probe first, change later. No baseline, no verdict.
- Probe must be read-only and cheap (frame-probe.md, Probe hygiene).
- Roll out one change per wave, each behind a switch with an automatic kill on repeated exceptions.
- Sample Harmony timing hooks (for example 1 call in N) and sum per frame. A Stopwatch pair on a method called per player pair is itself a cost.
- Do not run heavy diagnostics on a full server for long; short windows with marks.
- Keep the old CSVs. A regression months later needs the baseline.

## 10. Alert logic (design guidance, not an engine fact)

For a lag or low-rate alert with a "stays below X for more than N s" requirement and hysteresis (enter below X, leave at or above a higher Y):

- Keep two things apart: the **episode** (open from the first low window until a window at or above Y) and the **sustained timer** (time actually spent below X).
- Only windows below X add to the timer. Any window at or above X adds nothing; for a "stays below" requirement it also resets the timer (or pauses it if you want cumulative time, and then say "cumulatively"). A window in the band X to Y keeps the episode open, which only suppresses a repeat alert. The timer must never run on wall-clock time since the first low window.
- Test the state machine with a trace before shipping: one low window followed by ten good ones must not alert; a continuous low stretch of N s plus one window must; a flap just under and just over X must not accumulate.
- Build the message from what was counted. If the timer ran only on low windows, "below X for N s" is true. If you alert on episode age instead, say "has not recovered above Y for N s". Quote the latest reading and the threshold it is compared with, not a stale one.
- Windows with `Server.IdleModeActive`, restart and map generation are no data: they neither advance nor reset the timer, and they do not close an episode.
- Rate-limit repeats (one alert per episode plus a recovery note) so a flapping server does not post every window.

## Re-verifying on a new build

- Re-run the sanity test from frame-probe.md (a known 100 ms stall must appear in the probe).
- Re-measure the idle-mode behaviour: confirm `Server.IdleModeActive` and the frame-period spike you drop from stats.
- Re-check dummy behaviour: that dummy connections are still outside the connection list, still discard sends, and that `DummyUtils.SpawnDummy` and `Round.IsLocked` still exist.
- Check `perf` can still attach and which libraries the process loads (`ldd`/`/proc/<pid>/maps` on a test copy).
- Re-run one A/B with a known no-op switch: both arms must show the same distribution. If they differ, your method (bucketing, warm-up) is leaking.
