# Plugin lifecycle: Enable, Disable, Shutdown, config reload

Verified against LabAPI 1.1.7 / SCP:SL 14.2.7 (2026-10). Part of scpsl-game-lifecycle.

Loader folders, build and load errors belong to deploying-scpsl-plugins; plugin structure to designing-scpsl-plugins. This file covers only the timing facts.

## Order of calls

1. LabAPI config, wrapper init, LabAPI's own commands, then every `*.dll` under `dependencies/` (all configured folders), then plugin DLLs of `global`, then of `<port>`. Plugin types are instantiated per directory, so a `global` plugin cannot depend on a DLL that exists only in `plugins/<port>`.
2. Plugins are sorted by `Priority` ascending (Highest 64, High 96, Medium 128, Low 160, Lowest 192; lower value enables first; equal priorities fall back to file enumeration order, which is not a contract). EXILED's loader uses 255, so it enables last.
3. Per plugin: `properties.yml`, version check, then inside one try/catch: `LoadConfigs()`, `RegisterCommands()`, `Enable()`, then the plugin joins the enabled set and "Successfully enabled" is logged.
4. After all plugins: the default permission provider is registered, then `ServerEvents.PluginsEnabled` fires. Once.

All of it runs synchronously on the main thread inside the first scene's `Awake`. Slow `Enable()` work delays server start.

## What follows from that

- **`Enable()` runs before any world exists.** No host, no `Player.List`, no rooms, no `Round.*`, no `NetworkServer`. Read `ConfigFile.ServerConfig` and your own config there; defer the rest to `ServerEvents.WaitingForPlayers`, which fires every round (build idempotently, drop last round's references). `PluginsEnabled` is also still before any world; use it only for cross-plugin wiring.
- **EXILED loads inside its own `Enable()`**, synchronously and last. Every LabAPI plugin's `Enable()` has run before any EXILED plugin exists, so do not touch `Exiled.API` state there; use `PluginsEnabled`.
- **Permissions:** do not query the permission manager in `Enable()`; query at command time.
- **A throw in `LoadConfigs`, `RegisterCommands` or `Enable`** aborts that plugin only: the error is logged and the next plugin loads. The plugin is not in the enabled set, but its commands stay registered and **every subscription made before the throw stays live**; `Disable()` is not called to clean up. A command constructor that throws is rethrown and `Enable()` is never reached. Do fallible work first and subscribe last; if you wrap `Enable()` in a catch to undo partial work, **rethrow**, otherwise the loader logs "Successfully enabled" and hides the failure.
- **Plugin metadata getters** (`Name`, `Priority`, `Version`, `RequiredApiVersion`) are evaluated outside any try/catch. Keep them constants that never throw, with a unique, path-safe, stable `Name` (it is the config folder). An exception escaping the loader would skip hooking scene loading, so no match would be created (derived, never triggered).
- **Do not use `Assembly.Location`** (empty for byte-loaded assemblies; `Path.GetDirectoryName("")` throws and fails `Enable()`). Use `Plugin.FilePath`, assigned after the constructor returns, so read it in `Enable()` or later.

## Disable never happens

Nothing in LabAPI, the game or EXILED's reload calls `Plugin.Disable()`. There is no unload path; assemblies are loaded from byte arrays into the default domain, so statics, Harmony patches, static event subscriptions, MEC coroutines and threads live until the process exits. Code changes need a process restart.

- **Flush in `ServerEvents.Shutdown`.** It fires once at the start of the game's quit routine, synchronously, on the calling thread, and the process then waits roughly 0.6-2 s before exiting. Do the flush inline; no deferred or async work. The process ending also ends every player visit and every static: whatever you spool must be self-contained and final (close open sessions with a terminal record, for example reason `shutdown`), because nothing in the next process will finish it. See players.md. It also fires when Unity quits itself (whether that path exists on a dedicated server is unverified). It does **not** fire on SIGKILL or a crash; whether SIGTERM reaches it is unverified. Also flush on a timer.
- **`Enable()` can run twice.** A LabAPI plugin placed in EXILED's own plugin folder is enabled by EXILED, and EXILED's reload clears its list and enables again with no `Disable()` in between. A second `Enable()` then leaks the first one's subscriptions and coroutines. Make `Enable()` idempotent (guard flag, or unsubscribe-then-subscribe).
- scpsl-lint rule SL015 flags cleanup that lives only in `Disable()`.

```csharp
bool _enabled;

public override void Enable()
{
    if (_enabled) return;                       // idempotent against a second Enable
    Config ??= new MyConfig();                  // an empty config.yml deserialises to null
    // 1. fallible work first
    _http = BuildClient();
    // 2. subscribe LAST so a throw above leaves nothing live
    ServerEvents.WaitingForPlayers += OnWaitingForPlayers;
    ServerEvents.Shutdown += OnShutdown;        // flush here, synchronously
    _enabled = true;
}

public override void Disable() { }              // never called; do not put the only flush here
```

## Config object lifecycle

- `Plugin<TConfig>.LoadConfigs()` runs before `Enable()`, and again on every `labapi reload configs`, for every loaded plugin including ones that never enabled. It **replaces** the `Config` object. A cached reference or a value derived from it goes stale and reload silently does nothing. Read `Config.X` at use time, or override `LoadConfigs()` (call `base`), then rebuild caches. Do not touch state that `Enable()` creates from an override.
- An empty or comment-only `config.yml` deserialises to null, so `Config` is null. Guard with `Config ??= new TConfig()`.
- Every load rewrites the file: comments regenerated, unknown keys dropped, new defaults added. A bad value makes the whole file fall back to defaults with a warning labelled with LabAPI, not your plugin. The reload command does not re-run `Enable()`, does not raise an event, and does not cover LabAPI plugins that EXILED loaded itself.
- `RequiredApiVersion`: `new Version(LabApiProperties.CompiledVersion)` is vacuous, because `CompiledVersion` is computed at runtime from the running `LabApi.dll`. Use a literal such as `new Version(1, 1, 7)`.

## Event subscriptions across the lifecycle

- Subscribe as the last step of `Enable()`. Static events hold their targets strongly, so anything shorter-lived than the server (per round, per player) must unsubscribe when it dies, or route from one static handler by id.
- Register a `CustomEventsHandler` instance once; a second registration of the same instance throws midway and leaves one stale hook. Style B registration reflects over every event, so do it once at enable, not per round.
- Handler order is subscription order. Handlers added later (lazily, per round) go to the end regardless of plugin priority.
- A throwing handler is caught per subscriber, but LabAPI's log header names the last subscriber, not the thrower; wrap your own bodies in try/catch with your own logging. See labapi-events.

## Re-verifying on a new build

- Re-read `PluginLoader` `Initialize`, `EnablePlugins`, `EnablePlugin`: order, try/catch scope, when `LoadConfigs` and `Enable` run, and what happens before `PluginsEnabled`.
- Grep LabAPI and the game for callers of `Disable`; grep EXILED's loader reload for `Disable`/`Enable`.
- Check `LabApiProperties.CompiledVersion` is still computed at runtime.
- Check where `ServerEvents.OnShutdown()` is called from and how long the quit routine waits.
- Probe: a plugin that logs from the constructor, `LoadConfigs`, `Enable`, `PluginsEnabled`, `WaitingForPlayers`, and `Shutdown`; run `labapi reload configs`; stop the server with the normal stop command and with SIGTERM and see which logs appear.
