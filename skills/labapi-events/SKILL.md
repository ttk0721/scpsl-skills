---
name: labapi-events
description: "Use when subscribing to or handling a LabAPI event (PlayerEvents, ServerEvents, Scp*Events, CustomEventsHandler, EXILED-to-LabAPI handlers), or when a cancel, IsAllowed or setter has no effect, a handler fires twice or never, the error log blames the wrong plugin, a locked door ignores your edit, PreAuthenticating crashes, or TPS/GC gets worse after adding a voice, visibility, radio, hazard or tesla handler."
---

# LabAPI events

Verified against LabAPI 1.1.7 / SCP:SL 14.2.7 (2026-10).

## Overview

A static multicast delegate raised synchronously inside a game method. Every subscription costs at the event's frequency. A setter matters only if the game reads the field back.

## When to Use

- Before any `+=` or `CustomEventsHandler` override; when an edit or cancel has no effect.
- Small addition (one non-hot handler, one setter): use the Quick Reference and skip the rest; the full pass is for hot, off-thread, cancelling or re-entrant handlers.
- Not for round timing (scpsl-game-lifecycle) or behaviour no event covers (scpsl-harmony-patching).

## Before every subscription

1. Look the event up in `events-catalog.md`: frequency, `hot`, cancellable, `ignoredSetters`, `pair`.
2. **Hot** (per-frame, per-pair, per-packet): don't subscribe unless needed, and then only while needed. One test or one dictionary write per handler; never log, allocate or throw; a `catch` logs rate-limited only. `scpsl-lint` SL001 flags it.
3. `never-fired` means the handler never runs (SL002). Check that a setter you rely on is honoured (SL003).

Detail for every row below, plus gate code: `event-mechanics.md`.

## Quick Reference

| Fact | Consequence |
|---|---|
| Args are built and a `Delegate[]` allocated per raise | Never free, even with zero subscribers |
| A counter-only handler on `ValidatedVisibility` is cheap, while one string + one list + one lookup per raise costs several times more (numbers: tools/hot-bench/README.md) | Per-raise work dominates (logs, locks, allocations, LINQ, throws). Gate, keep the handler trivial (scpsl-engine-cost-model) |
| Each handler has its own try/catch | Log header names the **last-registered** handler: read the stack trace |
| `IsAllowed` does not short-circuit. Last writer wins | Only write `false`. `if (!ev.IsAllowed) return;` sees earlier cancels only |
| `Priority` is a byte, lower enables first | `Lowest` gets the last word |
| `PreAuthenticating`/`PreAuthenticated` run on `ConnectionRequestThread` | No Unity, `Player` or MEC there. Use a concurrent queue |
| Re-entrancy is unguarded | `SetRole` inside `ChangingRole` recurses. Edit `ev.NewRole` |
| `-ing`/`-ed` are not 1:1 | `InteractedDoor(false)` follows a cancelled `InteractingDoor`. `Hurt` fires even when damage was `Nothing` |
| Pooled args members (`AffectedPlayers`, `WaveRespawning.Roles`) | Copy inside the handler. Never store `ev` |
| `CustomEventsHandler` registration costs a reflection pass per event | Register once in `Enable()`, never the same instance twice |

## Compiled examples

`examples/labapi-events-snippets/` (builds; lint: 0 errors, one SL001 warning for the deliberate `Hurting` subscription); key parts in `event-examples.md`.

| Need | File |
|---|---|
| Both styles, unsubscribed correctly | `StaticEventWiring.cs`, `CustomHandlerWiring.cs`, `EventsSnippetsPlugin.cs` |
| Hot handler: early return, unsubscribe when idle | `VoiceActivityGate.cs` |
| Cancel, mutate the same object, `CanOpen`, `NewRole` | `CancellationRules.cs` |
| Off-thread event (`PreAuthenticating`) | `PreAuthBridge.cs` |

## Common Mistakes

- **A voice handler described as "~50 packets/s, allocation-free".** `SendingVoiceMessage` fires 100 times/s per speaker and allocates each raise: gate it (`VoiceActivityGate.cs`) and state the cost. `ReceivingVoiceMessage` fires per packet **per listener**: avoid it.
- **Cancelling, then toggling `door.IsOpened`, to open a locked door.** `InteractingDoor.CanOpen` is the lock verdict: set `ev.CanOpen = true`. Cancelling still plays denial feedback.
- **`ev.IsAllowed = !banned`.** This un-cancels other plugins. Write `if (banned) ev.IsAllowed = false;`.
- **Reassigning `ev.DamageHandler` in `Hurting`.** The game ignores it. Mutate the existing handler object.
- **Trusting LabAPI's error header.** Wrap handlers in your own try/catch, rate-limited log.
- **An unbounded `Log.Error("..." + e)` in the `catch` of a frequent handler.** A persistent fault logs once per raise. `Hurting`/`Hurt` count. Use `RateLimitedLog.Error(key, () => ...)` (SlKit).
- **Subscribing before fallible setup.** If `Enable()` throws, earlier subscriptions stay live and `Disable()` never runs (designing-scpsl-plugins). Subscribe last.
- **Treating `Joined`/`Left`/`RoundRestarted` as once-only.** They re-fire every round (scpsl-game-lifecycle).

## Red Flags

- You subscribed without opening the catalog, or call a hot handler "only a lookup, so free".
- A `catch` calling `Logger.*` or `Log.*` unthrottled in a handler for a per-action or faster event.
- You write `IsAllowed = true`, or cancel and then perform the action by hand.
- You call a game API inside the `-ing` handler of that same action.
- You touch `Player` or Unity in `PreAuthenticating`.
- You subscribe a lambda you plan to remove, or from a per-round or per-player object.
