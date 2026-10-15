# Scenario results (RED, GREEN) and their limits

How the skills were tested while they were written, what the numbers were, and how far they can be
trusted. Read [Limitations](#limitations) before the tables.

The method and the prompts are in [scenarios.md](scenarios.md), so anyone can **repeat the runs**.
**The original grading cannot be repeated**: it used private research notes that cite decompiled
game code. What a reader can do is **re-grade** new runs with the public
[answer keys](answer-keys.md), which list what a correct answer shows and the public skill file
that states each point. Re-grading with them will not reproduce the scores below, because they keep
only what a public skill supports. The transcripts and per-run grade files are **not in this
repository**: they quote private research and name people, so they are kept privately. Everything
the numbers rest on is summarised here.

## Test 1: five code tasks

Same five tasks, same model (Sonnet), same grading rubric: "would this be correct and cheap on a
60-player server", 0 to 10, graded against the research notes.

- **RED**: closed book, no files on disk.
- **GREEN**: only this repository (skills and tools) readable.

| Scenario | RED | GREEN | Mistakes RED to GREEN |
|---|---|---|---|
| s1 activity tracker with live HUD and HTTP export | 8 | 9 | 7 to 3 |
| s2 TPS alert to a Discord webhook | 6 | 9 | 11 to 4 |
| s3 "master keycard" tracked by serial | 6 | 9 | 7 to 2 |
| s4 anti-ESP (stop sending hidden players) | 3 | 7 | 14 to 7 |
| s5 port an EXILED fragment to LabAPI | 7.5 | 9 | 11 to 4 |
| **Mean / total** | **6.1** | **8.6** | **50 to 20** |

Critical mistakes (break the server or the feature): 4 in RED, 0 in GREEN. The RED criticals:
trusting `Server.Tps` for an alert (s2); a permission check that does not compile, mixing LabAPI
string permissions with game `PlayerPermissions` (s3); a Harmony patch on a parameter that does
not exist (s4); an anti-ESP that silences footsteps, proximity voice and gunshots of hidden
players (s4).

After grading, seven skills were patched where an agent that had read them still slipped
(`scpsl-game-lifecycle`, `measuring-scpsl-performance`, `scpsl-main-thread-and-timing`,
`designing-scpsl-plugins` and others). Two of those were the skill's own fault: a sample comment
that taught a wrong UserId assumption, and an HTTP failure policy too vague to implement. **The
GREEN scores above were measured before those patches.** s1 and s2 were re-run in round 4 of
test 2 (below) and scored 8 each; s3, s4 and s5 were not re-run.

The gain is uneven. s1 moved by one point (8 to 9); s4 stayed the weakest at 7 with seven mistakes
left.

## Test 2: process layer with a role-played owner

The agent (Sonnet) gets a short request from a server owner and may ask one question per turn, up
to 8 turns. The owner (Haiku) answers in Polish from a hidden fact sheet and reveals a fact only
when asked. A grader scores the transcript on which facts were discovered, whether the design was
approved before code, proportionality and engine mistakes.

- **RED**: no library.
- **GREEN**: the library, starting from `using-scpsl-skills`.

### Round 1 and 2 (GREEN run 2 is the second row below)

| Scenario | RED | GREEN run 2 | Questions RED to GREEN | Approval before code |
|---|---|---|---|---|
| p1 kill-stats HUD | 3 | 8 | 2 to 7 | no to yes |
| p2 activity rewards | 3 | 7 | 1 to 6 | no to yes |
| p3 speedhack detector | 5 | 9 | 1 to 9 | no to yes |
| p4 small change (log a line on join) | 9 | **7** | 0 to 0 | not needed |

Real features (p1 to p3): mean 3.7 in RED, 8.0 in GREEN, and a design was approved before code in
3 of 3 (RED: 0 of 3). In RED the agents never asked about the environment and wrote code with
thread races. In GREEN they asked about EXILED and other plugins, two servers and the panel API,
and tested on a separate port rather than the owner's.

**Regression, named plainly: p4 went from 9 in RED to 7 in GREEN run 2.** The library made a
one-line change worse: the agent delivered two variants, seven caveats and an unrequested filter.
Path A in `using-scpsl-skills` was written to fix this. In run 3 p4 scored 8, still below the
RED score of 9. The library has not yet beaten the baseline on a small change.

A first GREEN run asked 14 questions for p2 (3 irrelevant). After that, `brainstorming-scpsl-features`
got a question budget (about 5 to 7) and numbered assumptions with defaults; the second run asked 6.
It also left four weaknesses, each patched in the named skill:

- p2: "who counts" (staff, spectators) was never asked, and staff were counted by default
  (`brainstorming-scpsl-features`).
- p2: the AFK clock reset at every round restart, worth about 5 free minutes per round
  (`adding-per-player-tracking`).
- p1: a plugin overwriting hints was only listed as a test item (`adding-player-hud`).
- p4: the small change came with two variants, seven caveats and an unrequested filter (path A in
  `using-scpsl-skills`).

One p1 attempt in the first GREEN run was cut off by an API error and was re-run.

### Round 3: GREEN run 3, after the four patches

| Scenario | GREEN run 2 | GREEN run 3 | What changed |
|---|---|---|---|
| p1 kill-stats HUD | 8 (5 of 8 facts) | 8 (8 of 8 facts) | re-sends the HUD after spawn and role change (hint-overwrite patch works) |
| p2 activity rewards | 7 | 7 | now asks who counts and excludes staff (patch works); still never asks about round restarts or idle nights |
| p3 speedhack detector | 9 | **7** | all facts found, but after the owner said "write the code" it delivered a spec with no code |
| p4 small change | 7 | 8 | one question about style, one change in the plugin's own style, caught a wrong handler signature (path A patch works) |

**Regression, named plainly: p3 went from 9 in run 2 to 7 in run 3.** The mean over p1 to p3 fell
from 8.0 to 7.3 between run 2 and run 3. The gain from the patches shows in p1 and p4 and is
offset by this one run. Because each cell is one run, the drop may be run-to-run noise or a real
side effect of the question budget; one run cannot tell.

Run 3 exposed two more issues, patched in `brainstorming-scpsl-features` but **not re-tested**:

- p1 asked 19 questions, packed 3 or 4 per message, apparently to fit the question budget. The
  budget now counts questions, not messages.
- p3 ignored the owner's explicit "write the code" after the design was approved. The owner's
  shortcut now wins after approval, and the agent names what it skips.

Across the three GREEN runs real features scored 7 to 9 against 3 to 5 in RED, and every GREEN run
had the design approved before code.

### Round 4: GREEN run 4, after every patch above and the second external review

Run on 2026-10-02 against the library as it is in this commit: the four process scenarios, plus
the two code tasks whose right answer depends on SlKit's HTTP and worker API (s1, s2), which the
review changed. Same models and prompts as before. **Each attempt was graded twice, by two
separate grader instances**: one against the private research notes (as in rounds 1 to 3), one
that was given only the public [answer keys](answer-keys.md) and the skill files they point to.

| Scenario | Earlier GREEN | Run 4, private grader | Run 4, public-key grader | Approval before code |
|---|---|---|---|---|
| p1 kill-stats HUD | 8, 8 | 7.5 | 7 | yes |
| p2 activity rewards | 7, 7 | 6 | 6 | yes |
| p3 speedhack detector | 9, 7 | 8 | 7 | yes |
| p4 small change | 7, 8 | 7 | 6 | not needed, proportionate |
| s1 activity tracker (test 1) | 9 | 8 | 8 | not part of test 1 |
| s2 TPS alert (test 1) | 9 | 8 | 7.5 | not part of test 1 |

- **Grader agreement.** The two graders differed by at most 1 point and by 0.5 on average. A reader
  who has only this repository can re-grade a run and land within about a point of the private
  grading. Both graders named the same main faults in p4 and p3; they differed on detail.
- **Approval before code held in 4 of 4** process runs, and all four were judged proportionate.
- **Real features (p1 to p3): mean 7.2**, against 8.0 and 7.3 in runs 2 and 3. That is inside the
  spread of one run per cell (limitation 5). s1 and s2 scored 8 against 9 before the test 1
  patches; the grader instance is new, so this is not evidence of a regression either way.

What did not hold, named plainly:

- **Question budget, partly.** p1 asked about 11 questions (19 in run 3), but turns 4 and 5 still
  packed 3 or 4 questions into one message.
- **p3 delivered a skeleton.** The detector's threshold, zone, elevator and pocket checks were
  stubs. It said so, but "do it" asked for a working plugin.
- **p4 contradicted a skill.** It claimed dummies pass through `Joined` and suggested a filter for
  them; `scpsl-game-lifecycle/idle-config-dummies.md` says dummies never raise `Joined`.
- **p2 never asked** the peak player count or about round restarts and idle nights, so it gave no
  cost estimate for 60 players. Run 3 missed the same two facts.
- **s1 has no final flush at `ServerEvents.Shutdown`**, a trap the skills and the linter (SL015)
  both name.

These were not patched after run 4.

## Limitations

1. **The graders are language models.** In rounds 1 to 3 each transcript was scored by one LLM
   instance. Round 4 added a second grader per attempt (public keys only) and the two agreed within
   1 point, but both are the same model family and there was no human grader, so the absolute
   scores are not calibrated. The comparison RED to GREEN is more trustworthy than any single
   number.
2. **One model family on the agent side.** Only Sonnet was tested as the agent. Other models may
   behave differently with the same skills. The owner in test 2 was role-played by Haiku, from a
   fact sheet written by the author: it answers like a cooperative, fluent owner, not like a
   terse or confused one, and it answers in a different language than the skills are written in.
3. **The original ground truth cannot be checked by anyone else.** The rubric and the "mistakes"
   were derived from the project's own research notes, which cite decompiled game code and stay
   private, so the original scores cannot be reproduced. The public [answer keys](answer-keys.md)
   are the way to re-grade: every row names the public skill file that states the fact, and says
   whether LabAPI's public source is enough to check it or a decompiler or test server is needed.
   The keys were written from the original grade files, after the fact, so they were not available
   as a rubric when the runs were graded. The same research wrote the skills and the keys, so the
   skills and the keys are not independent.
4. **Nothing was compiled or run in the transcripts.** The agents produced code and designs as
   text. "Correct and cheap" was judged by reading, not by building the plugin and measuring a
   server. (The example plugins in `examples/` do compile, and `tools/sl-probe` and `tools/hot-bench`
   were run on a live test server, but those are separate from these scenarios.)
5. **One run per scenario per round, with about plus or minus 2 points of variance.** p3 moved
   from 9 to 7 between run 2 and run 3, although none of the four patches in between was aimed at
   p3. Differences of one or two points between cells are not evidence.
6. **Re-testing after the patches is partial.** Round 4 re-ran all four process scenarios and two
   of the five code tasks (s1, s2) on the current library. s3, s4 and s5 were not re-run after the
   test 1 patches, so their GREEN scores still describe the library before those changes. The
   weaknesses that round 4 found were not patched and re-tested.
7. **Small sample, authored scenarios.** Five code tasks and four process tasks, all written by the
   author, most of them aimed at mistakes the author already knew. They show that known pitfalls
   are covered; they say little about pitfalls nobody thought of.
