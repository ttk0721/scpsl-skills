# Deploy verification and per-client builds

Verified against LabAPI 1.1.7 / SCP:SL 14.2.7 (2026-10).

## Deploy checklist

A compiled version with a script is in `examples/deploy-template` (DEPLOY-CHECKLIST.md, verify-deploy.sh; walkthrough in deploy-template.md). Before step 1, confirm the user has a server and wants a local test (running-a-test-server); never use their running ports.

1. Build `Release` against `Managed/` (build-and-references.md). Confirm the output folder holds only your DLL (and optional `.pdb`), not game DLLs.
2. Copy the plugin DLL to `plugins/<port>/` (one server) or `plugins/global/` (all). Copy shared libraries to `dependencies/`; for a `global` plugin they go to `dependencies/global`, because a library only in `dependencies/<port>` fails on every other port (for example `0Harmony`). Remove the previous copy of the same DLL and any backups named `*.dll` anywhere below.
3. Stop and start the game process. A code change is never picked up without a restart. `labapi reload configs` only reloads configs.
4. Read the boot log (LocalAdmin log, or the server console). For EVERY plugin on the server, look for `Successfully enabled '<Name>'`.
5. Grep for the failure strings below. Zero hits plus the success line is the pass condition.
6. For a plugin with a config: open `configs/<port>/<Name>/config.yml` and check it was written with your keys.

## Strings to grep

| String in the log | Meaning |
|---|---|
| `Successfully enabled '<Name>'` | Enabled. Missing line has two causes: a loader error, or `is_enabled: false` (silent) |
| `Couldn't load the plugin inside` | Assembly failed `GetTypes()`, usually a missing dependency. Followed by `Missing dependencies:` and the exception |
| `Couldn't enable the plugin` | Exception in `LoadConfigs`, command registration or `Enable()` (exception follows), or a refused major-version mismatch (no exception, text explains the `unsupported_loading` switch) |
| `Failed to load the configuration file, using default values.` | Broken `config.yml`, running on defaults, file untouched |
| `Couldn't load the dependency` | A DLL in `dependencies/` failed to load (bad image) |
| `Failed to resolve embedded resources` | Embedded DLL extraction failed |
| `Unable to register command` | Duplicate command name or failing command constructor |
| `Couldn't read the configuration` / `Couldn't create a default instance` | Config or properties problem |
| `Potential issue with plugin` / `Forcefully loading unsupported` | Warnings: `RequiredApiVersion` has a newer minor/patch than the server (loads anyway) / a different major was loaded by override |

`verify-deploy.sh log` also fails on the two warning strings (`Potential issue`, `Forcefully loading`): read them, they mean a version mismatch you should know about.

```
grep -E "Successfully enabled|Couldn't|Failed to load the configuration|Potential issue|Forcefully|Unable to register" "<LocalAdmin log>"
```

## Symptom to cause

| Symptom | Cause | Fix |
|---|---|---|
| No line at all for the plugin, no error | Wrong folder (not under a configured `plugin_paths` entry, wrong port number, typo in `global`), or `is_enabled: false`, or the DLL has no non-abstract `Plugin` subclass | Check path and port; open `properties.yml`; grep your DLL name in the log |
| `Couldn't load the plugin inside '<path>'` + `ReflectionTypeLoadException` | A type signature references an assembly that is not loaded | Put the library in `dependencies/<global\|port>`, restart. Read the exception text, not the "Missing dependencies" list |
| "Missing dependencies" lists an assembly that is present | The list compares `Name vVersion` strings with assemblies loaded at that moment; lazy and version-mismatched entries show up as missing (false positives occur, for example for System.Net.Http and Exiled.API) | Trust the exception |
| `FileNotFoundException` later, at first use | Reference used only inside a method body loads lazily | Same fix: the library is not in `dependencies/` |
| `ArgumentException: Invalid path` from `Enable()` | `Path.GetDirectoryName(Assembly.Location)` on a byte-loaded assembly | `Plugin.FilePath` (read in `Enable()` or later) or `GetConfigDirectory()`. scpsl-lint SL013 |
| Events fire twice, duplicate commands | Same plugin DLL twice (copy, `old/`, in both `global` and `<port>`) | Keep one copy; backups as `.bak` |
| Plugin "enabled" but handlers half-work after a throw | Handlers subscribed before the exception stay live; no rollback | Fallible work first, subscribe last, undo and rethrow |
| Config edits vanish, keys disappear, comments move | File is rewritten on each load from the C# class | Edit the class, not free-form notes |
| `labapi reload configs` does nothing | Cached `Config` reference | Read `Config.X` at use time |
| Data not saved after stop | Used `Disable()` | `ServerEvents.Shutdown` |
| NRE on `Config` | Empty `config.yml` | `Config ??= new T()` |

Missing `Successfully enabled` and nothing else in the log is the "total silence" case. It is almost always a folder or `properties.yml` problem, never a code bug, so check the file system before the code.

## What a loader failure does NOT stop

The server boots normally in all these cases; nothing alerts an operator. A dependency error, a failed `Enable()` and a skipped plugin each cost one log block. The only reliable alert is your own post-deploy grep, so make it part of the deploy script (exit non-zero when a plugin's success line is absent).

## Branding a build per client

Use case: the same source shipped under different identities for different servers or customers.

What the loader does with names (verified):
- The log prefix `[Name]` of every `Logger.*` call is the CALLING assembly's simple name, so `AssemblyName` is what the operator sees in the log.
- `Plugin.Name` is the config directory (`configs/<port|global>/<Name>/`). Two plugins with the same `Name` share `config.yml` and `properties.yml`.
- Nothing dedupes by plugin name or assembly. Two enabled copies both run.
- What `Assembly.Load(byte[])` does when an assembly with the same identity is already loaded is Mono-specific and UNVERIFIED. Do not rely on it: give each variant a distinct `AssemblyName` instead of testing collisions.

Pattern: drive both names from one MSBuild property.

```xml
<PropertyGroup>
  <Client Condition="'$(Client)' == ''">default</Client>
  <AssemblyName Condition="'$(Client)' != 'default'">MyPlugin.$(Client)</AssemblyName>
  <AssemblyName Condition="'$(Client)' == 'default'">MyPlugin</AssemblyName>
  <DefineConstants Condition="'$(Client)' != 'default'">$(DefineConstants);CLIENT_BRAND</DefineConstants>
</PropertyGroup>
```

```
dotnet build -c Release -p:Client=acme -o out/acme -p:SL_REFERENCES="/path/to/SCPSL_Data/Managed"
```

Give every variant its own `-o` folder: the shared `obj/` makes the next build delete the previous variant's DLL from `bin/` (measured). A full project with the client id baked into an assembly attribute: deploy-template.md.

Rules:
- Keep `RootNamespace` and type names constant; only the assembly name varies. Changing the identity of a shared library (for example SlKit) per client defeats its single clock; brand only the plugin.
- Decide on purpose whether the variants should share configs. Same `Plugin.Name` = shared config directory, different `Name` = separate directories. `Name` must stay path-safe and never change after release, or the config appears to reset.
- Never deploy two variants to the same server unless you want two running plugins; remove the old variant's DLL (and its config directory if it should not linger) when switching.
- Verify each variant by its own success line: `Successfully enabled '<Name>'`, with log prefixes showing the branded assembly name.
- Keep `RequiredApiVersion` a literal in every variant.

## Re-verifying on a new build

- Boot with a deliberately misplaced DLL (wrong port folder) and with a plugin referencing a library you leave out of `dependencies/`; confirm the silence and the `ReflectionTypeLoadException` block described above.
- Diff the log strings in the table against LabAPI's `PluginLoader.cs`, `CommandLoader.cs`, `ConfigurationLoader.cs`, `AssemblyUtils.cs` (public on GitHub).
- Build two variants with different `AssemblyName` and check both enable with their own log prefixes; if you need same-identity behaviour, test it on the real server runtime and record the result here.
- Check the loader is still the only place that decides plugin order (no new Priority semantics) and that `Plugin.Name` still names the config directory.
