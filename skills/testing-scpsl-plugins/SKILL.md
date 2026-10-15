---
name: testing-scpsl-plugins
description: "Use when about to write or change SCP:SL plugin logic beyond a one-line addition, when asked how to test, verify or prove a plugin works, when tempted to just deploy and see, before saying a plugin works, or before starting a game server for a test."
---

# Testing SCP:SL Plugins

Verified against LabAPI 1.1.7 / SCP:SL 14.2.7 (2026-10).

## Overview

Test-first, for code whose runtime you cannot start on demand. **Every decision lives where plain .NET can reach it. The live server is the last layer, and it needs the owner's consent.** Iron law as in test-driven-development: no logic without a test you watched fail. Violating the letter is violating the spirit.

## When to Use, and when NOT

| Size | Do | Skip |
|---|---|---|
| **Small**: one handler line, a log line, a text, a config default | L0 build + L3 lint; say what the owner should look for | Questions, test project, server |
| **Medium**: one feature or rule set | Seams, L1/L2 tests first, L0, L3, checklist | Server, unless wanted |
| **Large**: new plugin, IO, hot path, patches | All of it, plus an L5 plan | Nothing |

Small becomes medium when a hidden decision appears: a threshold, an exclusion, a time window, per-round state. Medium or larger with no approved design: designing-scpsl-plugins first.

## The pyramid

L0 compile against `Managed` (names exist). L1 pure Core tests (rules, math, state machines). L2 handler logic behind interfaces (decisions, resets, failure policy, allocations). L3 `scpsl-lint` (known traps). **L4 smoke on a test server: consent.** **L5 dummy load + `sl-probe`: consent.** Commands and code: [pyramid-layers.md](pyramid-layers.md). No layer proves client visuals, voice, latency or other plugins: [manual-checklist.md](manual-checklist.md).

## Process

1. **Size it.** Small ends at L0 + L3.
2. **Collect the rules that become tests.** Owners hold rules you will not guess (who is excluded, what must never happen, what a restart does). Ask once, in one message: [test-case-sources.md](test-case-sources.md).
3. **Cut the seams.** Core has no `LabApi`, `UnityEngine` or `Player`. World in through a read interface that fills a caller-owned list; effects out through a sink; time is a number; rules arrive through a `Func` (a reload replaces `Config`).
4. **RED.** One test per rule, named after it. Run it, read the failure: it must fail because the behaviour is missing. A "does nothing" test passes against an empty stub, so give each a positive control.
5. **GREEN, minimal.** Then mutate once: break a rule, expect exactly one named failure, revert.
6. **Wire the thin adapter.** It copies state into snapshots and routes events; it decides nothing. L0 and L3 to 0 errors.
7. **Hot path?** Allocation test at L2; measurement plan (measuring-scpsl-performance).
8. **L4/L5 only after the ask.**
9. **Report by layer:** what ran with its output, what could not be tested, the owner's checklist.

## Before any server: ask, never assume

Stop at the first "no". The protocol is running-a-test-server (the single copy); what L4/L5 check and may claim: [server-test-protocol.md](server-test-protocol.md).

1. Is a dedicated server installed here, and do you want local tests? No: skip L4/L5, hand over the checklist, name what stays unverified. Do not ask again.
2. Where? Offer the OS default; if the owner's path differs, ask for it.
3. Is any server here public (boot log "won't be visible on the public server list" means private)? Which ports exist?
4. One of your ports, or our own instance? Recommend our own on an unused port (say 9999). Never touch an existing port, its config, or `plugins/global` unless told (test DLLs go in `plugins/<port>`). Plugins already in `global` load on our instance too: show the list first, they may write to production.

## Compiled example

`examples/testing-pattern/`: `RewardCoordinator` behind `IPlayerSource` / `IRewardSink`, 15 tests, thin plugin. Against an empty stub 14 failed, 1 passed; removing the staff check fails exactly `StaffNeverEarn`. Run commands: pyramid-layers.md.

## Common Mistakes

| Wrong | Right |
|---|---|
| Logic in the event handler | Handler routes; Core decides |
| "Tests green, so it works" | L1/L2 prove logic; wiring is L0, L3, L4 |
| Dummy run "proves 60 players" | Dummies skip sockets, voice, hints: state the gap |

## Red Flags: stop

| Thought | Reality |
|---|---|
| "Deploy and see" | One run, one player count, no restart |
| "Port 7778 looks free" | Look for `config/<port>`, `LabAPI/plugins/<port>`, logs |
| "Small change, full process anyway" | Small is L0 + L3 |
| "Tests later" | Delete the code; start from the test |
| "Plugins can't be unit tested" | Decisions can; only wiring cannot |
