# Symptom, causes, checks

Verified against LabAPI 1.1.7 / SCP:SL 14.2.7 (2026-10). Each row: what you see, the causes in order of likelihood, the cheapest check (read-only first), and the skill that holds the fix. `sldoctor` is the DebugDoctor example (where-to-look.md). Items marked UNVERIFIED are inferred, not observed.

## A. The plugin is not loaded

| What you see | Causes | Check | Fix lives in |
|---|---|---|---|
| No log line for the plugin, no error | 1. DLL in the wrong folder: not under a `plugin_paths` entry, wrong port number, typo in `global`. 2. `is_enabled: false` in `properties.yml` (logged nowhere). 3. The DLL has no non-abstract `Plugin` subclass. 4. The DLL sits in `dependencies/`, where it is registered but never instantiated | List `plugins/` and `LabApi-<port>.yml`; open `configs/<port>/<Name>/properties.yml`; `sldoctor plugins` (lists LabAPI-loaded plugins only) | deploying-scpsl-plugins |
| `Couldn't load the plugin inside '<path>'` + `ReflectionTypeLoadException` | A type signature references an assembly that is not loaded: a missing dependency | Read the exception text; the "Missing dependencies" list has false positives | Put the library in `dependencies/`, restart |
| Same, but only on some ports | A `global` plugin needs a dependency that exists only in `dependencies/<other port>` (for example, a global plugin needing Harmony that lives only in one port's folder fails on the other port) | Compare `dependencies/global` with `dependencies/<port>` | Shared libraries in `dependencies/global` |
| `Couldn't enable the plugin` + exception | `Enable()`, `LoadConfigs()` or a command constructor threw. Commands stay registered, handlers subscribed before the throw stay live, `Disable()` is not called | The exception under the line. `ArgumentException: Invalid path` = `Assembly.Location` on a byte-loaded assembly (SL013) | deploying-scpsl-plugins |
| `Couldn't enable` with text about versions, no exception | `RequiredApiVersion` has a different major than the server and `unsupported_loading` is off | Compare to the LabAPI version line. A newer minor only warns | Literal `new Version(1, 1, 7)`; never `CompiledVersion` (it can never fail the check) |
| `Successfully enabled` but the feature does nothing | `Enable()` swallowed an exception and returned; `Config` is null (empty `config.yml`) or an in-plugin enable flag is false; EXILED state touched too early | Read `Enable()` for `catch`; `sldoctor subs` the events you rely on | deploying-scpsl-plugins; do EXILED wiring in `PluginsEnabled` |
| Events fire twice, commands duplicated | Two copies of the DLL (backup named `.dll`, a copy in both `global` and `<port>`); or the same LabAPI plugin also sits in EXILED's own `Plugins` folder, where EXILED loads it a second time (inferred from the loader code, not observed) | `sldoctor plugins` shows DUPLICATE NAME for LabAPI copies; EXILED-loaded copies are not in that list, so look in the EXILED folder | One copy; backups as `.bak` |
| Code change has no effect | LabAPI loads DLLs from bytes and never unloads them: replacing the file changes nothing until the game process restarts; or the new DLL went to a folder the port does not read; or an old copy loads too | The `Successfully enabled` line carries `Version:`; compare with the build. Check file dates and every plugin folder | Restart the process; one copy |
| `MissingMethodException`, `TypeLoadException` or `FileNotFoundException` at first use, not at boot | A reference needed only inside a method body loads fine and fails when first called. Inside an event handler it is caught per handler and logged as an event exception. Usual cause: compiled against a different game, LabAPI or library build | The exception names the member and assembly; compare with the installed `LabApi.dll` and `dependencies/` | Rebuild against the installed binaries; ship the library |
| Command unknown, "does nothing", no output | Duplicate command name skipped with no log; a built-in alias shadows it; `Unable to register command` (alias clash); empty `response`; someone ran `refreshcommands`, which wipes every plugin command of that handler (RA or console) until restart | grep `Unable to register`; try the name with a prefix | scpsl-commands-and-permissions |

## B. The handler never runs

| What you see | Causes | Check | Fix lives in |
|---|---|---|---|
| Nothing happens, `sldoctor count` says 0 raises | 1. The event is declared but never raised by the game (`PlayerEvents.SearchedAmmo`, `SearchedArmor`, `ServerEvents.CassieAnnouncing`, `CassieAnnounced`); `PlayerEvents.AimingWeapon` does not exist at all. 2. Wrong event of a pair. 3. The action did not happen in the window | The catalog row for the event (`never-fired`); scpsl-lint SL002 | labapi-events (events-catalog.md) |
| `-ing` runs, `-ed` does not (or reverse) | `-ing`/`-ed` are not 1:1: `InteractedDoor` fires on denial too (`CanOpen` false, and for a locked door without any `InteractingDoor`) but not after an `InteractingDoor` you cancelled on an unlocked door; `Hurt` fires even when the damage was `Nothing`; `Muting`/`Muted` are raised only by the mute commands; `Joined` and `Left` are per hub, not per connection | Catalog `pair` and notes | labapi-events |
| `sldoctor subs <event>` does not list you | Never subscribed: `Enable` returned early (null config), threw before the `+=`, or a `CustomEventsHandler` was never passed to `CustomHandlersManager.RegisterEventsHandler`; or unsubscribed by a lambda you lost | `sldoctor subs`; read `Enable` top to bottom | designing-scpsl-plugins |
| You are listed, raised, but the effect is missing | 1. Another plugin cancelled: `IsAllowed` does not short-circuit, so every handler still runs; the last writer wins. 2. A setter the game ignores (catalog `ignoredSetters`, scpsl-lint SL003). 3. A Harmony prefix elsewhere returned false: the original body, including the event raise, was skipped (then count shows 0) | `ev.IsAllowed` at the start of your handler; `sldoctor count` reports raises already cancelled; scpsl-harmony-patching `GetPatchInfo` on the target | labapi-events; scpsl-harmony-patching |
| Handler throws immediately in pre-auth events | `PreAuthenticating` and `PreAuthenticated` run on the connection thread, not the main thread: no `Player`, no Unity, no MEC there | `sldoctor count` reports off-main-thread raises | scpsl-main-thread-and-timing |
| It ran once at boot, never again | Subscribed before the world exists, or state cleared at `WaitingForPlayers` without re-subscribing | Where do you subscribe, and where do you clear? | scpsl-game-lifecycle |
| Mutation inside the handler "didn't stick" | Re-entrancy (`SetRole` inside `ChangingRole`); reassigning `ev.DamageHandler`; an `await` before the decision (handler already returned) | Read the handler for `await` and for game calls on the same action | labapi-events |

## C. Runs too often, TPS drops, frames hitch

| What you see | Causes | Check | Fix lives in |
|---|---|---|---|
| Lag after adding a handler | Hot event (per frame, per pair, per packet): voice, visibility, radio, hazards, tesla, SCP-173 observers | `HotEventAudit.Log()`; `sldoctor subs` | labapi-events |
| Frame hitches every N seconds | Periodic job doing O(players) work in one frame; synchronous file or HTTP on the main thread; `GC.Collect`; a coroutine per player | scpsl-lint SL005, SL007, SL010, SL011; per-job cost in `SlClock.Stats` | scpsl-main-thread-and-timing |
| "TPS is 58 but players rubber-band" | `Server.Tps` is smoothed and clamped; it hides hitches, and shows 296 in idle mode | Measure frame period and busy time, not `Server.Tps` | measuring-scpsl-performance |
| Whole server slow only when your plugin runs | `FirstChanceException` hook; `Logger` calls in a loop or hot handler; per-frame allocations | scpsl-lint SL008, SL009, SL010; allocations have no rule, read the handler | scpsl-engine-cost-model |
| A handler runs twice per action | Registered twice (two copies, or `CustomEventsHandler` instance registered twice, or `+=` in a per-round method) | `sldoctor subs` | labapi-events |

## D. Exception spam

| What you see | Causes | Check | Fix lives in |
|---|---|---|---|
| Thousands of identical stack traces, CPU up | A persistent fault inside a hot handler; each catch logs once per raise, at stack-walk-plus-regex cost per line | Count lines per second in the log; first occurrence | Fix the cause, then rate-limit the catch (`RateLimitedLog`) |
| The error header names another plugin | LabAPI names the last-registered handler of the delegate | Read the stack trace | labapi-events |
| `MissingReferenceException`, `NullReferenceException` on a player | Stale `Player`/hub from a previous round or a leaver | Section E | scpsl-game-lifecycle |

## E. Broken after a round restart

| What you see | Causes | Check | Fix lives in |
|---|---|---|---|
| Errors or ghost players only from round 2 | A cached `Player`, `ReferenceHub`, `Room`, `Door` or `Pickup` from the previous round (the scene is rebuilt) | Grep your statics and fields for those types | scpsl-game-lifecycle |
| Work keeps running between rounds, touching dead objects | MEC coroutines survive the restart (`Timing.Instance` persists); statics and static subscriptions survive | `Timing.IsRunning` on your handles; the job list of your clock | scpsl-main-thread-and-timing |
| Handlers double up each round | `+=` inside `WaitingForPlayers` or `Joined` without a matching `-=` | `sldoctor subs` after round 1 and after round 2 | scpsl-game-lifecycle |
| Per-player state attached to the wrong person | `PlayerId` is recycled; `UserId` of offline players is not an identity; dummies share `ID_Dummy` | How is the cache keyed? | scpsl-game-lifecycle |
| Lobby or round will not end or start | A lock you set survives restarts (`Round.IsLobbyLocked`, `Round.IsLocked`) | Who sets and who clears it | scpsl-game-lifecycle |
| `RoundRestarted` logic runs twice | The event can fire twice | Make it idempotent | scpsl-game-lifecycle |
| Round state not reset after `softrestart` | `softrestart` changes level without raising `RoundRestarted`; only `RoundRestart.OnRestartTriggered` sees it | Wire both, or reset in `WaitingForPlayers` | scpsl-game-lifecycle |

## F. Hints flicker, vanish or overwrite

The client has one hint slot per player: the newest hint replaces the current one, and an identical resend every 0.5 s does not flicker (verified, client, SCP:SL 14.2.7; scpsl-hints-and-hud). So a flicker or an alternating HUD means two different texts are being sent, not the resend itself. Ask what the player sees (screenshot).

| What you see | Causes | Check | Fix lives in |
|---|---|---|---|
| HUD flickers or alternates | Two plugins send hints to the same player; the server arbitrates nothing | Which other plugins call `SendHint`? Ask the owner for the plugin list; test with the other plugin disabled | scpsl-hints-and-hud |
| HUD vanishes after a pickup warning or an event | Vanilla sends its own hints (inventory full, pickups); your hint was sent once with a long duration and never refreshed | Add a keep-alive resend under a 3 s duration | scpsl-hints-and-hud |
| HUD gone for a moment after escape or role change | The game suppresses a player's hints while it respawns pickups | Re-send after the window | scpsl-hints-and-hud |
| Spike at round start or on spawn | An "info" plugin refreshes every player's hint on each spawn; at round start every player spawns, so N players send N x N hints | `sldoctor count PlayerEvents.ChangedRole`; who handles it (`subs`) | scpsl-hints-and-hud |
| `Cannot display a hint to the local player` | `Player.List` includes the host | Iterate `Player.ReadyList`, skip dummies | scpsl-hints-and-hud |
| Broadcast of another plugin disappeared | `ClearBroadcasts` clears everything, vanilla too | Search for the call | scpsl-hints-and-hud |

## G. Harmony conflicts

| What you see | Causes | Check | Fix lives in |
|---|---|---|---|
| A patch silently does nothing | A caller inlined a tiny target before patching; the signature was remembered, not resolved; another prefix returned false | `Harmony.GetPatchInfo(target)`: owners, priority, index; hit counter in your patch | scpsl-harmony-patching (troubleshooting.md) |
| Patches vanish after a reload | UCR, UCI, ProjectMER or Exiled.CustomItems call `UnpatchAll(null)`, which removes every owner's patches (if the two Harmony copies share one registry: UNVERIFIED) | Probe `GetPatchInfo` at `WaitingForPlayers` | scpsl-harmony-patching |
| `InvalidProgramException` in position or voice code | A transpiler broke IL that EXILED's transpilers also anchor on | Fingerprint the pristine IL; run with and without EXILED | scpsl-harmony-patching |
| Works without EXILED, breaks with it | Testing only in the clean configuration | Reproduce with the owner's full plugin set | scpsl-harmony-patching |

## H. Config not applied

| What you see | Causes | Check | Fix lives in |
|---|---|---|---|
| Edits disappear, keys vanish, comments move | The file is rewritten on every load from the C# class; unknown keys are deleted | Open the file after a boot | deploying-scpsl-plugins |
| All settings are defaults | One bad value (enum, `is_enabled: maybe`) discards the whole file; the warning `Failed to load the configuration file, using default values.` carries `[LabApi]`, not your name; the broken file is left as it was | grep that string; the file still holds your bad value | deploying-scpsl-plugins |
| NullReferenceException on `Config` | Empty or comment-only `config.yml` deserialises to null | `Config ??= new T()` | deploying-scpsl-plugins |
| `labapi reload configs` does nothing | A cached `Config` object: `LoadConfigs` replaces it | Read `Config.X` at use time | deploying-scpsl-plugins |
| A renamed property lost its value | The key is the snake_case of the property; the old key is orphaned and deleted | Diff the file | Migrate in code |
| A setting is missing from the file, or edits to it are ignored | A public field is neither written nor read; a get-only property is written but never read back | Declare `{ get; set; }` properties | deploying-scpsl-plugins |
| A list "ignores" my default entries | A list in the file replaces the default contents | Read the file | deploying-scpsl-plugins |
| Edited the wrong file | Config is per port (`configs/<port>/<Name>/`); `Name` renamed or shared by two plugins; EXILED plugins have their own files | Which folder did the server read? | deploying-scpsl-plugins |

## I. Works on one port, not on another

| Causes | Check |
|---|---|
| Different plugin sets per port (a Harmony conflict or a hint overwrite exists only where the other plugin is installed) | `sldoctor plugins` on both |
| `global` versus `<port>` folders: a plugin or dependency present in one only | Diff the folder trees |
| Per-port config and server config (`config/<port>/config_gameplay.txt`, `configs/<port>/<Name>/`) | Diff the two |
| `LabApi-<port>.yml` has different `plugin_paths` | Read both files |
| Different game or LabAPI builds per install | Compare the version lines in the two boot logs |
| Player count and idle mode: a quiet test port is in idle mode (about 1 frame per second) | `Server.IdleModeActive`; run with dummies |

## Re-verifying on a new build

- Re-run the live failure cases: a plugin in the wrong port folder, a plugin with a missing dependency, a plugin that throws in `Enable`, `is_enabled: false`, a bad config value; confirm each row of section A and H.
- Replace a plugin DLL while the server runs and confirm the old `Version:` still loads until a process restart; check that `refreshcommands` still wipes plugin commands.
- Re-check the catalog rows cited in section B against the current events-catalog.md.
- Re-test one `UnpatchAll(null)` scenario for section G.
