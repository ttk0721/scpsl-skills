# Loader, lifecycle and config

Verified against LabAPI 1.1.7 / SCP:SL 14.2.7 (2026-10). LabAPI source is public (northwood-studios/LabAPI); file names below refer to it.

## Boot position

The whole loader (`PluginLoader.Initialize`) runs synchronously on the Unity main thread inside the server's first `Awake`. Plugin `Enable()` therefore runs before the match, the network host, the Facility scene and `WaitingForPlayers`.

| Available in `Enable()` | Not available yet |
|---|---|
| `ConfigFile.ServerConfig`, `Server.Port`, LabAPI config, command registration, Harmony patching, static event subscription | `Player.List` (empty), `Server.Host` (null), `NetworkServer.active`, rooms, doors, `Round.*`, `PermissionsManager` default provider |

- `ServerEvents.PluginsEnabled` fires once, after every plugin (including EXILED's) is enabled, but is still before any world exists. Use it for cross-plugin wiring.
- `ServerEvents.WaitingForPlayers` fires at the start of every round (the scene reloads), so world-dependent setup goes there and must be idempotent and drop last round's references. See scpsl-game-lifecycle.
- Slow work in `Enable()` (HTTP, DB connects, big Harmony sets) delays server start directly.
- An exception that escapes the loader itself (not the per-plugin catch) is not caught by the game; from the code it would prevent the match from being created. UNVERIFIED in practice. The practical rule: metadata getters (`Name`, `Priority`, `Version`, `RequiredApiVersion`, `IsTransparent`) are constants that never throw.

Loader order: LabAPI config, wrapper init, LabAPI's own commands, all dependencies, plugins (load and enable), transparent-flag resolution, default permissions provider, `PluginsEnabled`.

## Folder layout

Root is the platform application-data folder (`~/.config` on Linux). On Windows it is the standard .NET `ApplicationData` folder (inferred, not tested). A `hoster_policy.txt` in the working directory with `gamedir_for_configs: true` moves it to a relative `AppData` folder.

```
<root>/SCP Secret Laboratory/LabAPI/
  LabApi-<port>.yml          loader config, rewritten at every boot
  plugins/{global,<port>}/   plugin DLLs
  dependencies/{global,<port>}/   libraries
  configs/{global,<port>}/<Plugin.Name>/config.yml, properties.yml
```

- `LabApi-<port>.yml` defaults: `plugin_paths: [global, $port]`, `dependency_paths: [global, $port]`, `load_unsupported_plugins: false`. `$port` is a plain string replace.
- `global` serves every server instance on the machine; `<port>` only that port.
- Every `*.dll` under each configured directory is loaded, recursively. This includes `old/`, `backup/` and files named `Foo.dll` that are really copies. Nothing dedupes by name or assembly, so two DLLs with a plugin type give two enabled plugins (double events, rejected duplicate commands). Name backups `.bak`.
- Dependencies are loaded by `Assembly.Load(bytes)` and only registered; no code runs and their own references are not checked.
- For plugins, each configured directory runs three passes: load all DLLs (plus `.pdb` if present beside the DLL), extract embedded `.dll` / `.dll.compressed` resources (loaded only if no assembly with that simple name exists), then `GetTypes()` and instantiate every non-abstract `Plugin` subclass. Consequence: `global` plugin types are instantiated before `<port>` DLLs are loaded, so a `global` plugin cannot depend on a DLL that exists only in `plugins/<port>`. Put shared libraries in `dependencies/`.
- Directory enumeration is not sorted by the BCL. Do not rely on any particular order, alphabetical included.

## Load order and Priority

| Priority | Value |
|---|---|
| Highest | 64 |
| High | 96 |
| Medium (default) | 128 |
| Low | 160 |
| Lowest | 192 |

Lower byte enables first. Custom values outside 64-192 are allowed (`(LoadPriority)255` is what EXILED's loader uses, so it enables last). Sorting is a stable LINQ `OrderBy`; ties keep file-enumeration order, which is an implementation detail. A provider plugin takes Highest/High, its consumer Low/Lowest.

Priority only orders `Enable()`. By then every plugin assembly is loaded and every plugin constructor has run.

EXILED is an ordinary LabAPI plugin (`Exiled.Loader.dll` in `plugins/global`, `Exiled.API.dll` in `dependencies/global`). It loads and enables all EXILED plugins synchronously inside its own `Enable()`. So every LabAPI plugin's `Enable()` ran before any EXILED state exists: do not touch `Exiled.API` from `Enable()`; use `PluginsEnabled`. Its Harmony patches are applied after all LabAPI plugins. See porting-exiled-to-labapi.

## Enable gating, per plugin

1. `properties.yml` is loaded or created (`is_enabled: true`, `unsupported_loading: Default`). Unreadable: plugin skipped, a config-loader error is the only trace.
2. `is_enabled: false`: skipped with NO log line.
3. Version check (`RequiredApiVersion`): null is accepted; same major accepted (a newer minor/patch than the server only logs a warning); different major loads only with `unsupported_loading: True` (or Default plus `load_unsupported_plugins: true`), else an error that also starts with `Couldn't enable the plugin` is logged and the plugin is skipped.
4. `LoadConfigs()`, then command registration, then `Enable()`, inside one try/catch. A throw logs `Couldn't enable the plugin` and the loop continues.

Notes:
- `RequiredApiVersion`: `LabApiProperties.CompiledVersion` and `CurrentVersion` are static readonly values computed at runtime from the RUNNING `LabApi.dll`. A literal `new Version(1, 1, 7)` is the only form that can ever warn or refuse. Pick the minimum LabAPI you actually need.
- The "modded server" flag is set before anything can fail, so a plugin that failed to enable still marks the server modded.
- Command registration scans every type in your assembly for `[CommandHandler]` and constructs it before `Enable()`. A throwing command constructor aborts that plugin's enable; commands do not roll back if `Enable()` later throws. A command must not depend on state created in `Enable()`.
- If `Enable()` throws midway: commands stay registered, handlers subscribed before the throw stay live, `Disable()` is not called. Undo your own subscriptions in a catch and rethrow (a swallowed exception makes the loader log "Successfully enabled" and hides the failure).

## Disable() and shutdown

Nothing calls `Disable()`: no caller in LabAPI or the game, and EXILED's reload does not call it for LabAPI plugins either. No unload path exists (assemblies come from bytes into the default AppDomain). Statics, Harmony patches, event subscriptions, MEC coroutines and threads live until the process exits. Deploy = replace the DLL and restart the process.

`ServerEvents.Shutdown` is the only notification. It runs synchronously on the caller's thread at the start of the quit sequence, then the game waits roughly 0.6 to 2 seconds and exits. Flush synchronously inside it. It may also fire when Unity itself quits (a SIGTERM path is UNVERIFIED); SIGKILL and crashes fire nothing, so also flush on a timer.

If a LabAPI plugin DLL also sits in EXILED's own plugin folder, an EXILED reload can call `Enable()` again with no `Disable()` between. `Enable()` must be idempotent.

## Config rules

Library: YamlDotNet shipped with the game, `UnderscoredNamingConvention`.

| Behaviour | Consequence |
|---|---|
| Key = snake_case of the property (`MaxPlayers` -> `max_players`) | Renaming a C# property orphans the old key; migrate in code |
| Only properties with a public setter are read; get-only properties are written but never read; `{ get; private set; }` is written AND read | Use `{ get; set; }` |
| Public fields are neither written nor read | Properties only |
| `[System.ComponentModel.Description("...")]` becomes a `# comment` line | Comments are regenerated each save |
| Lists, arrays, sets and dictionaries in the file REPLACE the default contents | A file with `[a]` beats default `[a, b]` |
| Enum values match the member name, case-insensitive (`fast` ok, `slow_mode` for `SlowMode` throws) | A single bad value (enum, `is_enabled: maybe`) discards the WHOLE file |
| Object is created with `new T()`; missing keys keep their initializer defaults | Put defaults in initializers; no required ctor args |
| Every successful load rewrites the file: unknown keys deleted, new defaults added | Never keep hand-written notes in config YAML; keep a copy before upgrading |
| Failed read: the file is left untouched and an in-memory default is used; the warning `Failed to load the configuration file, using default values.` carries a `[LabApi]` prefix, not your plugin name | grep for it after every config edit |
| An empty or comment-only `config.yml` deserialises to null; `Config` is null | `Config ??= new MyConfig()` |
| `Vector4` writer emits `w` from `z` (1.1.7 bug) | Never put `Vector4` in a config; use `Vector3`, `Color` (`{r,g,b,a}`), `Color32` (hex string) or a four-float DTO. scpsl-lint rule SL014 flags it |
| `Plugin.Name` is the config folder; per-port by default, the helpers' `isGlobal: true` parameter stores under `configs/global` | Renaming `Name` "resets" the config; two plugins with one `Name` share `config.yml` |
| No translation system in LabAPI | Use a second file: `this.LoadConfig<Other>("other.yml")` |

`labapi reload configs` (needs the server-config permission) calls `LoadConfigs()` on every plugin in the loader's list (enabled or not, but not LabAPI plugins that EXILED loaded itself). It does not re-run `Enable()`, re-read `properties.yml` or raise an event. `Plugin<TConfig>.LoadConfigs` REPLACES the `Config` object: a cached reference or value derived from it is stale. Read `Config.X` at use time, or override `LoadConfigs()`, call `base`, and rebuild caches there. `LoadConfigs()` also runs once before `Enable()` and for never-enabled plugins, so an override must not touch state that `Enable()` creates.

Do config I/O from the main thread: Vector/Color converters use non-thread-safe Unity pools, and concurrent writes to one file race anyway.

## Logging from the loader's view

`Logger.Debug(object, bool canBePrinted = true)` has no level gate. Each call walks the stack for the calling assembly name and runs regexes in the console formatter (per-call cost not measured). Guard with your own flag before building the string and never log per frame. The `[prefix]` is the CALLING assembly name, which is why AssemblyName matters for branding (verify-and-brand.md). See scpsl-engine-cost-model.

## Re-verifying on a new build

- Read LabAPI's `PluginLoader.cs`, `ConfigurationLoader.cs`, `Plugin.cs`, `Constants/Priority.cs`, `LabApiProperties.cs` on GitHub; diff the load order, the priority numbers, the `ValidateVersion` branches and whether `CompiledVersion` is still computed at runtime.
- Check `CustomVectorConverter` still writes `w` from `z`.
- Boot a server with a stub plugin that throws in `Enable()` and a second with `is_enabled: false`; confirm the log lines in verify-and-brand.md.
- grep the LabAPI tree for callers of `Disable()` and `Unload`; confirm there is still none.
- Re-run a scratch YAML round trip (empty file, bad enum, list replacement, private setter) against the shipped `YamlDotNet.dll`.
- Test whether `ServerEvents.Shutdown` fires on SIGTERM (still unverified).
