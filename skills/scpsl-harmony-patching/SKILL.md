---
name: scpsl-harmony-patching
description: "Use when considering a Harmony patch, prefix or transpiler in an SCP:SL plugin, when a patch silently does nothing or vanishes after a reload, when EXILED, UCR, UCI or ProjectMER conflict with your patch, or when position sync, voice or role code throws InvalidProgramException after patching. Keywords LabAPI, EXILED, Harmony, UnpatchAll, GetPatchInfo, PatchAll."
---

# Harmony Patching on an SCP:SL Server

Verified against LabAPI 1.1.7 / SCP:SL 14.2.7 (2026-10).

## Overview

Patching is global and unscoped: EXILED, UCR, UCI and ProjectMER share your registry, registration has no dedup, and any of them may wipe your patches. Prefer a LabAPI event; patch only the gap, fingerprint, contain failures, re-verify at lifecycle points.

## When to Use

- You think "there is no event for this" (check `labapi-events` first).
- A patch does nothing, stops after `exiled reload plugins`, or `PatchAll` throws in `Enable`; position, voice or role code throws after your transpiler.
- Not for cost numbers (`measuring-scpsl-performance`) or wire design (`scpsl-networking-for-plugins`).

## Decide

| Need | Use |
|---|---|
| React to something LabAPI raises | Event: no patch cost, survives every `UnpatchAll(null)`. |
| Event covers one consumer of a shared decision | Postfix on the decision method. |
| Per-pair or per-frame, must skip work | `Priority.Last` call-site transpiler, 1-for-1. |
| Target IL under 20 B (or `AggressiveInlining`) | Patch its callers. Patched methods become NoInlining, which protects only callers compiled later. |

## Facts

- Harmony 2.2.2.0 is byte-loaded from a LabAPI dependencies folder (LabAPI does not bundle it); EXILED loads an identical copy. Shared registry between the copies: UNVERIFIED.
- Higher priority runs first, default 400, ties by registration order. EXILED, UCR, UCI, ProjectMER and ZAMERT set none.
- After a prefix returns false, later prefixes that return `bool` or take `ref`/`out`/class-typed parameters are skipped, and so is the original body, including transpiler-injected event code. Postfixes still run. No finalizer by default, so a throwing patch body propagates into game code.
- LabAPI plugins enable before EXILED (loader priority 255); EXILED event patches apply lazily on first `Subscribe`.
- `UnpatchAll(null)` removes every owner's patches; UCR, UCI, ProjectMER and Exiled.CustomItems call it. Scoped `UnpatchAll(id)` is owner-blind for a patch method another owner also registered. LabAPI never calls `Disable()`.

More: [harmony-on-the-server.md](harmony-on-the-server.md), [who-else-patches.md](who-else-patches.md), [patch-recipes.md](patch-recipes.md) (code), [troubleshooting.md](troubleshooting.md).

## Rules

1. One `Harmony` instance, one fixed id; `UnpatchAll(thatId)` first in `Enable()`. Never `UnpatchAll()`, `UnpatchAll(null)` or `Unpatch(m, type, "*")`.
2. Resolve targets with `AccessTools.DeclaredMethod` and the full parameter list; null means log and refuse.
3. Fingerprint pristine IL (`GetOriginalInstructions`); on mismatch, do not patch and log loudly.
4. Transpilers: `Priority.Last`, match by operand, exact hit count, else return input unchanged. Keep EXILED's anchors.
5. No `return false` prefix on methods others transpile (`FpcServerPositionDistributor.WriteAll`, `VoiceTransceiver.ServerReceiveMessage`, `RadioItem.Update`). `scpsl-lint` SL017 covers `WriteAll` only.
6. Per-pair or per-frame bodies: try/catch, a static Active flag; no allocation, `__args`, LINQ or stack walks.
7. Never patch `EventManager.InvokeEvent<T>` or other generics over reference types (one shared body).
8. Probe `Harmony.GetPatchInfo(target)` at `Enable`, `ServerEvents.WaitingForPlayers` and on a 30 s or slower timer, own targets only.
9. Count hits: a patch without proof it runs is unproven.

## Common Mistakes

| Wrong | Right |
|---|---|
| Prefix on a remembered private signature to flip visibility. In `WriteAll` the flag is an IL local fed into `ValidatedVisibility`; a wrong signature makes `PatchAll` throw in `Enable` or no-ops. | `ValidatedVisibility` event (`IsVisible` settable), or a fingerprinted `Priority.Last` call-site transpiler. |
| No `UnpatchAll(id)` in `Enable`, no try/catch in a per-pair body: one exception breaks position sync for the tick. | Rules 1 and 6. |
| No `GetPatchInfo` probe or re-check after a foreign `UnpatchAll(null)`; or polling at 1 Hz, or `HasAnyPatches`. | Rule 8; idempotent re-apply. |
| Two Harmony instances plus two `PatchAll`, or a timestamp id per instance. | One instance, fixed id. |
| Subscribing to EXILED events in `Enable()` or mid-round. | `PluginsEnabled`/`WaitingForPlayers`; first subscribe patches synchronously. |

## Red Flags

- Target under 20 B IL patched after round one.
- Testing only without EXILED.
- Reading EXILED source, not the installed DLL (source 9.15 has a `WriteAll` transpiler 9.14.2 lacks).
