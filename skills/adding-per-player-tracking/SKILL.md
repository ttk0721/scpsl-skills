---
name: adding-per-player-tracking
description: "Use when adding anything that accumulates per-player state over time to an SCP:SL LabAPI plugin: playtime, session or visit time, AFK detection, activity or kill stats, rewards for time played, 'who was on' logs, especially at 60 players or when totals must survive rounds and restarts."
---

# Adding per-player tracking

Verified against LabAPI 1.1.7 / SCP:SL 14.2.7 (2026-10).

## Overview

Sample, don't subscribe. One staggered clock job reads every player once per period and adds seconds to totals keyed by `UserId`. Events only open and close entries. The hub is scratch space; the account is the unit.

## When to Use

- Playtime, AFK, activity, per-player counters, time-based rewards, anything persisted per account.
- Not for a one-off join log or single-event counter: subscribe and stop. A per-round tally needs only the state owner and the sampler, no journal.
- Showing numbers: adding-player-hud. Sending them to a panel: adding-external-integrations.
- Ask the owner first (brainstorming-scpsl-features): do staff and Overwatch count, does spectating, online mode, several servers, who reads the totals.

## Decisions

| Decision | Default | Change when |
|---|---|---|
| Key | `UserId` from `Player.ReadyList`, rejecting `ID_` ids | Offline server: ids are per connection, so per-hub only; say so |
| Unit | Totals per account; per-hub state holds baselines only | Per-round stats: reset the owner at `WaitingForPlayers` |
| Round reset | Reset what points into the old scene (hubs, positions, baselines). Keep what is keyed by `UserId`: totals, idle clock | Never reset the idle clock at `WaitingForPlayers`, `Joined` or `ChangedRole` |
| Driver | `SlClock.EveryStaggered<Player>`, 5 s, `Server` scope | Never a coroutine per player |
| AFK signal | Position and look deltas plus polled `ServerIsSending` | Sub-sample speech matters: gated event (afk-and-activity.md) |
| Roles | Spectator own bucket; Overwatch, `None`, Filmmaker skipped; 079 active | Owner wants staff counted |
| Persistence | Append-only journal of checksummed, batch-numbered lines; async flush; synchronous write of everything unconfirmed at `Shutdown` | Panel or database: `SlHttp`, bounded retries |
| Kill switch | `Config.IsEnabled`, read at use | none |

## The pattern

1. `Enable`: fallible work first (load journal), `SlClock.Install()`, register jobs, subscribe last. Idempotent.
2. One owner for round state (hub baselines), replaced at `WaitingForPlayers`. Totals and the per-account idle clock live outside it.
3. `Refill` once per period from `Player.ReadyList` into a reused list. `Sample` starts with `IsDestroyed` and adopts any account without an entry, so a missed `Joined` costs nothing.
4. `Joined` replaces the entry, `ChangedRole` clears the position baseline but not the idle clock, `Left` credits the open interval and removes it.
5. Time comes from the `Stopwatch` that schedules the job, clamped to two periods. Count what the clamp drops.
6. Flush packs dirty deltas into a batch with an id and hands it to the plugin's own `Background.ForOwner` worker. Keep the batch until confirmed; on error or a full queue re-send it unchanged under the same id. `Shutdown` writes everything unconfirmed on the calling thread.

The sample in code: [afk-and-activity.md](afk-and-activity.md#the-sample-in-code). Journal format, why a retry cannot double-count, locking: [accounting-and-flush.md](accounting-and-flush.md). Complete, building, lint-clean: [examples/adding-per-player-tracking](../../examples/adding-per-player-tracking/PlaytimePlugin.cs). Cost at 60 players: one player per 5 frames; prove it with `SlClock.Stats`, never `Server.Tps`. Run on a server only if the owner wants it (verifying-scpsl-work).

## Common Mistakes

| Wrong | Right |
|---|---|
| One session per `Joined`, flushed on `Left` | A restart sends `Left` for all 60. Accumulate into `UserId` totals |
| Read a custom class (UCR and similar) in `ChangedRole` and trust it | Such plugins assign their class a moment after the role change: the first read sees the vanilla role. Re-check the class shortly after (about 0.5 s) and keep a periodic check as a safety net |
| `SendingVoiceMessage` as "50 packets/s, allocation-free" | 100/s per speaker, allocates per raise. Poll `ServerIsSending` |
| Wall-clock tick on default-segment `WaitForSeconds` | Idle mode stretches it ~100x. Use `SlClock` |
| Keep-alives "staggered by join time" | A restart starts everyone in one frame. `EveryStaggered` |
| Rejoin does `_states[id] = new State()` | Wipes unflushed seconds. Totals outlive hubs |
| Pending zeroed on flush, never restored | Seconds vanish on failure. Keep the batch and re-send it |
| Failed batch merged back into pending, written under a new id | A partial write already put part in the file: counted twice. Same id, dedupe on read |
| Journal line = split on tabs + `double.TryParse` | A line cut mid-number parses as a smaller value. Checksum plus a required newline |
| Shutdown flush leaves the batch to the worker, or flushes in `Disable()` | The worker dies with the process and `Disable()` is never called. Write unconfirmed batches synchronously on `Shutdown` |
| Staff, lobby, Overwatch counted unasked | Ask. `None` and Overwatch have no FPC state |
| AFK timer reset at restart or respawn, or "first sample after a baseline counts as present": free credit every round | The idle clock is per `UserId` and survives both. A fresh baseline is not movement. Only a long absence resets it |

Mouse-jiggle defeats position and look sampling. Say so.

## Domain skills

scpsl-game-lifecycle, scpsl-main-thread-and-timing, labapi-wrappers (`ReadyList` is lazy), labapi-events, scpsl-voice-and-audio, scpsl-roles-damage-effects.
