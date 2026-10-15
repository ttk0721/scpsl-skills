---
name: adding-round-mechanics
description: "Use when adding custom round flow to an SCP:SL LabAPI plugin: holding the lobby, a custom or forced round end, a round time limit, blocking or delaying respawn waves, per-round timers, round announcements, or state that must reset each round. Also when the lobby will not hold, Round.End does nothing, or a cancelled wave event fires every frame."
---

# Adding round mechanics

Verified against LabAPI 1.1.7 / SCP:SL 14.2.7 (2026-10).

## Overview

Round hooks rarely do what their names say. Hold with the locks, end with gate plus verdict, delay waves by moving their timer, reset per-round values at three points.

## When to Use

- Lobby hold, forced or custom round end, time limit, wave control, round timers, round announcements.
- Not for: one broadcast at round start or a counter reset (scpsl-game-lifecycle rules suffice), event args (labapi-events), timers in general (scpsl-main-thread-and-timing).

## Decisions

| Decision | Choose | Because |
|---|---|---|
| Hold the lobby | `Round.IsLobbyLocked` plus a max-hold valve | Cancelling `RoundStarting` neither holds nor delays and leaves `Round.Duration` at 0 |
| End on your rule | `Round.End(true)` and `CanEnd = true` in `RoundEndingConditionsCheck` | Each alone is undone or never reached |
| Block an end | `CanEnd = false` (vanilla resumes) or `Round.IsLocked` (check never raised) | Depends on whether vanilla should return |
| Stop a wave | Cancel `WaveTeamSelecting`, set `RespawnWave.TimeLeft` | `WaveRespawning` still burns a token; a bare cancel re-fires every frame |
| Round timers | `SlClock.After(.., ClockScope.Round)` plus your own `Stopwatch` | MEC waits stretch ~100x in idle; `Round.Duration` can be 0 |
| Announce | One broadcast per event; `Announcer.Message(text, subtitles)` | Plain `Message(text)` shows no subtitles |
| Statics you set | Track ownership, release only yours | Locks survive restarts |

## The pattern

1. One owner holds plain per-round values: flags, a `Stopwatch`, job handles. No `Player`.
2. Reset in `WaitingForPlayers`, `RoundRestarted` (can fire twice) and `RoundEnded` (Round-scoped jobs outlive it by about 10 s). Idempotent.
3. Rebuild at `WaitingForPlayers`. Start round clocks at `RoundStarted`; roles are still `None` there.
4. Handlers stay cheap and read config at use time. The end check runs every 2.5 s.
5. Subscribe last in `Enable`; release locks and jobs in `ServerEvents.Shutdown`.
6. Every feature has an off value, plus one kill switch.

## Key parts of the example

[RoundMechanics.cs](../../examples/adding-round-mechanics/RoundMechanics.cs) is a complete plugin (lobby hold, time limit with warnings, end floor, wave suppression).

```csharp
Round.IsLobbyLocked = true; _ownsLobbyLock = true;         // the lock is the hold
_limitReached = true; Round.End(true);                      // opens the gate
if (_limitReached) ev.CanEnd = true;                        // verdict, in the check handler
ev.IsAllowed = false; wave.TimeLeft = WaveRecheckSeconds;   // cancel AND move the timer
```

[RoundVariants.cs](../../examples/adding-round-mechanics/RoundVariants.cs) has compiled fragments (objective end, round lock, token grant). Reset table: variants.md.

## Common Mistakes

| Wrong | Right |
|---|---|
| Cancel `RoundStarting` to hold the lobby | `IsLobbyLocked`; a release starts a fresh `lobby_waiting_time` |
| `Round.End(true)` alone, or a `CanEnd` handler alone | Both |
| Cancel `WaveRespawning`, or cancel `WaveTeamSelecting` and leave the timer | Cancel at selecting and push `TimeLeft`, or one ready wave starves the rest |
| Round flag cleared at `RoundEnded` and `WaitingForPlayers` only | Also `RoundRestarted`, or it stays true through the restart window |
| Anonymous lambdas on round events; counters reset only in `RoundStarted` | Named handlers removed in `Shutdown`; reset at the three points |
| Limit built on `Round.Duration` or MEC waits | Own `Stopwatch`, `SlClock` |
| Assuming the round ends when the last human leaves | It does not by default; idle can start mid-round |
| Lock taken, release tied to a cancelled job | Own flag; release on start, timeout, `Shutdown`; never take another owner's lock |
| Min-player count from `ReadyList` | Includes dummies; count `IsPlayer && IsReady` |
| Items in `RoundStarted`, the old `Cassie` wrapper, hooking `CassieAnnouncing` | `ChangedRole`; `Announcer`; that event is never raised |

## Verify

The round-flow traps are static traces. Suggest probes for lobby release, forced end and wave cancel (count firings); never push them. Ask first whether a test server is wanted, check whether the local one is public, and use our own instance on a free port, never the owner's (verifying-scpsl-work holds paths and procedure).

## Domain skills

scpsl-game-lifecycle, labapi-events, labapi-wrappers, scpsl-main-thread-and-timing, scpsl-hints-and-hud, designing-scpsl-plugins.
