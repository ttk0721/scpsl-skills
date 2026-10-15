---
name: reviewing-scpsl-plugins
description: "Use when reviewing, auditing or approving the source of an SCP:SL plugin (LabAPI, EXILED) or a pull request to one; when TPS drops, frames hitch or players rubber-band and a plugin is suspected, even with few players online; when the console floods with logs or exceptions; or when a plugin misbehaves only after a round restart."
---

# Reviewing SCP:SL plugins

Verified against LabAPI 1.1.7 / SCP:SL 14.2.7 (2026-10).

## Overview

Rank by damage on a full server: rate x work per call. One line on a per-pair event outranks fifty style issues. A finding without a rate, a line or a measurement is a guess.

## When to Use

- Code review, audit, "is this safe for 60 players", pre-deploy check of plugin source.
- Lag, log floods or round-restart bugs with a plugin suspected.

Not for: new designs (designing-scpsl-plugins), profiling without source (measuring-scpsl-performance), load failures (deploying-scpsl-plugins).

## Procedure

1. **Build it** against the server's `SCPSL_Data/Managed` assemblies.
2. **Run scpsl-lint on the whole project**: `python3 tools/scpsl-lint/scpsl_lint.py <dir>`. Read each hit.
3. **Walk review-checklist.md top to bottom.** Impact order: hot events and per-frame work first, config traps last.
4. **Confirm suspicions by measurement, never by `Server.Tps`.** **REQUIRED SUB-SKILL:** measuring-scpsl-performance.
5. **Report** in review-report.md format: severity, rate, evidence, fix, confidence, and what you could not check.

## Example: diagnostics block

A diagnostics block that forces verbose logging on and installs a process-wide `FirstChanceException` handler can drop reported TPS from 60 to about 10 at 3 players (smoothed readout). Cost tracked exceptions and log lines, not players. Grep every review for `FirstChanceException` and hard-coded debug flags. Analysis: review-report.md.

## Common Mistakes

| Wrong | Right |
|---|---|
| One try/catch around a whole tick loop, handlers unguarded | One throwing player aborts the tick. Require try/catch per job and handler with own logging: LabAPI's error header names the last subscriber, not the thrower. |
| `Enable()` subscribes before seeding state | Subscribe last; a throw midway leaves earlier subscriptions live. |
| Assuming it builds | `using UnityEngine;` + `using LabApi.Features.Console;` + bare `Logger.` is likely CS0104 (`UnityEngine.Logger` is public). Compile to confirm. |
| Trusting config docs | Find the code behind each option. A `Debug` flag whose counters nothing prints is a finding. |
| Approving an enforcing feature (anti-ESP, auto-kick) as written | Require shadow mode first: compute, don't apply, log rates, audit samples. |
| Citing `Server.Tps` | Smoothed and clamped; hides hitches. Measure frame time. |
| "Might be slow" | Give rate x players x work, or a number. |

Facts behind other findings: scpsl-game-lifecycle, labapi-events, scpsl-hints-and-hud.

## Red Flags

- "Lint is clean." It misses helpers, config, runtime subscriptions.
- "It's only debug code." Debug code can carry the dominant cost (forced verbose logging, `FirstChanceException`).
- "Fine on the test server with 3 players." Per-pair cost grows with N squared.
- "The handler is tiny." x 212 000 raises/s it is not.
- Stopping at the first S1 finding.
