# Parallel lanes for SCP:SL plan tasks

Verified against LabAPI 1.1.7 / SCP:SL 14.2.7 (2026-10).

A plugin has one main thread, one `Enable()`, one subscription list and (by design) one owner of
per-round state. Two agents that both "just add a handler" are both editing the same ordering-
sensitive code. Lane the plan before dispatching anything.

## The test

Two tasks may run at the same time only if all three hold:

1. **Disjoint files.** Each task's file list is in the plan, and no file appears twice.
2. **Fixed interfaces.** Everything a task consumes from another (type names, method signatures,
   snapshot struct fields, config keys, command names) is spelled out in the plan, not "to be
   decided by Task 3".
3. **No runtime ownership.** Neither task subscribes to events, starts a clock job, owns
   per-round state, sends hints, crosses a thread boundary or patches with Harmony.

If any fails, the tasks run one after another in plan order.

## Parallel-safe kinds

| Kind | Why it is safe | Condition |
|---|---|---|
| Pure-logic module (scoring, AFK math, streaks, parsers, rate limits) | No LabAPI, Unity or game types; compiles and tests on plain .NET, the way `SlKit.Core` does | Inputs are plain snapshots (structs, strings, numbers), never `Player` |
| Its unit tests | Separate test project, plain .NET runner | One test project per lane, path fixed in the plan, compiling only that lane's files. A shared `Core.Tests` globs all of `Core/` and shares one `obj/`: it compiles another lane's half-written file and races on the build. Run it after the wave instead |
| Config class | One file; the loader reads public `{ get; set; }` properties | Property names, defaults and snake_case keys fixed in the plan |
| One command per file | LabAPI registers `[CommandHandler(...)]` classes itself, before `Enable()`; the plugin class is untouched | Command names and aliases fixed in the plan (duplicate names are guarded by name only, silently). The file sits in the shared plugin project, so that agent does not build (see below) |
| HUD text rendering | A pure function from a snapshot to a string | The HUD sender is not in this lane |
| Docs, config comments, README | No code | None |

## Sequential kinds (one owner, plan order)

| Kind | Why it must be serialized |
|---|---|
| Plugin class: `Enable()`, `Disable()`, `ServerEvents.Shutdown` | Order matters: fallible work first, seed state, subscribe last, undo and rethrow on failure. Two authors cannot both own the order. |
| Event wiring | Every `+=` needs its `-=` on the same lifecycle path, and hot-event decisions need one view of all subscriptions. |
| Per-round state owner | Reset at `WaitingForPlayers`, keyed by `UserId`. Two writers diverge at the first restart. |
| Clock jobs and the HUD sender | One clock for periodic work, one HUD owner per server. |
| Thread boundary (`Background`/`SlHttp`, `MainThread.Post`) | Race bugs live here; review on the strongest tier. |
| Harmony patches | One Harmony id, `PatchAll` once. |
| `.csproj`, `Directory.Build.props`, references | Change the build for every lane. |

## Waves

```
Wave 0 (sequential)  skeleton: plugin class, Config shape, snapshot structs, interfaces
Wave 1 (parallel)    pure logic + tests | commands | HUD text | config docs
Wave 2 (sequential)  wiring into the plugin class, round-state owner, clock jobs, IO boundary
Wave 3               integration gate, whole-plan review, optional smoke run
```

Dispatch all Wave 1 agents in one message (that is what makes them parallel). An agent builds and
tests only a project it alone owns (its own test project). Files that live in the shared plugin
project (commands, config) are not built by the lane agents: they report `build: deferred`, run
lint on their own files and confirm every API name in source. The controller builds the plugin
project once after the wave and routes errors back to the owning agent. Parallel builds of one
project write the same `obj/` files (ordinary .NET build behaviour, not engine-specific).

After a parallel wave, check the reports for files touched outside the declared list. A stray edit
is a finding, even when it builds.

## Example: kill stats with a HUD on a 60-player server

| Task | Lane | Model and why |
|---|---|---|
| 1. Skeleton, `Config`, `KillSnapshot` struct | Wave 0 | Sonnet: sets interfaces every later task consumes. |
| 2. `KillStats` pure logic + tests | Wave 1 | Haiku if the plan has the code, else Sonnet. |
| 3. `HudText.Render(KillSnapshot)` | Wave 1 | Haiku: pure string building from a fixed struct. |
| 4. `stats` RA command | Wave 1 | Sonnet: permission check and argument parsing from prose. |
| 5. Wire `PlayerEvents.Death`, reset at `WaitingForPlayers` | Wave 2 | Sonnet: a per-lifecycle event, but it edits `Enable()` order. |
| 6. HUD sender on one clock, coexisting with the server's existing HUD | Wave 2 | Strongest: shared screen space with another plugin is a design decision. |

## Re-verifying on a new build

- Commands still registered by the loader before `Enable()`, and duplicate names still guarded by
  name only (scpsl-commands-and-permissions).
- Config loader still reads public get/set properties only (deploying-scpsl-plugins).
- `WaitingForPlayers` still raised once per round after the scene reload (scpsl-game-lifecycle).
