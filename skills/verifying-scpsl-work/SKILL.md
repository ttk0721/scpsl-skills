---
name: verifying-scpsl-work
description: "Use when about to say an SCP:SL plugin change is done, working, fixed, fast, safe or deployed; before reporting to the owner, committing or opening a PR; when tempted to write 'should work', 'it compiles so it is done' or 'TPS looks fine'; when deciding whether to run anything on a local test server."
---

# Verifying SCP:SL work

Verified against LabAPI 1.1.7 / SCP:SL 14.2.7 (2026-10).

## Overview

**Evidence before claims.** Every sentence of your summary that says "works", "fast", "survives restarts" or "deployed" needs output you read this turn. The SCP:SL twist: most evidence exists only on a running game server, and that server may not be yours to run. Prove what you can; say plainly what you could not, with the check the owner can run.

## When to Use

Before any completion claim, commit, PR or hand-off. Scale to the change:

- **Small change** (a log line, a default, a typo): build, lint, one sentence: "not run on a server; join once and look for `<line>`". No test server, no perf run, no questions.
- **New feature or behaviour change:** the full table; test-server rows only if the owner agreed.
- **"No lag", "cheap", any cost claim:** measured before and after, always.

## Claim to evidence

| Claim | Evidence (read it, fresh) | Does not count |
|---|---|---|
| Compiles | `dotnet build -c Release` against the server's `Managed` folder: `0 Error(s)`. No install to reference: say "not compiled against Managed" | A net48-pack build |
| Unit tests pass | Runner output, exit 0, failure count (pure logic only) | An earlier run |
| Lint clean | `scpsl_lint.py <whole project dir>` exit 0; each suppression has a reason | A single-file scan |
| Loads | `Successfully enabled '<Name>'` in the boot log of THIS build | The DLL sitting in the folder |
| Handler works | The handler's own log line (or its `FrameProfiler.Mark` row in the sl-probe CSV) during a test that fired the event. `Joined` needs a real client | "Enabled" (a swallowed `Enable()` failure still prints it); a dummy for a join handler |
| Cheap | sl-probe p95/p99/% over 50 ms before and after, same setup, 3+ repeats; job microseconds from `SlClock.Stats` | `Server.Tps`, one run, a guess |
| Survives round restart | Restart on the test server: reset line at `WaitingForPlayers`, state zeroed, handler fires in round 2 | Reasoning about statics |
| Deployed | DLL in the right folder of the TARGET server, one copy, enabled line from its boot | A green test on another port |

Commands, trigger recipes, before/after method: evidence-recipes.md.

## Process

1. **List your claims.** Each gets an evidence row or a line under "not verified".
2. **Run the cheap checks now**: build, tests, lint. Read the whole output.
3. **Runtime claims need a server, so ask first.** Does the owner have one locally, and want local tests at all? "No" or "don't know": claims stay unverified, the owner gets the check. Never push tests on anyone. Procedure, paths, ports and privacy check: running-a-test-server (the single copy); what a run counts as evidence: test-server.md.
4. **Make the feature provable:** a log line at first handler run, `FrameProfiler.Mark`, a named `SlClock` job, a state-reset line at `WaitingForPlayers`. [examples/verifiable-handler](../../examples/verifiable-handler/VerifiableHandler.cs) does all four; it needs SlKit in `dependencies/` (without it: `Logger` line plus `Stopwatch`).
5. **Report** per unverified-claims.md: Verified (command, result), Not verified (why, owner check), Environment gaps.

## Common Mistakes

| Wrong | Right |
|---|---|
| Starting a server "just to check" | Ask first |
| Testing on port 7777, or "it is not public, so it is free" | Own instance on an unused port, after asking which they prefer; confirm it is private |
| "Enabled line seen, handler works" | Fire the event, read the handler's own line |
| `/dummy spawn` to test a join handler | Dummies raise no `Joined`: real client, or NOT VERIFIED |
| Perf verdict from 30 dummies, or in idle mode | State the gap (no sockets, auth, voice); players or dummies connected, drop the first minute |
| "Verified" for something only reasoned | ESTIMATE or NOT VERIFIED |

## Red Flags: stop

| Thought | Reality |
|---|---|
| "Should work" | Run the check or say unverified |
| "It compiles, so it is done" | Compiling proves types, not behaviour |
| "TPS looks fine" | `Server.Tps` is smoothed and idle-blind; use frame percentiles |
| "I'll deploy and see" | A deploy with no grep for the enabled line is not a step |
| "Restart can't matter here" | Statics, MEC coroutines and subscriptions survive it |
| "They won't mind a test run" | They own those ports. Ask |

Related: running-a-test-server, testing-scpsl-plugins, deploying-scpsl-plugins, measuring-scpsl-performance, scpsl-game-lifecycle, reviewing-scpsl-plugins.
