---
name: executing-scpsl-plans
description: "Use when an approved multi-task implementation plan for an SCP:SL server plugin (LabAPI or EXILED) is ready to execute, when splitting plugin work across subagents or deciding which plugin tasks may run in parallel, or when dispatching implementer or reviewer agents for SCP:SL plugin code."
---

# Executing SCP:SL plans

Verified against LabAPI 1.1.7 / SCP:SL 14.2.7 (2026-10).

## Overview

Run an approved plan task by task: a fresh implementer per task, two reviews each (spec, then engine), one integration gate at the end. Adapted from subagent-driven-development.

**Core principle:** parallelize what owns no game state; serialize everything that touches the plugin class, event wiring or per-round state.

## When to Use

A plan from writing-scpsl-plans (path C of brainstorming-scpsl-features) with three or more tasks.

Not for:
- **Paths A and B** (a log line on join, one handler, one config field): implement directly with the domain skill (B: plus its `adding-*` recipe), then build and scpsl-lint. No plan, no subagents.
- No approved design: brainstorming-scpsl-features first.
- A bug or failing gate: debugging-scpsl-plugins, then resume.

## Process

1. **Setup.** Read plan and spec once. Keep `progress.md` beside the plan: a `Task N: complete` line per task, each deviation as `Ruling: <decision> — <why> — <cost if wrong>`. After context loss trust the ledger, not memory.
2. **Test answer.** Read the plan's `Test server:` line (the spec's "local tests?" answer). Missing: ask once: local server at all, its path if not the default in running-a-test-server (Linux and Windows paths), an existing port or, recommended, our own instance on a free port such as 9999. "No" is final: ledger `Smoke: declined`.
3. **Lane the tasks** (parallel-lanes.md). Parallel only with disjoint files and interfaces fixed in the plan.
4. **Dispatch** implementer-prompt.md, naming the task's recipe (the plan's, else from the using-scpsl-skills index) and domain skills. NEEDS_CONTEXT: answer, re-dispatch. BLOCKED: more context, a stronger model or a smaller task, never an unchanged retry.
5. **Review 1, spec** (reviewer-prompts.md): missing, extra, misunderstood, every owner business rule.
6. **Review 2, engine**, after review 1 passes: scpsl-lint on the whole project, then the reviewing-scpsl-plugins checklist sections the diff touches.
7. **Fix loop:** rounds 1-3 resume the implementer with findings verbatim; rounds 4-5 a fresh implementer one tier up. Unexplained failure: debugging-scpsl-plugins. After round 5, rule on each open finding.
8. **Integration gate:** every project builds with 0 errors against `SCPSL_Data/Managed`, every test project passes, the plan's Core-purity grep is silent, scpsl-lint 0 errors per plugin directory, no guessed API name or "verify" comment, hot-path budget rows measured or ledgered `not measured`. Then one whole-plan review, strongest model.
9. **Smoke run** only on a yes from step 2, on the instance the owner chose: our own, or their port only on an explicit yes, never one running or holding players. Run `check-private.sh` first; a public one is never used (running-a-test-server).
10. **Finish:** verifying-scpsl-work, then deploying-scpsl-plugins.

Inline (no subagent tool): same order; each task still ends with build, lint, tests.

**Stop and ask only for:** the owner's server, ports or configs; a production deploy; anything acting on real players; an irreversible step; a plan where every path is a guess. Otherwise rule, ledger, continue.

## Model tiering

Every dispatch names a model and one sentence why (implementer-prompt.md); an omitted model inherits the expensive session model.

| Tier | Use for |
|---|---|
| Haiku | the plan contains the code; config fields; same-shape batches |
| Sonnet | implementing from prose, task reviews, extracting facts from LabAPI source |
| Strongest | design changes, thread boundaries, Harmony, hard debugging, the final review |

## Common Mistakes

| Wrong | Right |
|---|---|
| "Lint is clean" as the engine review | Lint misses helpers and runtime subscriptions; walk the checklist |
| Fire-and-forget flush in `Disable()` | Never called; flush synchronously on `ServerEvents.Shutdown` |

## Red Flags

| Thought | Reality |
|---|---|
| "These two handler tasks are independent." | Both edit `Enable()` and subscription order. Serialize. |
| "Mark the API name `// verify`." | Guessed names fail the gate. Find it in LabAPI source or the build. |
| "No answer about tests, run them anyway." | No answer is no. |
| "Their server on its usual port is right there." | Ports are never borrowed unasked; own instance on a free port. |
| "Tiny diff, skip the engine review." | One hot-event line outweighs the rest of the plan. |
| "Should I continue?" between tasks | Execute; only the stop list stops you. |
