---
name: writing-scpsl-plans
description: "Use when an SCP:SL plugin feature has an approved spec or clear requirements and needs more than one task, adds events, timers, HUD, HTTP or database IO, persistent state, commands or Harmony patches, or will be built by another agent or session, before writing plugin code."
---

# Writing SCP:SL Plans

Verified against LabAPI 1.1.7 / SCP:SL 14.2.7 (2026-10).

## Overview

A plan someone with zero SCP:SL context can execute: exact files, code, commands, expected output, **Global Constraints** every task inherits, a **file layout** testable without a server, **gates** ending every task.

Announce: "I'm using writing-scpsl-plans to write the implementation plan."
Save to `docs/plans/YYYY-MM-DD-<feature>.md` in the plugin repository unless the owner prefers elsewhere.

## When to Use

- Path C of brainstorming-scpsl-features, after the owner approved the written spec (`docs/specs/...-design.md`).
- Work handed to another agent or session.

**Not for paths A and B.** A (a log line on join): domain skill, build, lint, at most one question. B (a new config key, command, reworded text): the 1-4 questions and in-chat design of brainstorming-scpsl-features, the `adding-*` recipe, build, lint. No plan for either. New feature, no approved spec: brainstorming-scpsl-features first.

## Inputs, copied from the spec

- Context (peak players, EXILED or LabAPI, neighbours on the same surface, shared data, restart cadence, idle hours), business rules, edge-case and lifecycle tables, cost budget, failure policy.
- `SCPSL_Data/Managed`: the default in plan-template.md if it exists on this machine, else ask once and reuse the answer.
- **Test answer:** "no", "own private instance on port N", or "owner's port N, on request". Missing: ask once now. A no is final (running-a-test-server).

A gap the spec leaves open is one question to the owner, never a silent assumption.

## Process

1. **Scope:** one plan per independently shippable subsystem.
2. **Header and Global Constraints** (plan-template.md), verbatim from the spec.
3. **File map:** `Core/` pure logic, thin wiring, one owner of per-round state.
4. **Tasks** of 2-5 minute steps: failing Core test, run (FAIL), implement, run (PASS), wire, gates, commit. Each task names its recipe skill (`adding-*`) and domain skills, and has an **Interfaces** block with exact signatures. Look up every LabAPI/SlKit member now; unverifiable means a research task first (researching-scpsl-internals).
5. **Gates** end every task (below).
6. **Final tasks:** test server only with a recorded yes; always deploy-and-verify (deploying-scpsl-plugins, verifying-scpsl-work).
7. **Self-review**, then hand off.

Worked task: example-task.md, compiled as `examples/activity-time`.

## Gates

| Gate | Pass |
|---|---|
| `dotnet build <P>.csproj -c Release` against Managed | 0 errors |
| `dotnet run --project tests/<P>.Core.Tests -c Release` (no game references: its build is the purity check) and `grep -rEn "^using (LabApi|UnityEngine|Mirror)" Core` | exit 0; grep silent |
| `scpsl_lint.py <plugin dir>` | 0 errors; each suppression has a reason |
| **Hot path only** (per frame, per player per period, per-action-or-faster handler): clock cost and sl-probe A/B (measuring-scpsl-performance) | inside the budget row |

## Self-Review

1. Every requirement, business rule and edge-case row has a task with a test.
2. Every `+=` has a `-=` of the same named method (no lambdas) in teardown; every state field matches the lifecycle table.
3. Every event is in the budget table with its catalog class; hot ones gated.
4. Off-thread code gets snapshots only; flush on `ServerEvents.Shutdown`.
5. Every IO task states the spec's failure policy: retries, idempotency key, deltas restored on failure, max versus sum.
6. Names match across Interfaces blocks.
7. Review Focus: the 5 likeliest untested conditions (restart mid-operation, leave mid-period, idle mode, peak load, neighbour plugin, IO down), each with a test.

## Handoff

"Plan saved to `docs/plans/<file>.md`. Please review it; then I'll execute it with executing-scpsl-plans." Wait for the review.

## Common Mistakes

| Wrong | Right |
|---|---|
| "Handle round restart", "make it thread-safe", "verify API name", "check performance" | Name the event, the snapshot, the member, the metric and threshold |
| Test-server task by default, or on 7777 | Only with a recorded yes; own instance on an unused port |

## Red Flags

| Thought | Reality |
|---|---|
| "The owner wants code, skip the plan" | Multi-task code without a plan ships broken |
| "Needs the game, so no unit tests" | Only wiring needs the game. Move decisions to `Core/` |
| "I'll confirm names while implementing" | A guessed name fails the build or binds the wrong member |
| "Their server is set up, use it" | Ask first. Their ports are their production |
