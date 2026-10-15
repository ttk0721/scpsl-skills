# Reviewer prompts: spec, then engine

Verified against LabAPI 1.1.7 / SCP:SL 14.2.7 (2026-10).

Every task gets both verdicts. Spec comes first: engine-reviewing code that solves the wrong
problem wastes the review. For a small, low-risk diff one reviewer may return both verdicts in one
dispatch (stage 1 first); a diff that touches a hot event, a thread boundary or Harmony gets a separate engine
reviewer on the strongest tier.

Inputs for both: the brief path, the implementer's report path, and one diff file (commit list,
stat, full diff with context) written by the controller. The reviewer reads the diff once, does not
re-run the implementer's tests, does not mutate the tree, and does not dispatch subagents. The
implementer's report is a claim, not evidence.

## Stage 1: spec compliance

```
Agent dispatch
  description: "Spec review, Task <N>"
  model: sonnet — why: <one sentence, e.g. "compares a 3-file diff against a prose brief">
  prompt: |
    Review Task <N> of an SCP:SL plugin plan for spec compliance only.
    Brief: <BRIEF_FILE>. Report: <REPORT_FILE>. Diff: <DIFF_FILE>.
    Binding constraints from the approved design (verbatim): <GLOBAL_CONSTRAINTS>.

    Report:
    - Missing: requirements skipped or claimed without code.
    - Extra: features nobody asked for (new tables, new endpoints, own rank systems when the
      owner has an API for it).
    - Misunderstood: right feature, wrong behaviour.
    - Owner rules: check each business rule in the constraints by name (for example "staff
      excluded", "no automatic bans, report only", "use the existing external API",
      "progress survives restart"). Cite the line that honours it, or report it missing.
    - Files outside the brief's file list that the diff touches.
    - CANNOT VERIFY from this diff: requirements that live in other tasks or unchanged code.
    Every finding has file:line. Verdict: SPEC COMPLIANT | ISSUES FOUND.
```

## Stage 2: SCP:SL engine review

```
Agent dispatch
  description: "Engine review, Task <N>"
  model: <sonnet | strongest> — why: <e.g. "the diff adds a pool-thread callback and a
         Shutdown flush; concurrency needs the strongest tier">
  prompt: |
    Review Task <N> of an SCP:SL server plugin (LabAPI 1.1.7, SCP:SL 14.2.7) for engine cost
    and correctness. Load reviewing-scpsl-plugins and read its review-checklist.md.
    Brief: <BRIEF_FILE>. Report: <REPORT_FILE>. Diff: <DIFF_FILE>. Load model: <players>.

    1. Run: python3 <repo>/tools/scpsl-lint/scpsl_lint.py <whole plugin project dir>
       (one plain command). Every error is a finding; every suppression needs a written reason.
    2. Walk the checklist sections the diff touches, in impact order:
       - Hot events: rate class of every subscribed event from events-catalog.md; anything
         beyond a flag test or lookup on a hot path is a finding with rate x players x work.
       - Per-frame work: player loops, physics, LINQ, allocation, hint sends per frame.
       - Main-thread blocking: .Result, .Wait(), sync file or HTTP IO in handlers or clock jobs.
       - Threading: code after ConfigureAwait(false) or inside Task.Run/Background work that
         touches Player, Server, Round, Map, MEC or hint sends; async void handlers; IsAllowed
         set after an await.
       - Round restart: state reset at WaitingForPlayers, UserId keys, clock jobs or coroutines
         scoped to the round, restart-driven Left treated as a session end.
       - Hints: one HUD owner, Player.ReadyList not Player.List, send on change plus keep-alive,
         no "refresh everyone" from spawn or role events.
       - Logging: none on hot paths; a flag checked before formatting; rate-limited errors.
       - Lifecycle: fallible work first and subscribe last in Enable(), undo and rethrow on
         failure, every += has a -=, synchronous flush on ServerEvents.Shutdown, idempotent
         Enable().
       - Harmony: one id, no `UnpatchAll(null)`, no prefix returning false on a shared path.
       - Enforcement features (kicks, bans, hiding): shadow or report-only mode first, and
         the owner's rule about automatic action honoured.
       - Config and deploy traps: literal `RequiredApiVersion`, no shipped game DLLs, every
         option has a reader.
       - API names: every member exists (the build passing is the proof); any "verify" or
         guessed name is Critical.
    3. Do not re-run the test suite. Do not quote Server.Tps as evidence.

    Output: findings as Critical / Important / Minor, each with file:line, rate class, why it
    matters on a full server, fix, and confidence. List what you could not check (runtime
    subscriptions, behaviour of other plugins). Verdict: Approved | Needs fixes.
```

## Grading the findings (controller)

- Critical or Important from either stage enters the fix loop.
- Minor goes to the ledger as `Task <N>: minor (deferred): <one line>` and to the final review.
- A finding that conflicts with the plan text is a ruling for the controller, ledgered with its
  cost if wrong, never a silent dismissal.
- A CANNOT VERIFY item is checked by the controller before the task is marked complete.

## Final whole-plan review

After the integration gate, one reviewer on the strongest tier reads the whole-branch diff with
the design note, the ledger's `Ruling:` lines and deferred minors, and runs stage 2 over the
complete plugin: cross-task problems (two subscriptions to one event, two HUD senders, state reset
in one place but not another) only appear here.

## Re-verifying on a new build

- Section names and order in reviewing-scpsl-plugins/review-checklist.md.
- The hot-event list in `tools/catalog/labapi-events.json` regenerated for the new build.
- `Player.ReadyList` still excludes the host (scpsl-hints-and-hud).
