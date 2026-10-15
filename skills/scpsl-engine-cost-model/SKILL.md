---
name: scpsl-engine-cost-model
description: "Use when estimating where an SCP:SL server frame goes at 40-100 players, choosing a TPS target or alert threshold, or pricing a LabAPI plugin feature (event handler, per-player coroutine, hint, spawned toy, Harmony patch, log line) before writing it. Symptoms are low TPS, lag at round start or warhead, rubber-banding."
---

# SCP:SL Engine Cost Model

Verified against LabAPI 1.1.7 / SCP:SL 14.2.7 (2026-10).

## Overview

A frame is a fixed budget (16.7 ms at 60 TPS, 50 at 20). The game spends most of it on O(N^2) and O(P x O) loops a plugin cannot remove, so add almost nothing to them. Price features as calls per second x cost per call.

Tags: **[M]** measured, **[C]** counted from code, **[E]** estimate, **[U]** unverified. Untagged times are [E]; no full-load profile exists.

## When to Use

- Choosing a TPS target or alert threshold; judging whether a handler, loop, HUD, spawn or patch is affordable; explaining round-start, mass-death or map-load spikes.
- Not for measuring (measuring-scpsl-performance), wire filtering (scpsl-networking-for-plugins), patch mechanics (scpsl-harmony-patching), event semantics (labapi-events).

## Quick Reference (100 players)

| Part | Scales with | Cost | Plugin can change it? |
|---|---|---|---|
| `WriteAll` positions | every frame, up to 9,900 pairs [C] | 3-7 ms, ~600k allocs/s at 60 TPS | Only adds per-pair cost |
| Mirror `Broadcast` | (P+1) x O per tick [C] | 1.4-4 ms at O 700-1,000; 50-100 ms at 25k | Each spawned identity: +3 us/tick |
| Animator, PhysX, callbacks | fixed work | callbacks 1-3 ms; animator 45-225 ms/s [U] | No. Keep `fixedDeltaTime` |
| Voice | 100 packets/s per speaker [C] x 101 hubs | 35-130 ms/s at 10-20 speakers | Subscriptions add to it |
| GC | Boehm, whole live heap | cycle per ~0.33 x heap allocated | Only by allocating less |
| Mass events | one frame | warhead 45-160 ms (~300 at 80 deaths), round start 60-180 ms | Add nothing there |

See tick-budget.md, plugin-action-costs.md, evidence-status.md.

## Rules

1. **Count calls, then multiply.** Per-pair code runs up to 9,900 times per send, ~212k times/s at 60 players and 60 TPS [C]. Budget 20-50 ns per pair: flat array by `PlayerId`, no dictionary, no Unity `==`, no allocation.
2. **Not subscribing still costs.** The game builds the args and wrapper lookups per raise anyway; a subscriber adds a `Delegate[]` per raise plus your handler.
3. **A spawned identity is a permanent per-tick tax.** `IsStatic` for still toys; spawn in byte-sized slices (~134 B per toy per player [C]).
4. **One clock, no per-player coroutines, stagger bursts, no work in spike frames.** **REQUIRED SUB-SKILL:** scpsl-main-thread-and-timing.
5. **Compare to the frame target in force; measure wall clock.** **REQUIRED SUB-SKILL:** measuring-scpsl-performance.
6. **No logging on hot paths** (`Logger` walks the stack and runs regexes). Transpile, don't prefix, hot methods.

## Realistic TPS targets (estimates)

60 TPS at 80-100 players is unrealistic. 30 is plausible with no spike headroom. 20 needs the tickrate override applied, hot-path plugin overhead removed, O under 2-3k (or Broadcast patched) and uplink of 150-200 Mbit/s or more. "Stable": p95 frame <= 50 ms, p99 <= 80 ms.

## Common Mistakes

- **Alert at a fixed 30 TPS.** Fires forever on a server meant to run at 20. Mirror resets the frame target to 60 after `StartHost`; the configured `server_tickrate` returns after the first idle exit or config reload. Alert on a ratio to `Application.targetFrameRate`; idle is no data.
- **Calling a per-pair hook "one array lookup".** Two Unity `== null` checks, two `Dictionary<int,int>` lookups and a `bool[,]` read per pair cost a multiple of the 20-50 ns design (~0.2-0.5 ms/frame).
- **No dummy exemption.** Dummies are receivers and targets in `WriteAll` (their send is a no-op). Skip dummy receivers in per-pair work.
- **Rays per hidden pair without a budget.** Up to 12 Linecasts per pair, 1,440 rays/frame at a 120-pair cap; cost unmeasured. Microbenchmark first.
- **Voice event "cheap".** 100 packets/s per speaker, not 50: ~1,000 `Delegate[]`/s for one `SendingVoiceMessage` subscriber at 10 speakers; `ReceivingVoiceMessage` also fans out per listener.
- **Synchronized HUD keep-alives** after a restart or mass join: stagger.
