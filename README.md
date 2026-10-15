# scpsl-skills

Skills and tools for writing SCP: Secret Laboratory server plugins on LabAPI that are correct and
cheap. At 50–60 players a plugin should be an add-on, not the main load on the server.

It is a Claude Code plugin in the shape of [superpowers](https://github.com/obra/superpowers): an
agent working on an SCP:SL plugin loads the skill that matches the task. People can read the same
files as documentation.

**Written against LabAPI 1.1.7 and SCP:SL 14.2.7.** Most claims were checked against the game and
LabAPI code, many by a second reader, and a few things were measured on a live server. Not
everything was double-checked, and some things could not be confirmed and are marked unverified.
[What was checked, and how](#what-was-checked-and-how) gives the numbers.

> **Status.** Version 0.2.0, MIT licensed. The repository is https://github.com/ttk0721/scpsl-skills.
> It is written for one game and LabAPI version (above); after a game update, expect some
> statements to need re-checking (see [Keeping it true](#keeping-it-true)).

## At a glance

- **35 skills** for Claude Code (also readable as plain Markdown) under [`skills/`](skills/): how to
  plan, build, test, review and deploy an SCP:SL plugin, and how the server, LabAPI and the engine
  behave and what that costs per frame.
- **[SlKit](tools/SlKit/README.md)**: a small C# library (net48, no dependencies beyond the game) that
  plugins share on one server: a single main-thread clock and queue, bounded background workers and
  HTTP clients, rate-limited logging, a frame profiler.
- **Tools** under [`tools/`](tools/): `scpsl-lint` (Python scanner for plugin source), `sl-probe`
  (frame-time measurement plugin), `hot-bench`, `tickrate-fix`, and a catalog of the 377 LabAPI
  events with how often each one fires.
- **20 example projects** under [`examples/`](examples/), one per recipe or snippet set, all
  compiled against the real game assemblies.

Use it in one of two ways:

1. As a **Claude Code plugin**: install it (see [Install](#install)); when you work in an SCP:SL
   plugin project the agent loads the matching skill by itself.
2. As **documentation and libraries**: read the skills in `skills/`, copy `SlKit.dll` into
   `LabAPI/dependencies/global/` (or the sources into your plugin), run `scpsl-lint` over your code.

## What is in it

The skills come in three layers, like superpowers plus a domain:

1. **Process**: how to work. Interview the owner, plan, split the work across agents, test, debug,
   prove it.
2. **Recipes**: how to build a given type of feature. Each has a complete example plugin that
   compiles against the real game assemblies.
3. **Knowledge**: how the engine and LabAPI behave, verified in code and on a live server.

`using-scpsl-skills` ties them together. It is the entry point for SCP:SL plugin work, and it
picks the amount of process the task needs. A three-line change gets the knowledge but skips the
ceremony. A new plugin goes through brainstorming, a plan and reviewed execution. The owner's
explicit instructions outrank the skills; the skills outrank the agent's own defaults. The agent
loads only the skills the chosen path needs.

### Process skills

| Skill | Use it when |
|---|---|
| `using-scpsl-skills` | Always first: picks path A (small change), B (bounded feature) or C (new plugin); routing; seven rules the agent does not skip on its own |
| `brainstorming-scpsl-features` | Before building a feature: SCP:SL question bank (players, servers, EXILED, other plugins, data, failure policy), approaches with per-second cost, approval before code |
| `writing-scpsl-plans` | Turning an approved design into tasks with gates (build against the game DLLs, unit tests, lint, measurement) |
| `executing-scpsl-plans` | Running a plan with subagents: what parallelises in a single-threaded Unity plugin, model choice per task, spec and engine review per task |
| `testing-scpsl-plugins` | Test-first: pure logic on plain .NET, seams around LabAPI, lint, server smoke tests only with consent |
| `running-a-test-server` | A local server: asks first, default paths for Linux and Windows, never the owner's ports, private check, dummies, cleanup |
| `debugging-scpsl-plugins` | Something is wrong: symptom → cause → check tables, logs per port, reproduction |
| `verifying-scpsl-work` | Before saying "done": which evidence proves which claim |

### Recipes (with compiled examples in `examples/`)

| Skill | Feature type |
|---|---|
| `adding-per-player-tracking` | Playtime, AFK, activity, per-player stats |
| `adding-player-hud` | A HUD element that coexists with other hint plugins |
| `adding-custom-items` | Items tagged by serial |
| `adding-custom-roles` | Custom roles: spawn, loadout, abilities, cleanup |
| `adding-gameplay-rules` | Friendly fire, damage modifiers, interaction rules through cancellable events |
| `adding-external-integrations` | HTTP, webhooks, panel APIs, databases without blocking the server |
| `adding-round-mechanics` | Lobby lock, custom end conditions, waves, per-round timers |
| `adding-map-automation` | Doors, lights, elevators, admin toys at the right moment |
| `adding-player-abilities` | Abilities on a client keybind (Server-Specific Settings) |

### Knowledge skills

| Skill | Use it when |
|---|---|
| `designing-scpsl-plugins` | The architecture checklist and defaults behind any plugin |
| `labapi-events` | Subscribing to anything. Includes a catalog of all 377 events with how often each fires |
| `scpsl-main-thread-and-timing` | Timers, coroutines, threads, async, HTTP |
| `labapi-wrappers` | Player, Server, Round, Item, Pickup, Door, Room, Toy APIs |
| `scpsl-game-lifecycle` | Anything tied to joins, rounds or restarts (a restart reloads the scene) |
| `scpsl-hints-and-hud` | HUDs, hints, broadcasts |
| `scpsl-server-specific-settings` | Client settings, keybinds, sliders (SSS) |
| `scpsl-commands-and-permissions` | RA, server-console and client commands; permissions |
| `scpsl-roles-damage-effects` | Roles, spawning, damage, status effects, ragdolls |
| `scpsl-voice-and-audio` | Voice routing, muting, playing audio |
| `scpsl-networking-for-plugins` | What plugin actions cost on the wire; hiding players |
| `scpsl-harmony-patching` | When and how to patch safely |
| `scpsl-engine-cost-model` | Where a server tick goes, and what a plugin can and cannot fix |
| `measuring-scpsl-performance` | "The server is slow": measuring it properly (`Server.Tps` hides hitches) |
| `reviewing-scpsl-plugins` | Auditing an existing plugin |
| `porting-exiled-to-labapi` | Moving off EXILED |
| `deploying-scpsl-plugins` | Build setup (including the `Northwood.LabAPI` NuGet package and what it does not cover), folders, load failures, per-client builds |
| `researching-scpsl-internals` | A question these skills do not answer, or re-checking them after a game update: decompiling, grep recipes, a namespace map, the analyst + skeptic method |

### Tools

- **`examples/`**: 20 small projects, one per recipe or snippet set, all compiled against the
  game's own assemblies. The linter reports 0 errors on them and 4 SL001 warnings: each is a
  deliberate subscription to a conditionally hot damage event (`Hurt` in `adding-custom-roles`,
  `Hurting` in `adding-gameplay-rules`, `labapi-events-snippets` and `porting-exiled-pairs`), and
  each handler is gated by an early return and does no per-raise logging or allocation. It also
  prints 4 SL003 notes (an `IsAllowed` assignment the game ignores in one situation, which the
  author is asked to check). `examples/minimal-plugin` is the smallest correct plugin
  (lifecycle done right). `python3 examples/build-all.py` finds every `.csproj` under `examples/`
  and `tools/` (33 on this tree: the 20 examples and their 3 test projects, SlKit and its 4 test
  projects, its QuickStart, sl-probe, hot-bench, tickrate-fix and its test project), builds SlKit
  first, runs the test runners and the two Python suites, and lints five targets separately
  (`examples/`, `tools/SlKit`, `tools/sl-probe`, `tools/hot-bench`, `tools/tickrate-fix`; one lint run
  is one project for SL015). `tools/hot-bench` shows 3 SL001 warnings by design: it subscribes to a
  hot event because that is what it measures. `tools/scpsl-lint` is not linted, because its test
  fixtures are bad code on purpose. It prints one table of OK/FAIL per target.
- **`tools/SlKit`**: a small net48 runtime toolkit for plugins, **version 0.2.0** (semantic
  versioning from here on; see its [CHANGELOG](tools/SlKit/CHANGELOG.md) for every breaking change
  and how to migrate). One wall-clock scheduler for all periodic work (`SlClock`, with staggering of
  per-player work across frames; failure handling is set per job through `ClockJobOptions`, and by
  default a failing job backs off instead of being disabled for good; `ClockScope.Round` jobs end at
  round restart with no extra wiring), a budgeted queue back to the main thread (`MainThread`; its
  1 ms per-frame budget can only be changed by the server owner, through the environment variable
  `SLKIT_MAIN_THREAD_BUDGET_MS`), a bounded worker per plugin for
  IO (`Background.ForOwner`), HTTP with per-client timeout, headers, concurrency and a 1 MiB
  response cap (`SlHttp.CreateClient`; the shared mutable `SlHttp.Client` is gone). A request that
  waited too long for a slot now ends as `QueueTimedOut` (never sent) and no longer looks like a
  `TimedOut` request (sent, no answer), because the queue wait and the request are timed
  separately. SlKit also has real frame-time measurement (`FrameProfiler`), a runtime audit of who
  subscribes to hot events (`HotEventAudit`), rate-limited logging, a warning when more than one
  copy of SlKit is loaded, and polyfills for the few APIs the server runtime lacks. SlKit is one
  DLL shared by every plugin on the server, so it has no public static setting that one plugin could
  change to alter how another plugin's jobs behave (a test fails if one appears), and `SlClock.Install`
  and the registration methods throw when called off the main thread. Plugins built against 0.1 must
  be rebuilt. See its [README](tools/SlKit/README.md).
- **`tools/tickrate-fix`**: a standalone LabAPI plugin, not part of SlKit, that makes the
  `server_tickrate` config value actually apply. It used to be a SlKit call, which let any plugin
  change the whole server's frame rate as a side effect; now the server owner installs it on
  purpose. Version 0.1.0, **off by default** (it then only logs `targetFrameRate`), and it never
  lowers a `server_tickrate` the owner set: its cap replaces only `-1` and `0` (unlimited). See its
  [README](tools/tickrate-fix/README.md).
- **`tools/scpsl-lint`**: a dependency-free Python scanner for plugin source, 19 rules (SL001 to
  SL019). It catches subscriptions to hot events, per-raise work inside the handlers of hot and
  burst events, events the game never raises, setters the game ignores, `Server.Tps`,
  `KillCoroutines()`, blocking IO on the main thread (also inside `ICommand.Execute` and
  `SlClock.Every` lambdas), process-wide exception hooks, unsafe threading and more. A subscription
  to a hot event is a warning (SL001); work inside its handler (logging, string building,
  allocation, LINQ, locks, player-list enumeration, IO) is an error (SL018), and the same inside a
  per-shot "burst" handler is a warning (SL019). It resolves `using static` and alias spellings
  of event names. It reads the event catalog at runtime. SL007 (blocking calls) reports every
  blocking call on a line at its own column, and follows same-file helper methods up to three calls
  deep from a main-thread entry point, naming the path (for example `Save() is reached from
  Execute`). It does not follow other receivers, other files or off-thread work, and it exempts
  `ServerEvents.Shutdown`, where SL015 itself asks for a synchronous flush. The messages carry no
  benchmark numbers: they point at `tools/hot-bench/README.md`.
  `python3 tools/scpsl-lint/scpsl_lint.py path/to/plugin`
- **`tools/sl-probe`**: a measurement plugin built on SlKit that reports p50/p95/p99 frame time and
  the share of frames over 50 and 100 ms, with CSV output and a comparison script for A/B rounds.
  Use it instead of `Server.Tps`.
- **`tools/hot-bench`**: a test-server plugin that measures what subscribing to a per-pair hot
  event costs, against what the handler does per raise. The finding is that per-raise work
  dominates. Its [README](tools/hot-bench/README.md) is the only place the numbers are kept, with
  the machine, the arms, the spread and what was not measured. The full-server rate (60 alive
  players, about 208,000 raises/s) was measured directly; the limits are dummies only (no real
  players) and one machine.
- **`tools/catalog/labapi-events.json`**: the event catalog in machine-readable form. For each
  event it lists where the game raises it, its frequency class, cancellation, setters the game
  ignores and a confidence level, and whether a second reader re-derived it. Three flags are
  derived from the frequency, never set by hand: `hot` (14 events: raised every frame or at a
  high packet rate), `burst` (15 events: once per shot or hit) and `conditionallyHot` (2 events:
  per-frame only in listed situations). Ignored setters come in two lists:
  `ignoredSetters` (the game ignores them always) and `conditionallyIgnoredSetters`
  (`[{setter, when}]`: ignored only in the situation described). The linter (SL003) warns on the first
  list and only prints an info note for the second. `pair` is `null` or `{event, role}` with the role
  `before` or `after`, and it is always symmetric: if A is B's `before`, B is A's `after`.
- **`tools/check-skills.py`** and **`tools/check-publishable.py`**: see [Checks](#checks).
- **`skills/running-a-test-server/scripts`**: shell scripts for a local test server on Linux.
  `start-server.sh <port>` takes `--install DIR`, `--wait SECONDS`, `--allow-existing-port` (only
  when the owner asked to use that port) and `--accept-eula` (only after the owner accepted the
  EULA; the script never accepts it on its own). It refuses a port with a live listener, even
  with `--allow-existing-port`, and records the install it used. `stop-server.sh <port> [--kill
  [--sigkill]]` verifies the process is ours before it signals anything, then checks that the
  process and its children are really gone (exit 5 if not). `list-ports.sh [--suggest | --has <port>
  | --listening <port>]` falls back to `/proc/net` without `ss` and exits 3 ("cannot tell") rather
  than calling a port free. `list-created.sh <port> [--since EPOCH] [--install DIR]` uses the
  recorded install and asks when it cannot tell which one. `send-command.sh`, `check-private.sh` and
  `find-install.sh` complete the set.

## Install

As a Claude Code plugin, from the GitHub repository:

```
/plugin marketplace add ttk0721/scpsl-skills
/plugin install scpsl-skills@scpsl-skills
```

Or from a local clone: replace the first line with `/plugin marketplace add /path/to/scpsl-skills`.

The SessionStart hook loads the skill index only when the working directory looks like an SCP:SL
plugin project: a `.csproj`, `Directory.Build.props`, `Directory.Build.targets` or other
`*.props`/`*.targets` file with a `Reference` or `PackageReference` to LabApi, `Northwood.LabAPI`,
`PluginAPI`, NWAPI or EXILED (`Exiled`, `Exiled.*`, `ExMod.Exiled`), or a `HintPath` into
`SCPSL_Data` or to a file named `LabApi.dll` or `Exiled*.dll`. The hook matches the reference
itself, case-sensitively, after removing XML comments. A project that only has `Acme.PluginApi.csproj`
as a project reference, or the word "labapi" in a comment, does not count. A reference to
`Assembly-CSharp` alone does not count either, because every Unity mod has one, and neither does a
`ProjectReference`. The "SCP Secret Laboratory" marker was dropped: it only ever matched prose.
The hook reads `*.csproj`, `Directory.Build.props`, `Directory.Build.targets` and any other
`*.props` or `*.targets` file, plus the `Directory.Build.props`/`.targets` in the folders above the
working directory, with the same exact markers and comment stripping. Inside a git work tree it
asks git for the files instead of walking the directory tree. Outside git it runs one bounded
`find`: at most 8 levels deep, skipping hidden directories, `bin`, `obj`, `node_modules`,
`packages`, `Library` and `Temp`, limited to 5 seconds where `timeout` exists. It reads the
candidates as `find` prints them and stops reading at the first match, so a hit does not wait for
the rest of the tree; a project nested deeper than 8 levels is not seen outside git. At most 1000
files are read (`SCPSL_SKILLS_MAX_FILES` changes that).

The hook runs on `startup`, `clear` and `compact`, not on `resume`: a resumed session replays a
transcript that already contains the index injected when it started, so injecting again would
duplicate it, while `clear` and `compact` discard the earlier context and need the index back.
The injected text names the absolute plugin root (from `CLAUDE_PLUGIN_ROOT`), the skills directory
and the path of `events-catalog.md`, so the agent can open the files the index refers to. Set
`SCPSL_SKILLS_ALWAYS=1` to load the index everywhere.

### What gets installed

The marketplace entry has `"source": "./"`, so the plugin cache receives the whole repository, not
only `skills/`. Measured on this tree, without git history and build output: **about 3.4 MB**.

| Part | Size | Why it is there |
|---|---|---|
| `skills/` | 1.3 MB | The skills: 35 `SKILL.md` and 104 reference files |
| `tools/` | 1.3 MB | `tools/catalog/labapi-events.json` (0.4 MB) is read at runtime by `scpsl-lint`; SlKit, sl-probe, hot-bench and tickrate-fix sources are what the skills link to |
| `examples/` | 0.6 MB | Compiled example plugins that the recipe skills link to by relative path |
| `tests/`, `docs/`, `hooks/` | 0.2 MB | The hook is part of the plugin; the rest is documentation |

Nothing here is loaded into the agent's context at install time: the hook injects the one index
skill, and every other file is read only when a skill points at it. The cost is disk space. Game assemblies are never included (`.gitignore`).

### Building the tools

To build the tools you need the .NET SDK and an SCP:SL dedicated server install. The projects
compile against the server's own `SCPSL_Data/Managed` assemblies, never the net48 reference pack.
`deploying-scpsl-plugins` explains why, and also what the `Northwood.LabAPI` NuGet package gives
you (the LabApi reference and XML documentation, byte-identical to the server's copy for the same
version) and what it does not (the game assemblies and the server's base class library).

```
dotnet build tools/SlKit/src/SlKit.csproj -c Release -p:SL_REFERENCES="/path/to/SCPSL_Data/Managed"
dotnet run --project tools/SlKit/tests/SlKit.Tests/SlKit.Tests.csproj
python3 -m unittest discover -s tools/scpsl-lint/tests
```

## Checks

```
python3 tools/check-skills.py                         # frontmatter (needs PyYAML), version line, size limits, links and #anchors
python3 tools/check-skills.py --sizes                 # every skill: words, limit, headroom
python3 tools/check-publishable.py [--corpus <dir>]   # decompiler fingerprints, artefacts, verbatim overlap
python3 examples/build-all.py                         # build, test and lint everything
python3 -m unittest discover -s tools/scpsl-lint/tests
python3 -m unittest discover -s tests/hooks           # hook, test-server scripts, the checkers, manifests, counts quoted in the docs
dotnet run --project tools/SlKit/tests/SlKit.Tests/SlKit.Tests.csproj
```

**Size policy.** `check-skills.py` fails when a `SKILL.md` body (frontmatter excluded) is over its
limit: 700 words for the entry skill `using-scpsl-skills`, 900 for `brainstorming-scpsl-features`,
1,000 for every other skill. Reference files next to a `SKILL.md` have no limit. The current
numbers come from `--sizes`, so this README does not quote them; the reasons for the limits are in
[`docs/design.md`](docs/design.md).

`check-skills.py` needs PyYAML (`pip install pyyaml` or your distribution's `python-yaml`). It
parses the frontmatter the way Claude Code does and refuses to run without PyYAML (exit 2), because
a hand-written fallback would accept descriptions that real YAML loaders reject. Besides the
frontmatter, the version line and the size limits, it checks every relative link and image target
and every `#anchor` (same file or another `.md`, with GitHub's heading rules) in all Markdown under
`skills/`, `docs/` and this README. Links inside code fences and inline code are skipped.

`check-publishable.py` has four checks of very different strength:

1. **Fingerprints** (always on): decompiler headers, compiler-generated names, local user paths
   and references to private research. Weak: it only sees output that still carries those marks.
2. **Artefact detector** (always on): what survives retyping, in prose and data files (`.md`,
   `.json`, `.txt`, `.yml`): decompiler-style locals (a generic name with a counter appended),
   statement-shaped code in a sentence or a JSON string, private underscore fields of game types,
   line references into decompiled files and research tags. In fenced blocks and code files only the locals, line-reference and tag
   checks run, because the examples are original code.
3. **Verbatim overlap** (only with `--corpus <decompiled tree>`; the tree is never committed): at
   least `--min-run` (3) consecutive lines found in the corpus, and a token n-gram check (12
   tokens by default) that ignores whitespace, case, quotes, digit suffixes and leading
   underscores. That finds a fragment inside one long string, and code with renamed variables or
   a changed layout. A window counts only if it looks like code and not like a framework idiom
   (enough distinct identifiers and operators, present in few corpus files). The few matches
   reviewed by hand are in `tools/check-publishable.reviewed`, each with a reason, and a stale entry
   is itself a finding. Only this check can back "nothing was copied".
4. **Private words**: patterns from a local, untracked `.private-words` file.

The ignore marker `check-publishable: ignore-file <reason>` is honoured only in the first 10 lines
of a file under `tools/scpsl-lint/tests/fixtures`, only for checks 1 and 2, and only with a reason.
A marker anywhere else, or without a reason, is a finding, and every honoured marker is listed in
the report. Without `--corpus` the tool prints "no findings, but WEAK CHECK": it did not test for
copying. `--require-corpus` turns a missing corpus into an error (exit 2).

## What was checked, and how

Read this as a list of what each kind of evidence is worth. The numbers are from the files in this
repository unless it says otherwise.

1. **Research notes.** Claims are derived from reading LabAPI 1.1.7 and the game code, with a citation to the code for each. The notes cover the loader, events, wrappers, threading, hints, commands, lifecycle, roles, networking, voice, Harmony and EXILED mapping.
   The notes are private and cite decompiled code, so you cannot re-check them from this repository.
2. **Event catalog, 377 events.** For each event the catalog records where the game raises it and how often. **240 of 377 rows (64%)** were independently re-derived
   (`independentlyVerified: true`); the other **137 (36%) were checked by one reader only**. The
   independent check was not spread evenly, on purpose: it covers **all 14 hot events**, both
   conditionally hot events, 14 of the 15 burst events, 154 of the 173 cancellable events, 20 of
   the 23 events with a setter the game always ignores and 10 of the 11 with a setter it ignores
   only in some situation. Confidence is `high` for 344 rows, `medium` for 31 and `low` for 2.
   Four events are classed `never-fired`: LabAPI declares them, but nothing calls their dispatcher
   (no call site in the game build and none in LabAPI itself), so subscribing does nothing. None of the 137 single-reader rows is classed per-frame, per-frame-per-pair,
   high-rate or never-fired: they are per-action (101), on-request (16), per-round (12),
   per-lifecycle (5) and once (3). A wrong class there matters less, but a per-action event can
   still be a burst, so do not read "single reader" as "harmless".
3. **Skills, written test-first.** Each skill was fact-checked claim by claim.
4. **The process layer.** [`tests/RESULTS.md`](tests/RESULTS.md) has the
   tables and the full list of limits; [`tests/scenarios.md`](tests/scenarios.md) has the prompts
   and fact sheets to repeat the runs. **The runs can be repeated; the original grading cannot**,
   because it used private notes. A new run can be re-graded with the public
   [`tests/answer-keys.md`](tests/answer-keys.md): each row names the skill file that states the
   fact and says whether LabAPI's source is enough to check it or a decompiler or test server is
   needed. The run transcripts are kept privately.
5. **Measured on a live server.** SlKit and sl-probe are verified on a 14.2.7 server with dummy players. In idle mode the server runs about one frame
   per second while `Server.Tps` reads 296. 30 dummies cost about 4 ms of main-thread time per frame. The hot-event benchmark
   (60 dummies, 3 runs per arm) found that a counter-only handler on the per-pair visibility event
   is cheap and that per-raise work dominates; its numbers are in
   [`tools/hot-bench/README.md`](tools/hot-bench/README.md) and nowhere else. SlKit 0.2.0 loaded and
   installed its clock on that run. All of it ran on one machine, with dummies and no real
   players. Not measured: real players, long runs, other game versions, other hardware.
6. **What is marked unverified.** Wherever a claim rested on reading code and nothing else, or
   could not be settled (for example how the client treats two hints sent in a row), the skill
   says UNVERIFIED. Treat those as questions, not facts.
7. **No Northwood code, as far as a tool can show.** The research cites decompiled game code, so
   it stays private; the repository describes behaviour and names public types, except that the
   event catalog's `raisedFrom` column names the game method that raises each event, about 60 of
   them non-public, as a location without quoting its code. What is
   actually checked is the four checks of `tools/check-publishable.py` (see [Checks](#checks)):
   decompiler fingerprints, decompiler-style artefacts in prose, data and fenced code, any run of
   3 lines or 12 tokens that also occurs in a local decompiled tree (renamed variables and changed
   layout included), and private words. On this tree, run with the maintainer's local decompile of
   14.2.7, it reports clean, with two reviewed entries (a standard .NET idiom and a public API
   signature). It does not prove that a sentence is not a close paraphrase of game code, and it
   can only compare with the tree it is given.

## Keeping it true

Game updates change things. Every reference file next to a `SKILL.md` ends with a "Re-verifying on a
new build" section; the three short pointer files into `running-a-test-server` point back to it. The
`SKILL.md` files themselves carry just the version line. When the game or LabAPI updates, re-check
those items. [`docs/maintenance.md`](docs/maintenance.md) gives the order of work,
the private-research rule, how the event catalog is derived and regenerated, and what is not
automated. [`docs/design.md`](docs/design.md) describes the design and the size policy, which
`tools/check-skills.py` enforces.

## License

MIT. SCP: Secret Laboratory is a trademark of Northwood Studios. This repository contains no code
or assets from the game. LabAPI is LGPL-3.0 (northwood-studios/LabAPI).
