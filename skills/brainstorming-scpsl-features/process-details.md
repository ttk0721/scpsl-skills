# Process details (brainstorming-scpsl-features)

Verified against LabAPI 1.1.7 / SCP:SL 14.2.7 (2026-10).

The full text of the rules that SKILL.md states in short. Nothing here relaxes the gate or the
red flags in SKILL.md; it only spells them out.

## Step 2: asking

- Ask through the harness's interactive question tool (Claude Code: `AskUserQuestion`). A question
  at the end of a long text message is easy to miss.
- Multiple choice, your recommended answer first. One tool call holds up to 4 questions, each
  single- or multiple-choice (`multiSelect` when options can be combined). Put questions in one
  call only when no answer changes another question; ask a question whose answer decides what to
  ask next on its own, first.
- Name the design choice the answer flips. If no answer would change the design, do not ask:
  record an assumption.
- Stop when no remaining answer changes the design.
- **Budget: about 5-7 questions on path C, 1-4 on path B, counted per question, not per
  message.** Packing three questions into one message does not stretch the budget, it breaks the
  one-question rule. Past the budget, unknowns become assumptions.
- **Who counts** applies to anything that rewards, ranks, counts, measures or punishes players:
  rewards, stats, playtime, leaderboards, anti-cheat, moderation. It is never skipped. Ask one
  early multiple-choice question, or, if the budget is gone, list numbered assumptions (staff,
  spectators and Overwatch, AFK, dummies, tutorial and special roles) for the owner to confirm.
  It is not a reason to exceed the budget: it replaces other questions or turns into
  assumptions. The group table is in `question-bank.md`, section 1, "Who counts".

## Step 3: writing back

State the facts and the behaviour, separating what the owner said from what you assume. End with
**numbered assumptions, each with your default**, so the owner fixes them in one reply:

```
A3: staff groups earn nothing
A4: spectators and Overwatch do not count
A5: AFK players and dummies do not count
```

Business rules (who is excluded, what counts, what is punished) are never silent: ask, or list
them for an explicit yes. Staff default to **excluded**, and the owner must confirm it. An
assumption the owner has not seen is not a yes.

## Step 4: approaches

2-3 approaches, recommended first, each with a cost line and what it gives up. Price them with
designing-scpsl-plugins. Look API names up in the LabAPI source and in SlKit now, so the design
does not rest on a name that does not exist.

## Cost line

Frequency class from the labapi-events catalog; mark estimates:

```
events: Death (per-lifecycle) | per-frame: 0 | clock: 60 players / 2 s keep-alive = 30 hints/s, ~3 KB/s (est., 80 B text) | allocs: 1 string/send | IO: 1 POST / 60 s, off-thread
```

## Tests

Path C: ask whether the owner has a local dedicated server and wants local tests at all. No means
a manual checklist and a measurement plan; nothing later pushes tests. Never test on a public
server or on the owner's ports (7777, 7778) unasked: check privacy (`contact_email` unset, the
boot-log line), offer our own instance on an unused port (9999), let them choose. Answers carry
over: running-a-test-server (paths, procedure) does not ask again. On paths A and B a green build
and lint need no server. The question order is in `question-bank.md`, section 6.

## Red flags, spelled out

- "Staff are players too": not for rewards, ranks, stats or punishments. Default excluded, and
  ask who counts: staff, spectators, AFK, dummies, tutorial.
- "Spectators and AFK are obviously not playing" (or obviously are): dead players are Spectators,
  and so are most late joiners. The owner's call, asked or listed as an assumption.
- "Thorough means asking the whole bank": ask what flips the design (5-7); the rest go in as
  numbered assumptions with defaults.

## Re-verifying on a new build

- The SKILL.md rules reference other skills by name; run `python3 tools/check-skills.py` to catch
  a renamed or removed one.
- The test-server rules (ports, privacy check) live in running-a-test-server and the question
  bank, section 6; re-check them there, not here.
- Re-run the scenarios in `tests/scenarios.md` after changing the 5-7 question budget or the "who counts" default.
