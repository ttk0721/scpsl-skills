# Plan template (writing-scpsl-plans)

Verified against LabAPI 1.1.7 / SCP:SL 14.2.7 (2026-10).

Copy the blocks below into `docs/plans/YYYY-MM-DD-<feature>.md`. Text in `<angle brackets>` is a
slot the plan fills from the approved spec (brainstorming-scpsl-features, `spec-template.md`). A slot left unfilled in a saved plan is a placeholder, and a
placeholder is a plan failure.

## 1. Header and Global Constraints

````markdown
# <Feature> Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: executing-scpsl-plans. Steps use `- [ ]` checkboxes.

**Goal:** <one sentence>
**Architecture:** <2-3 sentences: what lives in Core/, what the wiring subscribes to, who owns state>
**Spec:** <`docs/specs/YYYY-MM-DD-<topic>-design.md`, approved by the owner>

## Global Constraints

Every task includes these. Values are copied from the spec, not re-derived.

- Target: LabAPI 1.1.7, SCP:SL 14.2.7, `net48`. `RequiredApiVersion => new Version(1, 1, 7)` (a literal).
- Build against the server's own `SCPSL_Data/Managed` at `<path>`: `FrameworkPathOverride` set to it,
  `DisableImplicitFrameworkReferences` and `AutomaticallyUseReferenceAssemblyPackages=false`,
  `mscorlib`/`System`/`System.Core`/`netstandard` plus every game DLL referenced from it with
  `Private=false`. Never ship game DLLs.
- SlKit: every timer is an `SlClock` job (no MEC coroutine per player, no `CallDelayed` per event);
  IO goes through the plugin's own `Background.ForOwner` worker or `SlHttp.CreateClient` client; results come back by callback or `MainThread.Post`;
  frequent handlers log through `RateLimitedLog`. `SlKit.dll` goes in `LabAPI/dependencies/`.
- Threads: code off the main thread reads plain snapshots only (strings, numbers, POCOs).
- Lifecycle: idempotent `Enable()`, fallible work first, subscribe last, roll back on throw.
  `Disable()` is never called: flush synchronously on `ServerEvents.Shutdown`. Per-round state is
  reset at `ServerEvents.WaitingForPlayers`; state that spans rounds is keyed by `UserId`.
- Config: read at use time, `Config ??= new()`, a list in the file replaces the default list.
- Original code only. Nothing copied from Northwood or from decompiled game code.
- Environment (spec Context): <peak players>; <LabAPI only | EXILED x.y loaded alongside>;
  neighbours on the same surface: <list, e.g. "another plugin rewrites hints on spawn">;
  <servers sharing data>; <restart cadence>; <idle hours>.
- Business rules: <one line each, e.g. "staff groups earn nothing", "never auto-ban">.
- Test server (spec Test plan): <"no: owner declined, final" | "own private instance on port N" |
  "owner's port N, at the owner's request">.

### Event and cost budget

| Event or job | Catalog class | Hot | Work per raise | Budget |
|---|---|---|---|---|
| <PlayerEvents.X> | <per-action> | <no> | <one dictionary write> | <-> |
| <job "feature.tick"> | <n/a, 1 Hz> | <-> | <O(players)> | <avg us per frame, from the spec> |

Rows come from the spec's Cost budget. No number means the feature is not designed yet: back to
brainstorming-scpsl-features and designing-scpsl-plugins. A hot event row describes its gate.

## Review Focus

<5 lines: condition, expected behavior, owning task. Typical SCP:SL classes: round restart
mid-operation, player leaves mid-period, idle mode, peak players, another plugin on the same
surface, IO down or slow, empty config.>

---
````

## 2. Server folder defaults

Use the default for the OS if the folder exists on this machine (`ls` it, never search the disk). If it is missing, or the owner's server lives elsewhere (SteamCMD, hosting panel, another Steam library), ask once for the install folder and reuse the answer. Defaults:

| OS | `SCPSL_Data/Managed` (Steam install of the dedicated server) |
|---|---|
| Linux | `~/.local/share/Steam/steamapps/common/SCP Secret Laboratory Dedicated Server/SCPSL_Data/Managed` (`~/.steam/steam` usually links to `~/.local/share/Steam`) |
| Windows | `C:\Program Files (x86)\Steam\steamapps\common\SCP Secret Laboratory Dedicated Server\SCPSL_Data\Managed` (Steam default, not checked on Windows) |

SteamCMD and hosting panels install elsewhere. Config, plugin and log folders, and how to run a
private instance, are in running-a-test-server.

## 3. File structure

```
<Plugin>/
  <Plugin>.csproj           net48, Managed references, <Compile Remove="tests/**" />
  Plugin.cs                 Enable/teardown, subscriptions, clock jobs; forwards to Core
  Config.cs
  <Feature>State.cs         the one owner of per-round state (or the plugin class itself)
  Commands/<Name>Command.cs
  Core/                     pure C#, BCL only: every decision worth a test
  tests/
    Directory.Build.props   empty <Project/>: stops the net48/game props from reaching the tests
    <Plugin>.Core.Tests/    net10.0 exe, compiles ../../Core/*.cs, exit code 0 = pass
docs/plans/
```

- `Core/` never mentions LabAPI, Unity, Mirror, game namespaces or the SlKit runtime (`SlKit.Core`
  sources may be compiled in, testing-scpsl-plugins). The test project has no game references, so
  its build is the purity check. The wiring converts at the boundary: `Vector3` to your own struct, `Player` to `UserId` plus primitives, time to
  `double` seconds passed in (tests pass explicit values, so no clock is mocked).
- Two builds, two different checks: the net48 build against Managed proves the server's Mono
  BCL has every API used; the tests run on modern .NET and prove the logic only.
- The test project (trimmed from the one compiled in `examples/activity-time`):

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>disable</Nullable>
    <ImplicitUsings>disable</ImplicitUsings>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="../../Core/*.cs" Link="Core/%(Filename)%(Extension)" />
  </ItemGroup>
</Project>
```

A `Program.cs` with `Run(name, test)` and a non-zero exit code on failure is enough; no NuGet test
framework is needed (SlKit's own tests work this way). Set `TargetFramework` to the newest modern
.NET the machine has (`dotnet --list-sdks`); the runner only needs to compile the plain C# in `Core/`.

## 4. Task template

````markdown
### Task N: <component>

**Files:**
- Create: `Core/<X>.cs`, `tests/<P>.Core.Tests/<X>Tests.cs`
- Modify: `Plugin.cs` (<which method>)

**Skills:** <recipe skill, e.g. adding-per-player-tracking>; <domain skills, e.g. labapi-events>

**Interfaces:**
- Consumes: <exact signatures from earlier tasks>
- Produces: <exact signatures later tasks use>
- Game/SlKit members used (each looked up): <`Player.PermissionsGroupName`, `SlClock.EveryStaggered<T>(...)`>
- Budget rows: <rows of the budget table this task adds>

- [ ] **Step 1: Write the failing Core test** <code>
- [ ] **Step 2: Run it.** `dotnet run --project tests/<P>.Core.Tests -c Release` → FAIL (<message>)
- [ ] **Step 3: Implement in Core/** <code>
- [ ] **Step 4: Run it.** → `ALL n TESTS PASSED`, exit 0
- [ ] **Step 5: Wire it** <code: subscription and its teardown line, or clock job>
- [ ] **Step 6: Gates**
  - `dotnet build <P>.csproj -c Release -p:SL_REFERENCES="<Managed>"` → 0 errors
  - `dotnet run --project tests/<P>.Core.Tests -c Release` → exit 0
  - `grep -rEn "^using (LabApi|UnityEngine|Mirror)" Core` → no output (the test build is the real purity check)
  - `python3 <scpsl-skills>/tools/scpsl-lint/scpsl_lint.py .` → `0 error(s)`
- [ ] **Step 7 (hot path only): Measure** <what, where, budget row it must meet>
- [ ] **Step 8: Commit** <files, message>
````

## 5. Final tasks

**Test-server task: include only when Global Constraints record consent.** Steps follow
running-a-test-server: a separate instance on an unused port (never the owner's ports unless the
owner asked), confirm from its log that it is not on the public list, install `SlKit.dll` in
`dependencies/<port>` and the plugin in `plugins/<port>`, confirm `Successfully enabled '<Name>'`,
load with dummies, run the sl-probe A/B (measuring-scpsl-performance), stop the instance, remove
only what the task created.

**Deploy-and-verify task: always.** deploying-scpsl-plugins for the copy (one DLL per plugin,
libraries in `dependencies/`, a process restart because nothing unloads) and the boot-log check
(`Successfully enabled`, `Couldn't enable`, `Couldn't load the plugin`); verifying-scpsl-work for
the report, which states what was checked and what could not be. Production deploys happen when
the owner says so, not as a side effect of finishing the plan.

## Re-verifying on a new build

- LabAPI version: update the literal in `RequiredApiVersion` and the Target line.
- The build still needs `FrameworkPathOverride` (check the `csc` command line for reference-pack
  paths) and the Managed folder still holds the DLLs listed in the examples' props.
- SlKit names used here (`SlClock.Every`, `EveryStaggered`, `Background.ForOwner`, `SlHttp.CreateClient`,
  `RateLimitedLog`, `MainThread.Post`) match the SlKit README.
- Catalog frequency classes for the events in your budget table are unchanged.
- Boot-log strings `Successfully enabled`, `Couldn't enable`, `Couldn't load the plugin` still appear.
