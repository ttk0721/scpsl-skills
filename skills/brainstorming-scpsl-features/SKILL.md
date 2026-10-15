---
name: brainstorming-scpsl-features
description: "Use when an SCP:SL server owner or developer asks for a new plugin, a new feature, or a behaviour change in a LabAPI or EXILED plugin (HUD, stats, rewards, playtime, anti-cheat, tracker, webhook, panel or database sync, custom roles or items), before any code exists for it, or when players, servers, other plugins, persistence or testing for the request are still unknown."
---

# Brainstorming SCP:SL Features

Verified against LabAPI 1.1.7 / SCP:SL 14.2.7 (2026-10).

## Overview

What decides an SCP:SL design lives on the owner's server, not in the request: 60 players, EXILED, a plugin refreshing everyone's hints, idle nights, staff to exclude.

<HARD-GATE>
No plugin code, csproj, scaffolding or implementation skill until the owner approves the design. Path B: a yes to the in-chat design. Path C: a yes to the design permits the spec; a yes to the written spec permits writing-scpsl-plans. Reading is always allowed. "Any questions?" is not approval.
Once the design is approved, the owner may shorten the rest ("write the code", "skip the spec"). Their instruction wins: say in one line which steps you skip and the risk, then write the code.
</HARD-GATE>

## When to Use: pick the path, say it aloud

| Path | Predicate | Do |
|---|---|---|
| A, small | One existing plugin; no state outliving a round, no IO, hints, patches or hot events; nothing players see or hit changes ("log nick on join") | **Skip this skill.** Apply the domain skill, implement; at most one question |
| B, bounded | Tunes an existing feature you can read in the repo: new command or config key, changed numbers or wording, one more field in an existing payload | 1-4 bank questions, in-chat design with a cost line, STOP for yes, implement. No spec, no plan |
| C, new | New plugin or feature, or new persistence, HTTP/DB, HUD, Harmony, hot event, new gameplay mechanic, several servers | Full process |

In doubt, take the heavier path. Hidden complexity upgrades mid-task; nothing downgrades.

## Process (path C)

Detail, budget rules, assumptions format and the cost-line example: `process-details.md`.

1. **Read first:** csproj (`Exiled.*` or LabAPI only), handlers, hint code, configs, `docs/specs`, server folders (read-only). Never ask what these answer. Independent features? Split.
2. **Ask from `question-bank.md`, in order, through the interactive question tool** (Claude Code: `AskUserQuestion`). Options recommended first, each naming the design choice it flips (none: record an assumption). One call holds up to 4 questions: batch those whose answers do not depend on each other, ask dependent ones after; `multiSelect` when options can combine. **Budget: about 5-7 questions on C, 1-4 on B, counted per question.** Past it, unknowns become assumptions. **Who counts** (anything that rewards, ranks, counts, measures or punishes players) is never skipped: one early question, or numbered assumptions.
3. **Write back** owner said vs you assume, ending with **numbered assumptions, each with your default**. Business rules are never silent. Staff default to **excluded**, and the owner must confirm it.
4. **Propose 2-3 approaches**, recommended first, each with a cost line and what it gives up. Look API names up now.
5. **Present the design** in sections; a yes after each.
6. **Write the spec** (`spec-template.md`) to `docs/specs/YYYY-MM-DD-<topic>-design.md`; self-review; owner reviews.
7. **Hand off to writing-scpsl-plans**; feature recipe skills enter through the plan.

Worked dialogue: `example-kill-stats-hud.md`.

## Tests are asked, never assumed

Path C: ask whether they have a local server and want tests at all; no means a manual checklist. Never test on a public server or the owner's ports unasked; offer our own instance on port 9999. Details: `process-details.md`.

## Red Flags: STOP

| Thought | Reality |
|---|---|
| "Owner is busy, I'll assume" | Assumptions go into the design and get a yes |
| "They ticked my LabAPI guess" | Not environment knowledge. Ask what is installed |
| "Staff, rewards, bans: left to me" | Owner's business rules. Ask |
| "Staff are players too" | Not for rewards, ranks, stats or punishments: default excluded, ask who counts |
| "Spectators and AFK obviously (do not) count" | Dead players and most late joiners are Spectators. The owner's call |
| "I'll design the endpoints" | Ask what panel, API or database exists |
| "Mark API names [VERIFY]" | Look them up now |
| "Only a HUD, cost is negligible" | Write the cost line |
| "All questions at once saves time" | Batch only independent ones (max 4 per window); an answer that deletes others goes first |
| "I'll put the question at the end of my summary" | The owner may not notice it. Use the question tool |
| "Thorough means asking the whole bank" | Ask what flips the design (5-7); the rest are numbered assumptions |
| "Four per window keeps me in budget" | Budget counts questions, not windows |
| "They said 'write the code', but the process says spec first" | After design approval the owner's shortcut wins: name what you skip |
| "Small, but full process anyway" | Path A: just do it |
| "I'll start while they read" | The gate is the yes |
| "Of course they want a test server" | Ask. A no is final |

## Common Mistakes

- Colour questions before players, EXILED, other plugins.
- A JSON file when a panel exists.
- No answer for restart, disconnect, dummies, staff.
- Counting everyone by default: staff, spectators and AFK players land in the reward or ranking, and only the owner reading the finished proposal catches it.
- A HUD corner picked without asking who else sends hints.
- Asking the test-server question twice, or on a small change.
