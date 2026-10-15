---
name: designing-scpsl-plugins
description: "Use when about to write a new SCP:SL server plugin or a significant feature on LabAPI or EXILED (HUD, hints, tracker, stats, anti-cheat, webhook, timers, per-player state), or when a plan includes a coroutine per player, frequent events, HTTP or database calls, cached Player objects, cleanup in Disable(), or \"it won't affect TPS\"."
---

# Designing SCP:SL Plugins

Verified against LabAPI 1.1.7 / SCP:SL 14.2.7 (2026-10).

## Overview

Every design choice is a cost multiplied by players and frames on one main thread. Answer the
checklist with numbers before writing code; if you cannot state the per-second cost, it is not
designed yet.

## When to Use

- A new plugin, or a feature adding events, timers, HUD, IO, persistent state or patches.
- Porting (with porting-exiled-to-labapi).

Not for one-line fixes or audits of existing code (reviewing-scpsl-plugins).

## Process

1. **Load model:** players, speakers, real frame rate; each trigger's catalog frequency class.
   Every hot event gets a gate or goes.
2. **Fill `design-checklist.md`** (13 items, design-note template).
3. **Start from `plugin-skeleton.md`.**
4. **Write the measurement plan first** (measuring-scpsl-performance).
5. **Review gate:** scpsl-lint with zero errors, then reviewing-scpsl-plugins.

## Quick Reference

| Decision | Default |
|---|---|
| Trigger | Lowest frequency class that answers the question (labapi-events) |
| Periodic work | One clock: `SlClock.Every` / `EveryStaggered` |
| Round state | One owner, reset at `WaitingForPlayers` |
| Keys | `UserId`, never `Player`/`ReferenceHub`/`PlayerId` across rounds |
| IO | Main-thread snapshot, then the plugin's own `Background.ForOwner` worker or `SlHttp.CreateClient` client; never wait; failure policy per status class (transient retries bounded, other 4xx stop) |
| HUD | One owner; on change plus keep-alive; client-side timers (unverified) |
| Logging | Nothing per hot raise; flag checked before formatting |
| Config | Read `Config.X` at use; guard `null` |
| Lifecycle | Idempotent `Enable`; flush on `ServerEvents.Shutdown` |
| Kill switch | Per feature at runtime; shadow mode for behaviour changes |
| Visibility filter | Reveal on the trigger's own send tick; sweeps only hide; margin includes decision age (checklist 11b) |
| Proof | Frame p95/p99 and per-job µs, never `Server.Tps` |

## Common Mistakes

| Wrong | Right |
|---|---|
| `_cfg = Config` cached in `Enable()` | Reload replaces `Config`: read at use, or override `LoadConfigs()` |
| `Config.X` unguarded | Empty `config.yml` gives `null`: `Config ??= new()` |
| Flush in `Disable()` | Never called: flush on `Shutdown`; `Enable()` idempotent (EXILED reload) |
| Subscribe first, no rollback | Fallible work first, subscribe last; on throw undo, rethrow |
| `new Version(LabApiProperties.CompiledVersion)` | Runtime value, never fails: `new Version(1, 1, 7)` |
| `HttpClient.Timeout` set on a used shared client, or `Background.TryRun`/static `SlHttp` calls on the default worker and client every plugin shares | Throws / shares a queue: your own `SlHttp.CreateClient` client (per-request timeout) and `Background.ForOwner` worker |
| Visibility culling on guessed geometry | Shadow mode, fail open, margin for both players plus latency (scpsl-networking-for-plugins) |
| Voice or footstep trigger only sets a timestamp for a 10 Hz sweep | Reveal at the trigger's own send tick (at most 1 tick); a sweep adds about 10 lost voice packets per 100 ms. Sweeps only hide, with hysteresis |
| Fixed 5 m margin, or margin without decision age | `(v_obs + v_tgt) x (RTT/2 + 2/tickrate + decision_age) + 2 m`, worst pair, speeds read at runtime |
| Retry state called "dropped" while the pending flag stays set; every non-2xx retried alike | Classify by `StatusCode` (and `Sent`): transient bounded, other 4xx stop. Deferred = will retry, dropped = never. Count items, not attempts |

## Red Flags: stop and redesign

- Hot event subscribed "to watch", or scripts subscribing at runtime.
- Coroutine or `CallDelayed` per player or per hot event.
- `.Result`, `.Wait()`, `Thread.Sleep`, file/HTTP on the main thread.
- `Player`/`ReferenceHub` in a static or a thread.
- Hints per frame, or "refresh everyone" per event.
- World access in `Enable()`.
- No answer to "what happens at round restart?"
- `Server.Tps` as success criterion; no kill switch.
- Failure policy that is one line ("retry 3 times then drop") with no status classes, terminal state or bound.
- Reveal path that waits for a polled sweep; margin stated without RTT and decision age.
