# Rule rationale and mistake detail: reference

Verified against LabAPI 1.1.7 / SCP:SL 14.2.7 (2026-10). Companion to SKILL.md; the full text moved out of its Rules and Common Mistakes tables. Related: frame-and-timing.md, threads-async-gc.md.

## Why each rule

| Rule | Why |
|---|---|
| Run wall-clock work on `Stopwatch` deadlines | Update-segment MEC time is scaled, clamped `Time.deltaTime`: about 100x slower in idle, loses time on long frames |
| One clock, try/catch per job, per-player work staggered | An exception ends a coroutine for good |
| Never call `Timing.KillCoroutines()` | It kills the game's coroutines too |
| End round-bound work at `RoundRestarted` | Coroutines outlive the round |
| Expect code before the first `yield` to run inside `RunCoroutine` | MEC prewarm. A throw there is only logged |
| Off-thread, use plain snapshots only | Nothing is locked. Even `Player.Get` writes |
| Marshal back with try/catch and a budget | `MainThreadDispatcher` drains with neither |
| Never block the main thread | `.Result`/`.Wait()` stalls every player |
| Decide before the first `await` in a handler | The event has already returned |
| Never decide anything on `Server.Tps` | It is smoothed, clamped and scaled |
| Re-assign `Server.MaxTps` at `WaitingForPlayers` when not idle | Mirror overwrites `server_tickrate` with 60 |
| Treat your PlayerLoop entry as droppable: add a watchdog outside the loop that re-installs, and read `df == 0` as no data | The loop is shared. A plugin that rebuilds it removes every foreign entry, and `IsInstalled` stays `true` |
| Log rarely; no per-frame allocation; no `GC.Collect` | Stack walk + 2 regexes per line; Boehm GC |

## Common Mistakes, full wording

| Wrong | Right |
|---|---|
| 1 s tick and 60 s POST on Update-segment `WaitForSeconds`, dt from a `Stopwatch` clamped to 3x the tick | Schedule and measure with one `Stopwatch`. Mixed clocks lose time after hitches. Clamp only on purpose. |
| TPS alert sampled by `WaitForSeconds(1f)`, window timed on the wall clock, no idle check | Idle can start mid-round (last player leaves, round stays open). Samples then come about 100 s apart. Treat `Server.IdleModeActive` as no data; the idle `Server.Tps` reading is unverified. |
| Default-segment `RunCoroutine` for a wall-clock job | Use `Segment.SlowUpdate` (real time, at most about 7 Hz) or a `Stopwatch` deadline. |
| One `SlClock.Every` registered in `Enable()`, `FrameProfiler.IsInstalled` taken as proof the frame counter runs, `df == 0` alerted as "TPS 0.0" | `IsInstalled` is a flag, not a probe. If another plugin rebuilds the PlayerLoop, the job stops silently, or the frame delta stays 0 while the clock ticks. Add the watchdog from frame-and-timing.md (re-install on stall, `df == 0` is no data). |
| Bare `while(true)` HUD coroutine, re-started at `WaitingForPlayers` "because MEC dies on restart" | Coroutines survive restarts; this adds a second copy unless `Timing.IsRunning` guards it. The real risk is one exception ending the loop: try/catch per tick. |

## Red Flag, full wording

- `PlayerLoop.GetDefaultPlayerLoop()` drops Mirror's network hooks, and any foreign rebuild drops your entry the same way.

## Re-verifying on a new build

- Re-check what the Update segment of MEC measures (scaled, clamped `Time.deltaTime`), and that `Segment.SlowUpdate` is still real time at roughly 7 Hz. The first rows of "Why each rule" and the segment mistake row depend on it.
- Re-check that `Timing.KillCoroutines()` still kills coroutines owned by the game, that coroutines still survive a round restart, and that code before the first `yield` still runs inside `RunCoroutine`.
- Re-check that `MainThreadDispatcher` still drains with neither try/catch nor a budget, and that `Player.Get` still writes to a shared cache when called off-thread.
- Re-check the `Server.Tps` formula and that Mirror still overwrites `server_tickrate` with 60 at the points the rule names (the `MaxTps` re-assignment at `WaitingForPlayers`).
- Re-check which code rebuilds the PlayerLoop, and that `PlayerLoop.GetDefaultPlayerLoop()` still drops Mirror's network hooks. The watchdog rule and the Red Flag depend on it.
- Re-check the idle-mode time scale and what a `Server.Tps` reading is there (marked unverified in the mistake rows). Settle it on a live server with `tools/sl-probe`, not by reading code.
