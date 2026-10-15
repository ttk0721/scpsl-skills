---
name: researching-scpsl-internals
description: "Use when you must find out how SCP:SL server internals really behave (where the game raises a LabAPI event, whether code runs per frame, whether a setter is honoured, which SyncVars or Harmony patches exist, whether a BCL API exists), when a game or LabAPI update may have invalidated these skills, or when planning a research or measurement pass before turning a finding into a rule."
---

# Researching SCP:SL internals

Verified against LabAPI 1.1.7 / SCP:SL 14.2.7 (2026-10).

## Overview

The installed build is the only ground truth; docs, old tutorials and memory are leads. Answer from the cheapest source that settles the question, cite what you opened, label the rest unverified.

## When to Use

- "Does the game really do X?", "how often does event Y fire?", "does this run per frame?".
- After a game or LabAPI update; before writing a rule, skill or catalog row others will inherit.
- Not for plugin code (designing-scpsl-plugins), choosing events (labapi-events) or measuring a live server (measuring-scpsl-performance).

## The ladder: climb only as far as needed

| Rung | Source | Settles |
|---|---|---|
| 0 | Existing skills, event catalog, `namespace-map.md` | Most event and lifecycle questions, free |
| 1 | `grep` (`-l`/`-c` first) in LabAPI source and a local decompile | Where something is raised, called, synced, patched |
| 2 | `ilspycmd -m`, one method | What it does (1-2 KB) |
| 3 | `ilspycmd -t`, one type | Class structure (5-200 KB: write to a file, grep it) |
| 4 | Whole-tree decompile or IL dump, once per build | Cross-cutting questions, build diffs |
| 5 | Probe plugin or `sl-probe` on a test port | Timing, allocation, threads, client behaviour |

## Rules

1. **Cite or label.** Name the type and method you opened, or mark [E] estimate / [U] unverified. A static count is [C]; only a run is [M].
2. **A raise site is not a frequency.** Read the caller (Update, MEC loop, Mirror handler, loop over players) and the lines after the call.
3. **Zero hits is not "never".** Check LabAPI itself, helper wrappers and `ReplaceHandler` first.
4. **Never commit or paste decompiled code.** Name public types and members; run `tools/check-publishable.py --corpus <tree>` before release.
5. **Strongest model only where a wrong conclusion becomes a rule** (threading, event semantics, design judgment), with one sentence saying why. Extraction and fact-checking go to cheaper models; a cheaper skeptic that overturns a strong-model rule gets one adjudication call.
6. **After an update, diff first**, then re-research only what moved. Reuse existing notes as leads, not facts.

## Common Mistakes

- **Printing a type or IL to the terminal.** `-t MEC.Timing` is about 175 KB; `-il` ignores `-t` and `-m` and dumps the whole assembly (40 MB). Use `-m`, write to `-o`, grep.
- **Grepping only `RegisterHandler`.** The voice message handler uses `ReplaceHandler`.
- **"No call site, so never fired."** Three LabAPI events are raised inside LabAPI.
- **Trusting a hand-kept list.** Cross-check with `scripts/event-sites.py`.
- **Checking a BCL API in docs.** The server's own `mscorlib.dll` decides.
- **Stamping every file "verified" after an update** without re-running its checks.

## Red Flags

- "I remember..." or "the docs say..." as evidence.
- A hot-path claim with no caller read; a setter claim with no code after the call read.
- The strongest model on extraction; a cheap model alone deciding a threading rule.
- Decompiled lines in a repo note; a measurement with no baseline.

## Reference files

`sources-and-decompiling.md` (local tree, ilspycmd flags), `grep-recipes.md` (raise sites, read-after-call, per-frame, SyncVars, patches, BCL, IL size), `namespace-map.md` (namespace to content), `research-workflow.md` (prompt templates, model tiers, cost rules), `reverifying-a-build.md` (update checklist, catalog regeneration, measurement), `scripts/event-sites.py` (catalog drift, build diff).
