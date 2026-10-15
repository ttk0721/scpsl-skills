# Frame, tickrate, idle mode and MEC: reference

Verified against LabAPI 1.1.7 / SCP:SL 14.2.7 (2026-10). Companion to SKILL.md. Figures marked
"estimate" were not measured on this build. Items marked UNVERIFIED are open questions.

## What paces the frame

The server is one Unity main thread on the standard PlayerLoop. `Application.targetFrameRate`
paces the loop. Physics is a separate fixed step of 0.02 s (50 Hz). That value comes from project
settings and was not re-read for this build (unverified).

Writes to `Application.targetFrameRate` on a dedicated server, in time order:

| Moment | Value written | Notes |
|---|---|---|
| Process start (`ServerStatic` setup) | 60 | Before plugins enable |
| `CreateMatch` config load | `server_tickrate` | Via the `ServerStatic.ServerTickrate` setter |
| Mirror host start (`NetworkManager.ConfigureHeadlessFrameRate`) | `sendRate` (default 60) | **Overrides the config.** SL does not override this virtual |
| Idle mode entered | `idle_mode_tickrate` (default 1) | Also `Time.timeScale = 0.01` and a scaled `fixedDeltaTime` |
| Idle mode left | `ServerStatic.ServerTickrate` | Also `timeScale = 1` |
| Config reload (`reload`) | `server_tickrate` | Also re-fills idle settings |
| Plugin sets `Server.MaxTps = x` | `x`, immediately | Writes even while idle. `-1` passes through as uncapped; `0` and values below `-1` become 60 |

Consequences:

- A `server_tickrate` below 60 has no effect after boot until the first idle exit or a config
  reload. On a server with `idle_mode_enabled: false` it only takes effect on a reload.
- Fix: in `ServerEvents.WaitingForPlayers` (round 1 is enough) and after each reload (the public
  static `GameCore.ConfigFile.OnConfigReloaded`), re-assign `Server.MaxTps` when
  `!Server.IdleModeActive`. Replace an unlimited value (`-1`, `0`) with a cap (for example 60) and
  do not lower a value the owner chose. Every assignment
  prints a "Server tickrate set to" console line, so never do it per frame. While idle, the setter
  would raise the frame rate while `timeScale` is still 0.01.
- This repository ships the fix as a standalone LabAPI plugin, `tools/tickrate-fix`
  (`TickrateFix.dll`, config `enabled` and `cap`), not as a library call: the frame rate is one value
  for the whole server, so installing it is the server owner's decision and a plugin should not set it
  as a side effect (SlKit 0.1 had `TickrateFix.Apply`; 0.2 removed it). It is off by default: as
  shipped it only logs `Application.targetFrameRate` at `WaitingForPlayers`, so the owner can see
  whether the override happens on their build before turning it on. With `enabled: true` it
  re-applies at `WaitingForPlayers`, skips while idle, replaces `-1` and `0` with `cap`, and never
  lowers a `server_tickrate` the owner set above `cap`. It logs
  `Application.targetFrameRate` before and after. If those two differ, the override happened on
  your build. If they are equal, the call was a no-op (for example when `sendRate` already equals
  `server_tickrate`). A plugin that needs the configured rate should say so in its README and point
  the owner at that plugin.
- A plugin's `Server.MaxTps` and `Server.IdleModeAvailable` overrides write the same statics that a
  config reload re-fills from the file, so a reload discards them. That is why the re-apply after
  `OnConfigReloaded` is needed.
- Alerts must compare the real frame rate against what is actually targeted:
  `Application.targetFrameRate` (exposed as `FrameSnapshot.TargetFrameRate`), or `Server.MaxTps`
  once the fix above has run. Use a ratio and never a hard-coded 60. A server configured for 20-30
  would otherwise alert permanently.
- Mirror's broadcast tick is `sendRate` (60), so at 60 FPS or below it fires every frame. The FPC
  position send interval is `1 / clamp(ServerTickrate, 10, 60)`, which follows the **config**
  value, not the real frame rate.

## Your PlayerLoop entry can disappear

The PlayerLoop is one shared tree. Mirror and every plugin insert into `GetCurrentPlayerLoop()`,
and anything that rebuilds the tree (`GetDefaultPlayerLoop()`, a hand-built loop, a copy made
before your insert) removes every entry it does not know. Nothing notifies you. This is the same
rule that makes `GetDefaultPlayerLoop()` kill Mirror, applied to your own entry.

SlKit specifics (its own behaviour, not the engine's):

- `SlClock` reinstalls only inside a registration call (`Every`, `After`, `EveryStaggered`) made
  after its frame counter has stalled for 10 s or more, or on an explicit `Install()`. A plugin
  that registers once in `Enable()` never triggers it, so its jobs just stop.
- `SlClock.IsInstalled` and `FrameProfiler.IsInstalled` are plain flags set by `Install`/`Uninstall`.
  They do not check the loop. If only the `FrameProfiler` entries are dropped, the flag stays
  `true`, `FrameCount` freezes, the frame delta reads 0 and the clock keeps ticking.
- Calling `SlClock.Install()` again is safe: it reinserts a missing entry, and when the entry is
  present it only restarts the stall window.

Rules for a job that must outlive other plugins:

1. A frame delta of 0 (`FrameCount` unchanged between two samples) is "no data", not "0 TPS".
   Do not alert, and treat it as a stall of the profiler.
2. Run a watchdog on a clock that is not your PlayerLoop entry, for example a `Segment.SlowUpdate`
   coroutine (MEC rides on `MonoBehaviour.Update`, a separate path). Each tick compare `SlClock.FrameCount`
   and `FrameProfiler.FrameCount` with the previous values. If one is unchanged for several
   seconds (longer while idle, see 4), call `SlClock.Install()` / `FrameProfiler.Install()`
   (main thread) and log it once. Keep try/catch around the tick.
3. Give the job a heartbeat of its own: record the wall time of its last run and let the watchdog
   report or re-register when it is overdue by a few periods.
4. While `Server.IdleModeActive`, frames come about once per second, so size the stall window in
   frames or seconds with that in mind (several seconds is not a stall then) and never alert on idle readings.

## One frame, in order

Standard Unity 6 phase order is assumed here. Dumping the top-level
`PlayerLoop.GetCurrentPlayerLoop().subSystemList[i].type.Name` once at startup to confirm it is
still UNVERIFIED for this build.

| Phase | What runs there |
|---|---|
| Frame-limiter wait (position UNVERIFIED), `Initialization` | `FrameProfiler` frame-start mark |
| `EarlyUpdate` | Mirror `NetworkEarlyUpdate` at its end (time interpolation) |
| `FixedUpdate`, 0..n times | PhysX, MEC FixedUpdate segment, `MainThreadDispatcher` FixedUpdate queue, idle-mode checks, **queued console commands**, round-end checks |
| `Update` | every `MonoBehaviour.Update` in undefined order. MEC `Timing.Update` runs the Update, SlowUpdate and RealtimeUpdate segments here. Also the `MainThreadDispatcher` Update queue, `UnitySynchronizationContext` continuations (exact sub-phase UNVERIFIED) and `SlClock` (right after `ScriptRunBehaviourUpdate`) |
| `PreLateUpdate` | `LateUpdate`s, including the LiteNetLib4Mirror transport draining received packets into Mirror message handlers. **Most client-driven game code and the LabAPI events it raises run here.** Also the MEC LateUpdate segment and FPC position distribution. Mirror `NetworkLateUpdate` runs at the end (`Broadcast`, then the transport flush) |
| `PostLateUpdate` | MEC EndOfFrame segment (needs Unity's coroutine pump). `FrameProfiler` frame-end mark |

- `FixedUpdate` runs 0 or 1 times per frame at 60 FPS, 2-3 times at 20 FPS, and more after a
  hitch. In idle it runs about 50 times per frame (derived, UNVERIFIED). Use it only for
  physics-coupled logic.
- The network poll thread sleeps 30 ms between polls, so input arrives in batches of up to about
  30 ms whatever the tickrate.

## Idle mode

| Fact | Detail |
|---|---|
| Condition | `ReferenceHub.AllHubs.Count <= 1`, meaning only the host hub. **One dummy or NPC hub keeps the server out of idle.** |
| Entry | When the last hub is removed, or about 30 s after `WaitingForPlayers` on an empty server (`idle_mode_preauth_time` 30000 ms, `idle_mode_time` 5000 ms) |
| Effect | `targetFrameRate = idle_mode_tickrate` (default 1), `Time.timeScale = 0.01` |
| Exit | Any hub added, any accepted preauth step (about 0.14 s after preauth), any rejected delayed connection |
| Pause | Paused across restarts and un-paused by the host's start right before every `WaitingForPlayers`. A plugin's `Server.PauseIdleMode = true` must therefore be re-applied each round |
| **Mid-round idle** | `end_round_on_one_player` defaults to false, so the round is kept open while fewer than 2 non-host hubs remain. When the last player leaves mid-round, idle starts **while the round is still in progress.** A plugin's own `_roundActive` flag does not mean anyone is online |
| Config | `idle_mode_enabled` (default true) |

What idle does to each clock:

| Stretched about 100x (game time) | Not stretched, but polled only once per frame (about 1 s granularity while idle) |
|---|---|
| MEC Update / LateUpdate / FixedUpdate / EndOfFrame segments, `CallDelayed`, `CallPeriodically`, `Time.time`/`Time.deltaTime` logic, vanilla wave timers | `Stopwatch`, `DateTime.UtcNow`, MEC SlowUpdate, `SlClock`, and RealtimeUpdate (unscaled, but clamp exemption UNVERIFIED) |

A 30 s Update-segment wait takes 30 / 0.01 = 3000 s, about 50 minutes, whatever the idle frame
rate.

## `Server.Tps` and better signals

LabAPI's `Server.Tps` is `Math.Round(1f / Time.smoothDeltaTime)`. The `tps` console command prints
the same expression as `x/N`, where N is the **config** tickrate, not the real target. EXILED's version is
`Math.Min(1 / Time.deltaTime, MaxTps)` plus the same smoothed variant, so it inherits the problems
and is also capped.

Why it misleads:

1. `deltaTime` is clamped at `maximumDeltaTime` (0.333 s, not re-read for this build).
   The reading therefore never drops below about 3, and a 2 s freeze reads as 0.333 s.
2. `smoothDeltaTime` is a low-pass filter, so isolated 100-300 ms hitches barely move it. The exact
   filter is UNVERIFIED.
3. It is probably scaled by `timeScale` (UNVERIFIED for `smoothDeltaTime` specifically), so it
   means nothing in idle mode. Whether it then reads about 1 or about 100 is UNVERIFIED. Treat
   `Server.IdleModeActive` as "no data".

| Signal | Accuracy |
|---|---|
| `SlKit.FrameProfiler`: `Stopwatch` at the first `Initialization` entry and the last `PostLateUpdate` entry | The only spike-accurate source. Period gives the real FPS, start to end gives busy time |
| `Mirror.NetworkServer.actualTickRate` | `int` frames per window of at least 1 s of unscaled time. Whether it escapes the clamp is UNVERIFIED, so it may overstate after a freeze |
| `Mirror.NetworkServer.fullUpdateDuration.average` | `Stopwatch` EMA in seconds, covering FixedUpdate through LateUpdate. Hides spikes |

See measuring-scpsl-performance for the method.

## MEC (`MEC.Timing`, shipped inside `Assembly-CSharp-firstpass.dll`; there is no MEC.dll)

### Time base per segment

| Segment | Clock | In idle | Use for |
|---|---|---|---|
| `Update` (default; `CallDelayed`, `CallPeriodically`) | summed `Time.deltaTime`, scaled and clamped | about 100x slower | per-frame gameplay polling |
| `LateUpdate`, `EndOfFrame` | same | same | avoid EndOfFrame |
| `FixedUpdate` | `Time.fixedTime` | many steps per frame | physics-coupled only |
| `SlowUpdate` | `realtimeSinceStartup`, ticked from Update at most every 1/7 s | about 1 s | wall-clock periodic work at 150 ms or coarser |
| `RealtimeUpdate` | summed `unscaledDeltaTime` (clamp exemption UNVERIFIED) | per frame | per-frame, unscaled |

- `Timing.WaitForSeconds(t)` returns an absolute wake time (segment local time + t) and allocates
  nothing. `Timing.WaitForOneFrame` is negative infinity.
- `Timing.LocalTime` only advances on frames where that segment had coroutines, so it is not
  `Time.time`.

### Behaviour that bites

| Behaviour | Consequence | Do |
|---|---|---|
| The controller is created with `DontDestroyOnLoad` | Coroutines **survive round restarts** and keep touching destroyed hubs | Kill round-bound ones at `RoundRestarted`. It can fire twice, so keep the handler idempotent (see scpsl-game-lifecycle). Starting another copy each round without `Timing.IsRunning(handle)` duplicates the loop |
| Prewarm: `RunCoroutine` runs the body up to the first `yield` synchronously (Update, FixedUpdate, LateUpdate, SlowUpdate, RealtimeUpdate; not EndOfFrame, ManualTimeframe or the singleton-wait overloads) | That code runs inside your handler. A throw there is caught and logged through `Debug.LogException`, never rethrown, so the caller sees success | `yield return Timing.WaitForOneFrame;` first if you need deferral. The first resume can still fall later in the same frame |
| An exception in any later step is caught per coroutine, logged, and the coroutine is killed | A `while(true)` loop without try/catch **dies silently for the rest of the process** | try/catch inside the loop body. C# forbids `yield return` inside a `try` that has a `catch`, so keep the yield outside |
| `Timing.KillCoroutines()` with no arguments reallocates every array and clears every dictionary | Kills the game's coroutines too (round summary, lobby). The game itself only kills by handle | Kill by handle or by a plugin-unique tag: `KillCoroutines("myplugin")` |
| `KillCoroutines(params CoroutineHandle[])` has no single-handle overload | Allocates an array per call | Fine for rare kills |
| `CancelWith(gameObject)` checks the object on each resume | **Kills** (not pauses) when the object is destroyed **or deactivated** | Only for coroutines owned by a scene object |
| `CallDelayed(delay, action)` is a coroutine that waits on the Update segment | Scaled time. Allocates an iterator and a delegate (plus a closure) per call | One per rare event is fine (role change, round start). Not per hit, shot or voice packet |
| `CallPeriodically` is two coroutines, and each period is measured from the resume frame | Drifts by up to one frame per period, on scaled time | Absolute deadlines instead |
| Slot arrays grow in chunks with a full copy, and compaction runs every 65 Update frames (4 dictionary operations per moved coroutine) | Spikes when the server first gets busy, and churn cost from many short-lived coroutines | Few long-lived coroutines. Pre-size your own lists too |
| MEC has no locks | `RunCoroutine`/`KillCoroutines` from another thread corrupts it | Main thread only |

### Cost per primitive (main thread)

| Primitive | Cost | Allocation | Clock |
|---|---|---|---|
| Sleeping coroutine, Update segment | each frame: a few array checks plus an interface `Current` call. 1000 of them cost microseconds per frame (estimate) | 0 | scaled, clamped |
| Sleeping coroutine, SlowUpdate | the same, at most about 7 times per second | 0 | wall |
| `RunCoroutine` | 2 dictionary adds, the synchronous first step, occasional array growth | iterator | — |
| `CallDelayed` | as above, plus a kill at the end | iterator + delegate (+ closure) | scaled, clamped |
| `CallPeriodically` | 2 coroutines | 2 iterators + delegates | scaled, drifts |
| PlayerLoop entry (`SlClock`) | one native-to-managed call per frame | 0 | whatever you read (`Stopwatch`) |
| `MonoBehaviour.Update` | one native-to-managed call per component per frame. About 0.1-0.2 µs each (estimate) | 0 | — |

Steady-state coroutine count is rarely the problem. Creation and kill churn and unbounded work
inside a step are. Do not rewrite a handful of long-lived coroutines just for speed. Rewrite them
for the right clock and for the exception boundary.

## The one clock without SlKit

```csharp
using System;
using System.Collections.Generic;
using System.Diagnostics;
using LabApi.Features.Console;
using MEC;

public sealed class PluginClock
{
    private struct Job { public string Name; public long Period, Next; public Action Run; }

    private readonly List<Job> _jobs = new List<Job>(16);
    private CoroutineHandle _handle;

    public void Every(string name, TimeSpan period, Action run)
    {
        long ticks = (long)(period.TotalSeconds * Stopwatch.Frequency);
        _jobs.Add(new Job { Name = name, Period = ticks, Next = Stopwatch.GetTimestamp() + ticks, Run = run });
    }

    public void Start()                                      // safe to call from a repeated Enable()
    {
        if (Timing.IsRunning(_handle)) return;
        _handle = Timing.RunCoroutine(Loop(), Segment.Update, "myplugin.clock");
    }

    private IEnumerator<float> Loop()
    {
        while (true)
        {
            yield return Timing.WaitForOneFrame;             // first: no prewarm work; never inside the try
            long now = Stopwatch.GetTimestamp();
            for (int i = 0; i < _jobs.Count; i++)
            {
                Job job = _jobs[i];
                if (now < job.Next) continue;
                long next = job.Next + job.Period;
                job.Next = next > now ? next : now + job.Period;  // absolute deadline; skip missed periods
                _jobs[i] = job;
                try { job.Run(); }
                catch (Exception e) { Logger.Error($"[clock] {job.Name}: {e}"); }  // rate-limit in real code
            }
        }
    }
}
```

- If 150 ms granularity is enough, run the same loop on `Segment.SlowUpdate` so it wakes at most
  about 7 times per second.
- `SlClock` does the same thing as a PlayerLoop entry and adds a few extras: per-job cost `Stats`,
  rate-limited failure logs, a failure breaker (after 5 failures in a row, tunable per job with `ClockJobOptions` at registration, the job
  stops; under the default `JobFailurePolicy.Backoff` it comes back after 30 s, then 1, 2, 4 ... up
  to 30 min, and one good run resets that; `DisablePermanently` keeps it off until `Reenable()`),
  `SkippedPeriods`, `ClockScope.Round`, and `EveryStaggered`, which spreads N items over the frames
  of one period.
- Accumulated durations (playtime, AFK time) should come from the same `Stopwatch`. Clamp a
  per-tick delta only when you mean "a freeze does not count", and log how much you dropped.

## Re-verifying on a new build

- LabAPI source (`LabApi/Features/Wrappers/Server/Server.cs`): `Tps` expression, `MaxTps` setter
  target, `IdleModeActive`.
- Mirror `NetworkManager`: does `SetupServer` still call `ConfigureHeadlessFrameRate` and set
  `targetFrameRate = sendRate`? Does any game subclass override it? At runtime, log
  `Application.targetFrameRate` and `NetworkManager.singleton.sendRate` in `WaitingForPlayers`.
- Idle mode: check `timeScale`, `idle_mode_tickrate` and the enter/exit conditions by watching
  `Server.IdleModeActive` and `Time.timeScale` after the last player leaves mid-round.
- MEC: inspect `Timing` locally with a decompiler, and never publish the output. Check the time
  source per segment, the prewarm loops in `RunCoroutineInternal`, the no-argument
  `KillCoroutines`, `DontDestroyOnLoad` on the controller, and the SlowUpdate period.
- Dump the PlayerLoop top-level system names once. Confirm that `ScriptRunBehaviourUpdate`
  exists, because `SlClock` anchors to it and logs a fallback otherwise.
- Run sl-probe or `FrameProfiler` across an idle period and compare `Server.Tps` with the real
  period to settle the UNVERIFIED idle reading.
