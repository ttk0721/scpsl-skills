# Command system reference

Verified against LabAPI 1.1.7 / SCP:SL 14.2.7 (2026-10). Part of scpsl-commands-and-permissions.

## The three entry points

| | RA (`/cmd` in the RA panel) | Server console / local console | Client console (`.cmd`) |
|---|---|---|---|
| LabAPI handler type | `RemoteAdminCommandHandler` | `GameConsoleCommandHandler` | `ClientCommandHandler` |
| LabAPI `CommandType` | `RemoteAdmin` | `Console` | `Client` |
| Sender | `PlayerCommandSender` (or `ServerConsoleSender` when `/cmd` is typed in the server console) | `ServerConsoleSender` | `PlayerCommandSender` |
| Gate before dispatch | player must have RA access; shared rate limit | none (trusted operator) | shared rate limit ONLY |
| Reply | `out response`, shown as `NAME#text`; empty = nothing shown | `out response` printed; whitespace = nothing | `out response` to the player's game console |
| Exception | stack trace to the admin | stack trace to the console | full `ex.ToString()` to the PLAYER |

- Prefix routing in the server console: `.x` is forwarded to the server as a dot command through the local host hub; `/x` runs the RA handler with the console as sender; `@x` is admin chat; `!x` is a central-server command on its own thread; anything else goes to the console handler. So a console-only command is NOT reachable as `/name`, and an RA-only command is NOT reachable as a bare name.
- RA and dot commands share one sender object per connection, so inside `Execute` you cannot tell them apart. Use separate classes per handler.
- A `.cmd` typed in the server console arrives as the local host hub's `PlayerCommandSender`, which is not full-permission. A permission-gated dot command may refuse the operator's own console (UNVERIFIED: the host hub's permission value was not traced).
- The RA panel also speaks a `$`-prefixed side protocol (player list, player info, auth, ban, status). It is not a command: `CommandExecuting/Executed` are NOT raised for it.

## Object model

- `ICommand`: `Command`, `Aliases`, `Description`, `Execute(ArraySegment<string>, ICommandSender, out string)`. `ICommandSender`: `LogName`, `Respond(message, success)`.
- Optional: `IUsageProvider.Usage` (RA autocomplete hints), `IHiddenCommand` (hidden from `help` and the RA list), `IHelpProvider`.
- The handler keeps two case-insensitive dictionaries (names, aliases). Registering a name that exists throws; a duplicate alias throws AFTER the name was added. `TryGetCommand` checks aliases first, then names.
- `ParentCommand` is itself a handler: it takes the first argument as the sub-command name and passes the remaining segment down; with no argument or an unknown sub-command it calls `ExecuteParent`. The underlying array is shared, so `arguments.Array[0]` is always the top-level command word.
- `CommandHandlerAttribute` does nothing in the game. Only LabAPI's loader gives it meaning.

## Registration (LabAPI `CommandLoader`)

- Order: LabAPI's own commands, then each plugin: load configs, register commands, `Enable()`. An exception in registration aborts enabling that plugin.
- The loader reflects over `assembly.GetTypes()`. One unloadable type throws `ReflectionTypeLoadException` and the plugin loses ALL its commands. It needs a public parameterless constructor (abstract or ctor-with-args throws).
- One class with N `[CommandHandler]` attributes gives N instances. Instance fields are not shared; use `static`. Keep constructors side-effect free: name-collision skips and parents registered early through a child leave extra throw-away instances.
- A `ParentCommand` used as a handler type must carry its own `[CommandHandler(typeof(RemoteAdminCommandHandler | ...))]` attribute, otherwise its children log "CommandHandler ... not found" and are skipped.
- Duplicate guard compares primary names only and skips silently (`return true`, no log). Colliding alias: throws mid-registration, leaves the command half-registered (name works, aliases do not) and skips the remaining handlers.
- Check at startup that you really own the name: `Server.RemoteAdminCommandHandler.TryGetCommand(name, out var c)` and compare `c.GetType().Assembly`.
- Manual registration: `Server.RemoteAdminCommandHandler`, `Server.ClientCommandHandler`, `Server.GameConsoleCommandHandler`, and public `CommandLoader.TryRegisterCommand(ICommand, CommandHandler, logName)`.
- LabAPI never calls `UnregisterCommands` and never calls `Plugin.Disable()`. Call `CommandLoader.UnregisterCommands(this)` yourself if you reload. Re-running `RegisterCommands()` without it registers then throws on a duplicate key.
- `refreshcommands` (needs `ServerConsoleCommands`) clears and reloads the built-ins of the RA or console handler only. All plugin commands there vanish until the plugin is re-registered; dot commands survive. Restrict the permission, or re-register from a `CommandExecuted` handler.
- Built-in LabAPI command: `labapi` (alias `lab`) in RA and console, children `permissions` and `reload configs`. Collisions with it are silent too.
- `CommandExtensions.ListSubCommands` joins each child's `Aliases`: a null alias array throws.
- Command list sync: the table is computed once at host-hub start and pushed once per RA hub. Commands registered in `Enable()` are in; later ones execute but do not autocomplete (inference, UNVERIFIED whether the host hub is recreated per round). Descriptions are cut at about 80 chars.

## Arguments

- `Execute` gets `ArraySegment<string>` WITHOUT the command word. Plugins compile against net48 where the segment has no indexer (the runtime mscorlib does, so this is compile-time only).
- Helpers (global namespace, game `CollectionExtensions`): `At(i)`, `FirstElement()`, `Segment(offset[, len])`, `IsEmpty()`. `At` is bounds-checked against the underlying array, not `Count`: `At(3)` on a 2-element segment returns the next token of the array or throws.
- Splitting: single space char, at most 512 tokens including the command word, empty tokens dropped, the last token holds the unsplit rest, tabs are not separators, no quote handling, arguments case-sensitive, command names case-insensitive.
- Player selection: `Utils.RAUtils.ProcessPlayerIdOrNamesList(arguments, startIndex, out string[] remaining)` accepts id lists (`1.2.3`), names and quoted names, returns `null` on error and does not throw; exact nickname match. `RAUtils.FormatArguments(args, index)` joins a tail.
- Parse numbers with `CultureInfo.InvariantCulture`.

## Responses

- Success flag colours the line. Rich text is allowed; unclosed tags are auto-closed.
- RA and console: empty or null response prints nothing. A cancelled `CommandExecuting` is also silent for RA/console (dot says "forcefully cancelled by a plugin"), so reply yourself.
- Dot: the dispatcher reads only `out response` (an empty one still goes out as a blank line). `sender.Respond`/`Print` on a `PlayerCommandSender` go over the RA channel, not the game console. Extra lines: `player.SendConsoleMessage(text, "red|cyan|blue|magenta|white|green|yellow|black|<else grey>")`.
- Each reply is one encrypted reliable message. Send ONE message, not many `Respond` lines.
- Empty query (client sends `.` plus space) throws outside the command try/catch and costs one logged error, not a crash.

## Pipeline and events

RA pipeline: trim and split, find command, raise `ServerEvents.CommandExecuting` (cancel = silent), handlers may replace `Sender`, `Command`, `Arguments`, run `Execute` in try/catch, close rich-text tags, raise `CommandExecuted` (handlers may rewrite `Response`/`ExecutedSuccessfully`), reply if non-empty. Console is the same shape. The dot path differs: `CommandExecuted` is raised after the reply was already sent, so rewriting `Response`/`ExecutedSuccessfully` there has no effect (see the event catalog in labapi-events).

- If the command was NOT found, setting `e.Command` does not make it valid (the found flag is stale). Register the real command; use the event only to cancel or rewrite existing ones.
- `CommandName` is `Arguments.Array?[0]`, wrong if a handler swapped in a segment of another array.
- On the dot path `CommandExecuted` fires in a `finally`, with a possibly null sender.
- `CommandExecuted` is raised only for commands that were found. Events are on-request frequency, not hot (see labapi-events).

## Running and sandboxing commands from code

- `Server.RunCommand(string, CommandSender? sender = null)`: null sender = FULL permissions; bare names go to the console handler, `"/name"` to RA, a leading `!` spawns a central-server command thread.
- Passing the player's own sender is NOT a sandbox for untrusted text: `!` is handled before the sender is looked at and `.` is forwarded through the local host hub. Whitelist the command word yourself, or call your own code instead.
- `executeas` narrows only game flags. LabAPI string permissions resolve through the substituted sender, so they are those of the TARGET player (inferred, not tested).

## Kick and ban from commands

- `BanPlayer.KickUser(target, sender, reason)` fires LabAPI `Kicking/Kicked` and tolerates a console sender. `BanPlayer.BanUser(target, sender, reason, long durationSeconds)` has a mandatory duration in SECONDS; duration 0 on a live hub degrades to a kick; a target with `BypassStaff` makes it return false with no event.
- Neither checks permissions or kick power. Check `PlayerPermissions` and the target's required kick power against `sender.KickPower` first.
- LabAPI wrappers `Player.Kick/Ban` and `Server.KickPlayer/BanPlayer` call the same methods. Do not call the console disconnect directly.
- Audit-log admin-impacting commands with the game's `ServerLogs.AddLog(ServerLogs.Modules.Administrative, $"{sender.LogName} ...", ServerLogs.ServerLogType.RemoteAdminActivity_GameChanging)`.

## Re-verifying on a new build

- LabAPI: read `CommandLoader` (duplicate guard still name-only and silent? alias handling? `UnregisterCommands` still uncalled?), `PluginLoader` (order: commands, then `Enable()`), `CommandExtensions.ListSubCommands`, `Server` handler properties.
- Inspect the game's `CommandSystem.Core` assembly (with a decompiler on your own install; never commit the output): `CommandHandler.RegisterCommand/TryGetCommand` (alias-first), `ParentCommand.Execute`.
- Game: `RemoteAdminCommandHandler`/`GameConsoleCommandHandler` for `refreshcommands`; `QueryProcessor` (dot path: response channel, exception text, command-list computation); `CommandProcessor` (RA pipeline, `$` routing); console `TypeCommand` prefix routing; rate-limit creator for the 150/3 s defaults.
- Runtime test: register `mp_probe` plus a colliding alias in a scratch plugin and check the log and `TryGetCommand`; send 200 dot commands from a test client and confirm the kick.
- Still UNVERIFIED: RA client `$0` poll interval, whether the host hub is recreated per round, max response size.
