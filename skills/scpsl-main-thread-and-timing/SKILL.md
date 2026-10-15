---
name: scpsl-main-thread-and-timing
description: "Use when an SCP:SL plugin (LabAPI or EXILED) uses MEC coroutines, Timing.WaitForSeconds, CallDelayed, timers, Task.Run, threads, async/await or HttpClient; when a periodic job stalls on an empty server, drifts after lag or silently stops; when Server.Tps or server_tickrate look wrong; or on \"Collection was modified\" or \"can only be called from the main thread\"."
---

# SCP:SL main thread and timing

Verified against LabAPI 1.1.7 / SCP:SL 14.2.7 (2026-10).

## Overview

One Unity main thread owns almost every game, Mirror, LabAPI and MEC object. Drive periodic work from one wall clock with absolute deadlines and a try/catch per job. Hand other threads plain snapshots only.

## When to Use

- Anything periodic, delayed, async or off-thread.
- Symptoms: a 30 s timer takes about 50 minutes on an empty server, timers lag after hitches, a loop dies mid-round, `Server.Tps` reads 60 during rubber-banding, `server_tickrate` changes nothing, `IsAllowed` set after `await` does nothing.
- Not for: measuring (measuring-scpsl-performance), restart order (scpsl-game-lifecycle).

## Rules

Why for each rule: rule-rationale.md.

| Rule | SlKit / lint |
|---|---|
| Run wall-clock work on `Stopwatch` deadlines | `SlClock.Every`/`After` |
| One clock, try/catch per job, per-player work staggered | `SlClock`, `EveryStaggered` |
| Never call `Timing.KillCoroutines()` | SL006 |
| End round-bound work at `RoundRestarted` | `ClockScope.Round` |
| Expect code before the first `yield` to run inside `RunCoroutine` | none |
| Off-thread, use plain snapshots only | your own `Background.ForOwner(Name, n)` worker's `TryRun`, SL016 |
| Marshal back with try/catch and a budget | `MainThread.Post` |
| Never block the main thread (`.Result`/`.Wait()`) | your own `SlHttp.CreateClient` client, SL007 |
| Decide before the first `await` in a handler | none |
| Never decide anything on `Server.Tps` | `FrameProfiler`, SL004 |
| Re-assign `Server.MaxTps` at `WaitingForPlayers` when not idle | owner's choice: the standalone `tools/tickrate-fix` plugin, not a library call |
| Treat your PlayerLoop entry as droppable: watchdog outside the loop re-installs; `df == 0` means no data | `SlClock.Install()`, `FrameProfiler.FrameCount` |
| Log rarely; no per-frame allocation; no `GC.Collect` | SL008, SL010 |

Details: frame-and-timing.md (frame, tickrate, idle, MEC, watchdog) and threads-async-gc.md (threads, marshalling, async, logging, GC).

## The pattern

```csharp
public override void Enable()
{
    SlClock.Install();                 // main thread only (throws elsewhere): one PlayerLoop entry; Round jobs end at RoundRestarted
    SlClock.Every(TimeSpan.FromSeconds(60), Flush, "stats.flush");
    _http = SlHttp.CreateClient(Name);                    // this plugin's own client: its slots, timeouts, headers
    _worker = Background.ForOwner(Name, capacity: 64);    // this plugin's own thread and queue (disk and CPU work)
}

private void Flush()                   // main thread, try/catch added by SlClock
{
    string json = BuildJson();         // snapshot: copy plain values out of wrappers HERE
    _http.PostJsonAsync(_url, json, r =>
    {
        if (!r.Success) Logger.Warn("flush failed: " + r);  // back on main thread; ToString names the phase
    });
}
```

The static `SlHttp.PostJsonAsync` and `Background.TryRun` exist, but they run on one default client (4 concurrent, 64 queued) and one default worker for every plugin that shares `SlKit.dll`, so another plugin's backlog turns into your `Rejected` results and failed `TryRun`s. Use them only for a throwaway call. `Success` is 2xx with a complete body; `StatusCode` can be non-zero on a failure, and `Rejected`/`QueueTimedOut` mean the request was never sent.

Without SlKit: one coroutine polling `Stopwatch` deadlines (frame-and-timing.md).

## Common Mistakes

Fix detail: rule-rationale.md.

| Wrong | Right |
|---|---|
| 1 s tick and 60 s POST on Update-segment `WaitForSeconds`, dt from a `Stopwatch` clamped to 3x the tick | One `Stopwatch` for schedule and measure. Clamp only on purpose. |
| TPS alert sampled by `WaitForSeconds(1f)`, window on the wall clock, no idle check | Idle can start mid-round. Treat `Server.IdleModeActive` as no data. |
| Default-segment `RunCoroutine` for a wall-clock job | `Segment.SlowUpdate` or a `Stopwatch` deadline. |
| One `SlClock.Every` in `Enable()`, `FrameProfiler.IsInstalled` as proof the counter runs, `df == 0` alerted as "TPS 0.0" | `IsInstalled` is a flag, not a probe. Add the watchdog; `df == 0` is no data. |
| Bare `while(true)` HUD coroutine, re-started at `WaitingForPlayers` "because MEC dies on restart" | Coroutines survive restarts; guard with `Timing.IsRunning`. try/catch per tick. |

## Red Flags

- `WaitForSeconds`/`CallDelayed`/`CallPeriodically` used for real-world seconds
- a coroutine loop with no try/catch
- `Player`, `Server`, Unity or `Timing` members inside `Task.Run`, `Timer` callbacks or after `ConfigureAwait(false)`
- `async void` handlers that change `ev` after `await`
- `PlayerLoop.GetDefaultPlayerLoop()`, which drops Mirror's network hooks (any foreign rebuild drops your entry too)
- a periodic job whose only liveness evidence is `IsInstalled` or "I registered it in `Enable()`"

Run scpsl-lint before review (reviewing-scpsl-plugins).
