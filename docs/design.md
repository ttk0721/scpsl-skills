# scpsl-skills: design

Describes the repository as it is at version 0.2.0 (verified against LabAPI 1.1.7 / SCP:SL 14.2.7).
Where this file and the repository disagree, the repository wins: fix this file. How to keep the
facts true after a game update is in [maintenance.md](maintenance.md).

## Goal

A skills library (a Claude Code plugin, in the shape of superpowers) plus a small toolkit, so that
an agent or a human can write SCP: Secret Laboratory server plugins on LabAPI that are **correct**
and **cheap**: at 50-60 players a plugin must be an add-on, not the main load on the server.

Two audiences:

- An agent writing or reviewing a plugin. Skills trigger on the task and load only what is needed.
- A maintainer coming back months later. The knowledge is versioned against a specific
  LabAPI/game build instead of living in one head or one lost chat.

## Hard constraints

1. **No Northwood code in this repository.** SCP:SL EULA section 12 forbids distributing
   non-public Northwood data. Skills describe behaviour and name public types and members; the
   event catalog's `raisedFrom` column also names the game method that raises each event (some
   are non-public) as a location. Nothing quotes decompiled game code, decompiled paths or line
   numbers. LabAPI is LGPL-3.0 and
   public, so it may be linked and briefly quoted with attribution. `tools/check-publishable.py`
   looks for decompiler signatures, private words (a local `.private-words` file) and, when given
   a corpus directory, for three consecutive lines copied from it. Without a corpus it says so
   ("WEAK CHECK") instead of claiming a clean result.
2. **Version-pinned facts.** Every skill states the LabAPI and game version it was verified
   against. Each reference file ends with a "Re-verifying on a new build" section listing what to
   re-check.
3. **Evidence over memory.** Knowledge comes from reading the code and from measurement, not from
   older EXILED or NWAPI tutorials. What could not be confirmed is marked unverified. The
   [README](../README.md#what-was-checked-and-how) says what was checked and how.
4. **Token discipline.** Progressive disclosure: a short `SKILL.md` per skill, detail in sibling
   files loaded on demand. The size policy below has numbers.

## Size policy

Counted as words in the body of `SKILL.md` (frontmatter excluded). The limits are enforced by
`tools/check-skills.py`, which holds them as constants and fails when a skill is over; this table
quotes those constants, and a test fails if the two disagree.

| Item | Limit |
|---|---|
| Entry skill `using-scpsl-skills` | 700 |
| `brainstorming-scpsl-features` (the heaviest process skill) | 900 |
| Any other `SKILL.md` | 1,000 |
| Sibling reference files | no limit; loaded only when the skill points at them |

The current numbers are not kept in this file, because a hand-filled table goes stale with the next
edit. Print them with:

```
python3 tools/check-skills.py --sizes
```

It lists every skill with its words, limit and headroom, then the median, mean and largest, and the
same for sibling reference files.

The first draft aimed at "entry under 200 words, skills under 500", and that did not survive
contact with the content: the entry skill has to carry the path choice, the routing table and the
seven rules, and a skill with a Rules table, Common Mistakes and Red Flags needs about 600 to
700 words to be usable alone. So the limits above are measured, not aspirational, and carry
headroom so that a small addition does not trip them. When a `SKILL.md` grows past its limit, move
detail into a sibling file and leave the rule, the gate or the red flag in `SKILL.md` in short form
(this is how `brainstorming-scpsl-features` went from 1,138 to 833 words with nothing dropped).
`tools/check-skills.py` also checks frontmatter, the version line and links.

## Layout

```
.claude-plugin/        plugin.json, marketplace.json
hooks/                 SessionStart: injects the index only in SCP:SL plugin projects
skills/<name>/         SKILL.md plus reference files and scripts; flat namespace, 35 skills
examples/              20 compiled example projects, one per recipe or snippet set; build-all.py
tools/SlKit/           C# (net48) runtime toolkit for plugins, versioned (0.2.0, CHANGELOG.md)
tools/scpsl-lint/      static scanner, 19 rules, reads the event catalog at runtime
tools/sl-probe/        frame-time measurement plugin on SlKit
tools/hot-bench/       test-server plugin that measures what a hot-event subscription costs
tools/catalog/         machine-readable LabAPI event catalog (frequency, hot, burst, cancellation)
tools/check-*.py       skill and publishability checks
tests/                 hook and script tests; RESULTS.md and scenarios.md for the skill tests
docs/                  this file and maintenance.md
```

## Skills

Three layers.

- **Process**: `using-scpsl-skills` (entry), `brainstorming-scpsl-features`, `writing-scpsl-plans`,
  `executing-scpsl-plans`, `testing-scpsl-plugins`, `running-a-test-server`,
  `debugging-scpsl-plugins`, `verifying-scpsl-work`.
- **Recipes**: nine `adding-*` skills, each with a compiled example in `examples/`.
- **Knowledge**: `designing-scpsl-plugins`, `labapi-events`, `labapi-wrappers`, the `scpsl-*`
  engine skills (threading, lifecycle, hints, SSS, commands, roles, voice, networking, Harmony, cost
  model), `measuring-scpsl-performance`, `reviewing-scpsl-plugins`, `porting-exiled-to-labapi`,
  `deploying-scpsl-plugins`, `researching-scpsl-internals`.

The full list with one-line purposes is in the README.

### The entry skill and the path choice

`using-scpsl-skills` picks one of three paths so that the process matches the task: A for a small
change (knowledge, no ceremony), B for a bounded feature (short brainstorming, a recipe,
test-first), C for a new plugin or anything with timers, IO, HUD, persistent state, hot events,
patches or several servers (brainstorming, plan, reviewed execution). Complexity found mid-task
moves the path up, never down.

Authority is ordered: the owner's explicit instructions, then the skills, then the agent's
defaults. The "do not skip" language binds the agent's own shortcuts, not the owner's choices.
The agent loads only the skills the path needs; reading the index is cheap, loading the library
is not.

## Toolkit (SlKit)

Small, dependency-free, net48, built against the game's own managed assemblies. Semantic
versioning from 0.2.0; a minor release may break the API while the major version is 0, and every
break is listed in `tools/SlKit/CHANGELOG.md` with a migration note.

- `SlClock`: one wall-clock scheduler in one PlayerLoop entry for all periodic plugin work,
  with staggering of per-player work across frames and a failure policy (back-off by default).
- `MainThread`: a budgeted queue from background threads back to the main thread.
- `Background`: a bounded worker per plugin for IO. `SlHttp`: HTTP over it, with per-client
  limits, a response size cap and no shared mutable client.
- `FrameProfiler`: real frame-time measurement with percentiles. `HotEventAudit`: a runtime list
  of who subscribes to hot events.
- `RateLimitedLog`, `Polyfills` (BCL gaps on the server runtime), and the pure `SlKit.Core` types
  that carry the unit tests.
- Not in SlKit: the tickrate cap. It changes a server-wide setting, so it is the separate plugin
  `tools/tickrate-fix` that the server owner installs on purpose.

## Verification

- Skills were written test-first: baseline runs without the library, then runs with it, graded
  against the research. Method, results and their limits: `tests/RESULTS.md`; the prompts and
  fact sheets to repeat it: `tests/scenarios.md`.
- The event catalog is derived from the code (see maintenance.md). `hot`, `burst` and
  `conditionallyHot` follow from the frequency class and the listed conditions, not from hand
  flags, and a test checks the embedded hot list against the catalog.
- The toolkit compiles against the installed game assemblies; its pure logic has unit tests;
  `examples/build-all.py` builds every project under `examples/` and `tools/`, runs every test
  runner, and lints `examples/` plus the plugin-shaped tools (SlKit, sl-probe, hot-bench).
- `tools/check-skills.py` and `tools/check-publishable.py` pass before a release.
