---
name: debugging-scpsl-plugins
description: "Use when an SCP:SL plugin misbehaves: it is missing from the boot log, a handler never runs or runs twice, a code change has no effect, hints flicker or vanish, TPS drops, exceptions spam the log, things break after a round restart, a Harmony patch does nothing, a config change is ignored, or it works on one port but not another."
---

# Debugging SCP:SL plugins

Verified against LabAPI 1.1.7 / SCP:SL 14.2.7 (2026-10).

## Overview

Read the evidence, name the cause, then fix. This server hides failures: a wrong folder logs nothing, an error header can blame the wrong plugin, every test costs a restart.

```
NO FIX WITHOUT A NAMED CAUSE AND EVIDENCE FOR IT
```

General method: systematic-debugging (superpowers). This skill adds where the evidence lives.

## When to Use

- Any wrong behaviour of a LabAPI or EXILED plugin.
- **Not the full process** when the cause is on screen (compile error, stack trace naming your line, a typo): read it, fix it, verify once.
- Not for measuring (measuring-scpsl-performance) or build setup (deploying-scpsl-plugins).

## The process

1. **Scope.** Ask only what you cannot read: LabAPI, EXILED or both; port; other plugins touching the same thing; what changed. No server access: ask for the boot loader block and the first exception.
2. **Read the evidence**: loader block, first exception (stack trace, not header), version lines. Paths and greps for Linux and Windows: [where-to-look.md](where-to-look.md); which log answers which question: [test-server.md](test-server.md).
3. **Classify** with [symptoms.md](symptoms.md), run its read-only check, write one hypothesis: "X, because Y".
4. **Reproduce**, cheapest first: pure-logic test, lint, test instance, read-only instrumentation, owner triggers it. Cannot reproduce: add counters and wait; do not fix blind.
5. **Fix the cause**: one change, failing reproduction first.
6. **Verify**: reproduction passes; the boot log shows `Successfully enabled` with the Version you built (a stale DLL logs the old one); no new exceptions; the owner's other plugins still present. State what you did not verify.
7. Three failed fixes: stop and question the design with the owner.

## Running anything on a server

Never start, restart, copy a DLL to, or send commands to a server without a yes. Some owners have no local server or do not want local tests: ask, never push. Procedure, scripts and paths: running-a-test-server (the single copy); which log to read: [test-server.md](test-server.md).

1. Ask once: server installed here, local tests wanted? No: evidence only (pasted logs, lint, build, pure-logic tests); tell the owner what to run.
2. Default install or app-data path missing: ask for it, reuse the answer.
3. List the owner's ports. Existing port or our own instance? Recommend our own, on an unused port (9999). Never touch a port the owner already uses, or any port with traces, unless the owner asks.
4. Check the server is private before dummies, `/forcestart` or restarts. Players on it, or `contact_email` set: read-only.
5. Test DLLs go in `plugins/<testport>`, never `global`. Say what already loads on the test port (`global`, EXILED).

## First check per symptom

| Symptom | First check |
|---|---|
| No log line for the plugin | Folder, port, `is_enabled`; `sldoctor plugins` |
| `ReflectionTypeLoadException` | Dependency location (global plugin needs `global`) |
| Code change has no effect | Restart done? Version in the success line; second copy of the DLL |
| Handler never runs | Catalog row, `sldoctor subs`, `sldoctor count` |
| Runs often, lag | Hot event, logging, per-frame work (not `Server.Tps`) |
| Broken from round 2 | Cached `Player`, statics, coroutines, doubled `+=` |
| Hint flicker | Other senders, vanilla hints, keep-alive |
| Patch does nothing | `GetPatchInfo`, foreign `UnpatchAll(null)` |
| Config ignored | Rewritten file, one bad value, cached `Config`, port folder |

All families with causes: [symptoms.md](symptoms.md). Tool: `examples/debug-doctor`.

## Common Mistakes

- Reading the header of an event exception instead of the stack trace.
- Testing without EXILED or the owner's other plugins; trusting dummies for join, voice or network logic.
- Reading durations from boot-log timestamps; a bare `Logger` call in a hot handler "to see".
- Checking one round: restart bugs need two.

## Red Flags

| Thought | Reality |
|---|---|
| "I will restart their server and see" | Their port, their players. Ask, or use your own instance |
| "It is probably the other plugin" | Prove it: `sldoctor subs`, disable it on a test instance |
| "Fixed, the log is quiet" | A wrong folder is quiet too. Find the success line |
| "Works on my test port" | Idle mode, no EXILED, no players. Same plugin set and load |
| "One more fix" (after two) | Stop. Question the design |
