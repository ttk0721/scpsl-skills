---
name: deploying-scpsl-plugins
description: "Use when building, packaging or installing an SCP:SL LabAPI plugin, when it fails to compile (CS1061 on KeyValuePair deconstruction, Dictionary.Remove, MinBy), is absent from the boot log, logs Couldn't load the plugin, ReflectionTypeLoadException or Couldn't enable, works on one port but not another, rewrites config.yml, or ships as per-client builds."
---

# Deploying SCP:SL plugins

Verified against LabAPI 1.1.7 / SCP:SL 14.2.7 (2026-10).

## Overview

Compile against the server's `SCPSL_Data/Managed` assemblies, ship one DLL, prove after every restart that the loader enabled it. The loader fails quietly: a wrong folder or missing dependency means no plugin and at most one log block.

## When to Use

- Setting up a csproj, copying a DLL to a server, a plugin that "does nothing", CS1061, writing `Enable()`/config/`RequiredApiVersion`.
- NOT for event semantics (labapi-events) or per-round state (scpsl-game-lifecycle).
- Proportionality: a few-line tweak to a plugin that already loads needs only rebuild, one DLL copy and the log check. Run the full checklist for a new plugin, a new dependency, a new port or a per-client build.

## Quick Reference

| Topic | Rule |
|---|---|
| References | `mscorlib`, `System`, `System.Core`, `netstandard` from `Managed/`, `DisableImplicitFrameworkReferences`, `Private=false` on every game DLL. See build-and-references.md |
| LabAPI package | `Northwood.LabAPI` on NuGet = LabApi.dll plus XML docs (IntelliSense). Not the game assemblies or the server BCL; pin the version to the server's. Use it WITH the Managed references. See build-and-references.md |
| Folders | `LabAPI/{plugins,dependencies,configs}/{global,<port>}` under the app-data dir. Every `*.dll` below is loaded, recursively |
| Libraries | `dependencies/`, never `plugins/`. A `plugins/global` plugin needs its libraries in `dependencies/global`. Never ship game DLLs; `MEC` has no DLL of its own |
| Order | dependencies, `global` plugins, `<port>` plugins; then `Enable()` by ascending `Priority` |
| Disable | never called. Flush in `ServerEvents.Shutdown`, synchronously |
| Config | public `{ get; set; }` properties, snake_case keys, rewritten on every load |
| Verify | `Successfully enabled '<Name>'` in the boot log, every plugin |
| Template | Copy `examples/deploy-template`: props, csproj, per-client build, `verify-deploy.sh` |

Details: build-and-references.md, loader-and-config.md, verify-and-brand.md, deploy-template.md.

## Rules

1. **Build against Managed, not the net48 pack.** The server runtime has `KeyValuePair.Deconstruct`, `Dictionary.Remove(key, out v)`, `string.Contains(char)`, `Span<T>`; the net48 pack lacks them, so CS1061 hits code that would run. Fix the references.
2. **Truly absent**: `Enumerable.MinBy/MaxBy/DistinctBy/Chunk/Order`, `Stopwatch.GetElapsedTime`, `Environment.ProcessId`, `Random.Shared`, `IsExternalInit` (`init`/`record` need your own attribute).
3. **One copy of each DLL.** Backups end `.bak`: a stray `Foo.old.dll` is loaded; nothing dedupes.
4. **`Plugin.FilePath`, never `Assembly.Location`.** Plugins load from bytes, so Location is empty and `Path.GetDirectoryName` throws in `Enable()`. `FilePath` is set after the constructor.
5. **`Enable()` runs inside server start**, before any player, room or `Server.Host`, and blocks boot. Fallible work first, subscribe last; on failure undo and rethrow (a swallowed exception still logs "Successfully enabled"). Keep it idempotent (an EXILED-folder reload can call it twice). World state goes in `ServerEvents.WaitingForPlayers`, which fires every round.
6. **`Name` is the config directory.** Constant, unique, path-safe. Metadata getters run outside any try/catch: never throw.
7. **Config is a schema, not a document.** Fields ignored, collections replace defaults, one bad value drops the whole file to defaults, unknown keys deleted, `Vector4` corrupts `w`. Empty file gives `Config == null`: `Config ??= new Config()`. Read `Config.X` at use time (reload replaces the object).
8. **Code changes need a process restart** (no unload exists).

## Common Mistakes

- **`RequiredApiVersion => new Version(LabApiProperties.CompiledVersion)`** (Northwood's examples; use a literal like `new Version(1, 1, 7)`). `CompiledVersion` is computed at runtime from the running LabApi.dll, so the check never fires.
- **net48 csproj with default references and no `Private=false`.** CS1061, and game DLLs ship beside the plugin. Start from deploy-template.md.
- **"Copy the DLL" with no verification.** Restart, then `verify-deploy.sh log`.
- **Deploying to a server nobody offered.** No server or no wish for local tests: ask, never push (running-a-test-server). Never use their ports.
- **Bundling `0Harmony.dll` with the plugin.** LabAPI does not bundle Harmony; the operator keeps one copy in `dependencies/`. Check there first (build-and-references.md).
- **A `plugins/global` plugin whose library sits only in `dependencies/<port>`.** `ReflectionTypeLoadException` on every other port (for example `0Harmony`). Move, not copy, the library to `dependencies/global`; `verify-deploy.sh layout` flags candidates.
- **Referencing `MEC.dll`.** No such file: MEC is in `Assembly-CSharp-firstpass.dll`.
- **Flushing in `Disable()`.** Never called; data is lost on every stop.

## Red Flags

- No success line: wrong folder, `is_enabled: false` (silent), or a missing dependency.
- A whole bin folder copied into `plugins/`; a config that "resets" after a rename; two DLLs with one plugin name.
