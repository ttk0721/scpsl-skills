---
name: measuring-scpsl-performance
description: "Use when an SCP:SL server lags, rubber-bands or stutters while the tps command or Server.Tps looks fine; when building a TPS or lag alert; before and after any optimisation; when claiming a plugin costs \"X ms per frame\" or \"allocates nothing\"; when planning a load test with dummies or a perf profile."
---

# Measuring SCP:SL server performance

Verified against LabAPI 1.1.7 / SCP:SL 14.2.7 (2026-10).

## Overview

Measure, do not infer. Time every frame with `Stopwatch` at the frame boundaries and compare distributions (p95, p99, % over a threshold) with a baseline from the same server, by player-count bucket. `Server.Tps` is not a measurement.

## When to Use

- Players rubber-band while TPS reads 58-60.
- Writing a health check, alert or dashboard.
- Verifying an optimisation or a CPU, ray or allocation claim.
- Planning a load test, profile or production A/B.

Not for choosing what to optimise (**REQUIRED SUB-SKILL:** scpsl-engine-cost-model) or timers and threads (scpsl-main-thread-and-timing).

## Quick Reference

| Signal | Truth | Use it for |
|---|---|---|
| `Server.Tps`, `tps` command | `round(1 / Time.smoothDeltaTime)`: smoothed, clamped by `maximumDeltaTime` (floor near 3), probably scaled by `timeScale` (unverified). Command denominator is the config value | Nothing. Show it only beside real numbers |
| `NetworkServer.actualTickRate` | Integer frames per wall second, window of at least 1 s | Coarse sanity check |
| `NetworkServer.fullUpdateDuration.average` | EMA of the Mirror early-to-late span. Hides spikes | Share of the frame |
| PlayerLoop `Stopwatch` markers | Wall-clock period and busy time per frame | Ground truth |

Details: signals.md. Probe code and CSV: frame-probe.md. Baseline, A/B, dummies, perf: protocol.md.

## Core rules

1. **Period = wall time between frame starts. Busy = frame start to frame end.** Report p50/p95/p99/max, % frames over 50 and 100 ms, busy/period. Averages hide hitches.
2. **Insert markers into `PlayerLoop.GetCurrentPlayerLoop()`, never a default loop.** Mirror's networking lives there. Marker types keep the edit idempotent.
3. **Define "stable" first.** Proposal for a 20 TPS target, not a game constant: p95 <= 50 ms, p99 <= 80 ms, frames over 100 ms only at mass events (detonation, round start).
4. **Exclude idle mode** (`timeScale` 0.01, 1 FPS by default), map generation and restart frames from steady-state stats. Report them separately.
5. **The round is the unit.** Reduce each round to a median p95; compare by player-count bucket. Frames are correlated.
6. **Baseline first.** Record an unchanged stretch before touching production. No baseline, no verdict.
7. **Label every number** MEASURED or ESTIMATE, with its assumption. Keep the probe cheap: ring buffer, no per-frame logging.

```csharp
FrameProfiler.Install();                        // SlKit, main thread
var s = FrameProfiler.Snapshot();               // on demand
Logger.Info($"p95 {s.PeriodMs.P95:F1} ms, >50ms {s.PercentOver50Ms:F1}%, Server.Tps {s.ServerTps}");
```

## Common Mistakes

| Wrong | Right |
|---|---|
| Alert on one `Server.Tps` read per second. It stays 58-60 during hitches and is meaningless in idle | Alert on p99 period and % frames over threshold, 60 s window. Treat `Server.IdleModeActive` as no data. Compare to `Application.targetFrameRate`, not 60 |
| Enter-low/leave-high hysteresis plus a "for more than N s" timer that starts at the first low window and keeps running through recovery readings. One bad window then ten good ones alerts, and "below X for 11 s" is false | Count only low time toward N. Any reading at or above the enter threshold adds nothing and resets the timer (the band only keeps the episode open to suppress repeats). Word the message from what was counted (protocol.md, Alert logic) |
| A per-frame cost claim ("well under 1 ms") derived from an assumed pass rate | Count the worst case: 2 directions x 3 heights x 2 points = 12 casts per pair, times a 120-pair cap = 1440 casts. Linecast cost on your map is unmeasured: microbenchmark it, or time a shadow run (`SlClock.Stats`) |
| "Allocates nothing" because one string is cached | Enumerate every allocation (a hint send allocates a parameter object, an array and a `TextHint`; `Player.ReadyList` is a LINQ iterator), then compare `GC.CollectionCount(0)` per minute, feature on vs off (coarse, unvalidated) |
| Trusting `server_tickrate: 20` | Log `Application.targetFrameRate` and Mirror `sendRate`. Host start sets the frame rate to `sendRate` (60 by default), overriding the config |
| Proving a fix on dummies | Dummies skip sockets, auth, voice, client commands. State the gap (protocol.md) |
| Profiling production with no baseline | Baseline first, read-only probe, one change at a time |

## Red Flags

- You quote `Server.Tps` as evidence.
- A cost in microseconds, no benchmark.
- One run, no player-count bucket.
- Idle or restart frames in the percentiles.
- An alert message states a duration its own timer did not measure as continuous.
- Unsampled Harmony timing hooks in production.
