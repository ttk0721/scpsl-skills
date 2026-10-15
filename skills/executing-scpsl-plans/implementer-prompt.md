# Implementer prompt template (SCP:SL plugin task)

Verified against LabAPI 1.1.7 / SCP:SL 14.2.7 (2026-10).

Fill every `<...>`. Give the task text as a file (the brief), not pasted plan history: a fresh agent
needs its task, the interfaces it touches and the constraints, nothing else.

## Model line: tier plus one sentence

| Tier | Example justification |
|---|---|
| Haiku | "The plan contains the full `Config` class; this is transcription plus a build." |
| Haiku | "Same one-line change in five command files, listed in the brief." |
| Sonnet | "Writes the `stats` RA command from a prose spec; needs `ICommand` and permission checks." |
| Sonnet | "Pure-logic AFK scorer with tests from a prose spec." |
| Strongest | "Moves HTTP results back to the main thread; a race here corrupts `Player.Dictionary`." |
| Strongest | "Fix round 4 after three failed rounds on Sonnet." |

## Skills to name in the prompt

Pick by what the task touches. Always `using-scpsl-skills`. Add the feature recipe skill the plan
names for the task (the `adding-*` family: HUD, custom items, external integrations, round
mechanics, ...); the recipe holds the pattern, the domain skills below hold the facts.

| Task touches | Skill |
|---|---|
| Any event subscription | labapi-events (and events-catalog.md for the rate class) |
| Timers, coroutines, HTTP, files, database, threads | scpsl-main-thread-and-timing |
| `Player`, items, doors, rooms, toys | labapi-wrappers |
| Per-round or per-player state, join, leave, restart | scpsl-game-lifecycle |
| Hints, broadcasts, HUD | scpsl-hints-and-hud |
| Commands, permissions | scpsl-commands-and-permissions |
| Roles, damage, effects | scpsl-roles-damage-effects |
| Client settings, keybinds | scpsl-server-specific-settings |
| Visibility, spawning networked objects | scpsl-networking-for-plugins |
| Voice | scpsl-voice-and-audio |
| Harmony | scpsl-harmony-patching |
| csproj, config class, `Enable()` | deploying-scpsl-plugins |

## Template

```
Agent dispatch
  description: "Implement Task <N>: <name>"
  model: <haiku | sonnet | strongest> — why: <one sentence>
  prompt: |
    You implement Task <N> of an SCP:SL server plugin plan (LabAPI 1.1.7, SCP:SL 14.2.7).

    ## Requirements
    Read first: <BRIEF_FILE>. It holds the task text with exact values; use them verbatim.
    Do not read the whole plan.
    Where this fits: <one line>.
    Constraints from the approved design (verbatim): <player peak, other plugins touching the
    same systems (HUD, custom roles, teleports), round length and restarts, idle mode at night,
    servers sharing data, owner business rules such as excluded staff or no automatic punishment>.

    ## Skills to load before writing code
    using-scpsl-skills, <domain skills>, <recipe skill if any>.

    ## Interfaces
    You consume: <exact signatures, struct fields, config keys from earlier tasks>.
    You produce: <exact signatures later tasks rely on>.
    Files you own: <list>. Edit nothing else; if you must, stop and report NEEDS_CONTEXT.
    <Pure-logic lane only: no LabAPI, Unity or game types in these files; inputs are plain
    snapshots.>

    ## Engine rules
    - Every API name comes from the LabAPI source or a successful build. Never write a guessed
      name or a "verify" comment; if you cannot find a member, report NEEDS_CONTEXT.
    - Off the main thread touch only plain snapshots copied on the main thread. Results come back
      through MainThread.Post or the main-thread callback of the plugin's own Background.ForOwner worker
      (or its own SlHttp.CreateClient client); never the shared static Background.TryRun / SlHttp.PostJsonAsync. Never .Result, .Wait()
      or Thread.Sleep on the main thread.
    - Subscribe last in Enable(); every += has its -= ; flush synchronously on
      ServerEvents.Shutdown (Disable() is never called).
    - Per-round state resets at WaitingForPlayers; persistent keys are UserId, never Player.
    - Nothing per frame or per hot-event raise beyond a flag test or lookup: no logging, LINQ,
      allocation or hint send there. Periodic work goes on one clock.
    - Only assign IsAllowed = false, never true.

    ## Gates before you report
    - <build command from the plan, with the Managed path> : 0 errors; read the warnings.
      Parallel wave, file in a shared project: write "build: deferred to controller" instead.
    - <test command> : all pass. For pure logic, write the failing test first and watch it fail.
    - <the plan's Core-purity grep, if the task touches Core/> : no output.
    - python3 <repo>/tools/scpsl-lint/scpsl_lint.py <project dir, or your own files in a
      parallel wave> : 0 errors. A suppression carries a written reason.
    - <hot-path task: the plan's measurement step and the budget row it must meet>.
    Shell: one plain command per call. No loops, variables, cd chains, or git unless told.
    Never start, stop or configure an SCP:SL server and never write under its app-data folder.
    You do not dispatch subagents; review comes from the controller.

    ## Report
    Write the full report to <REPORT_FILE>: what you built, files changed, each command with the
    tail of its output, every LabAPI member you used and where you confirmed it, concerns.
    Reply in at most 15 lines: Status (DONE | DONE_WITH_CONCERNS | NEEDS_CONTEXT | BLOCKED),
    files, build/test/lint one-liners, concerns, report path.
```

## Fix rounds

Rounds 1-3: resume the same agent with the open findings verbatim and the covering tests to
re-run. Rounds 4-5: a fresh agent one tier up, with the brief, the report file and the findings,
framed as "a prior implementer tried <N> times; you own it now". Each round appends to the same
report file, so it is the memory across agents.

## Re-verifying on a new build

- Skill names in the table still match the index in using-scpsl-skills.
- `Disable()` still has no caller in the loader, and `ServerEvents.Shutdown` still fires on quit
  (deploying-scpsl-plugins).
- SlKit member names (`MainThread.Post`, `Background.ForOwner`, `BackgroundWorker.TryRun`, `SlHttp.CreateClient`) still match
  `tools/SlKit/README.md`.
