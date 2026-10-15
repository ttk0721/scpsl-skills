---
name: scpsl-game-lifecycle
description: "Use when SCP:SL LabAPI plugin code depends on timing or round state - what exists in Enable, WaitingForPlayers, RoundStarted, Joined, Left, Shutdown or after a round restart; stale Player or Server.Host, state that resets or duplicates each round, session or visit tracking keyed by UserId (offline mode), flushing state on shutdown, roles None at round start, idle mode, RoundStarting, Round.End or wave cancel having no effect."
---

# SCP:SL Game Lifecycle

Verified against LabAPI 1.1.7 / SCP:SL 14.2.7 (2026-10).

## Overview
A round restart is a scene reload: every hub, `Player` wrapper, room, door and pickup is rebuilt, while statics, subscriptions, Harmony patches and MEC coroutines survive. Key what you keep by `UserId` and reset the rest yourself.

## When to Use
- Code runs in `Enable`, `WaitingForPlayers`, `RoundStarted`, `Joined`, `Left` or `RoundRestarted`.
- Symptoms: `MissingReferenceException` or ghost players after a round, `Left` for everyone at once, handlers running twice, role `None`, lobby will not hold, round will not end.
- Not for: event args (labapi-events), timers and threads (scpsl-main-thread-and-timing), wrappers (labapi-wrappers), load errors (deploying-scpsl-plugins).

## Quick Reference
`Enable` has no players, `Server.Host`, rooms or `Round.*`. Roles are `None` in lobby and `RoundStarted`; use `ChangedRole`. `RoundRestarted` can fire twice. `Disable()` is never called at shutdown.

Tables and details: [skill-details.md](skill-details.md), [timeline.md](timeline.md), [restart-and-state.md](restart-and-state.md), [players.md](players.md), [round-flow.md](round-flow.md), [idle-config-dummies.md](idle-config-dummies.md), [plugin-boot.md](plugin-boot.md).

## Rules
1. Rebuild per-round state in `WaitingForPlayers`; make it idempotent.
2. Never hold `Player`, hub or `Server.Host` across a restart. Key by `UserId`, not `PlayerId` or `netId`. `UserId` is an identity only for authenticated online-mode players: offline gives non-empty `ID_Offline_<netId>_<unix>`, dummies share `ID_Dummy`, so null/empty checks do not filter them.
3. `Joined` and `Left` are per hub, so per round. They are not connection events.
4. Stop old-round timers on `RoundRestarted` and `RoundRestart.OnRestartTriggered`, idempotently.
5. Do gameplay setup in `ChangedRole`, not `Joined` and not `RoundStarted`.
6. Hold the lobby with `Round.IsLobbyLocked`. End a round with `Round.End(true)` plus `CanEnd = true`.
7. Send hints and messages to `Player.ReadyList`.
8. Pre-auth handlers run off the main thread: no Unity, no `Player`.
9. Subscribe last in `Enable`, keep it idempotent, flush in `ServerEvents.Shutdown`. Exit ends every visit and statics die, so close open sessions there and mark them terminal (reason `shutdown`).
10. Read `Config.X` at use time; reload replaces it.
11. Re-apply `Server.PauseIdleMode` each round.

## Common Mistakes
| Wrong | Right |
|---|---|
| One session per `Joined`, flushed on `Left`, reported as a visit | Restarts send `Left` for all. Decide per-hub or per-visit; for visits park on `Left`, resume on `Joined` by `UserId`, flush after a grace period |
| Keying visits by `UserId` guarded only by `IsNullOrEmpty` | Offline ids are non-empty and new per hub. Reject `ID_*`, state the online-mode assumption, fall back to per-hub |
| `OnShutdown` spools `online`/`parked` sessions, next boot posts them | No `left` will ever come. Close them in the flush with a terminal record (`left`, reason `shutdown`) |
| Hard-coding `Role = "Unknown"` in `Left` | Wrapper is complete in `Left`; read `Role`, `UserId`, `Nickname` there or cache on `ChangedRole`. With `disconnect_drop` an alive leaver is already Spectator |
| Clearing round state only on `RoundEnded` and `WaitingForPlayers` | Also `RoundRestarted` (can fire twice) and `RoundRestart.OnRestartTriggered` (only one `softrestart` raises) |
| Per-`PlayerId` cache reused on a new join | Ids recycle at restart. Reset on `Joined`, `Left`, round start |
| Giving items in `RoundStarted` after a guessed 1 s delay | Roles are `None` there. Use `ChangedRole`; a delay leaves a window for death or leave first |
| Cancelling `RoundStarting` to hold the lobby | Neither holds nor delays, leaves `RoundLength` 0. Use `IsLobbyLocked` |
| `Round.End(true)` alone, or a `CanEnd` handler alone | Do both |
| Cancelling `WaveRespawning` | Token and timers still spent. Cancel `WaveTeamSelecting` (re-fires each frame, keep cheap) |
| Trusting `Server.Tps` or MEC waits on an empty server | Idle mode (timeScale 0.01) can start mid-round. Treat `IdleModeActive` as no data |

Tools: scpsl-lint SL015 (cleanup only in `Disable`); SlKit `ClockScope.Round` (`SlClock.Install` ends those jobs at `RoundRestarted`).
