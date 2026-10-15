# Permissions reference

Verified against LabAPI 1.1.7 / SCP:SL 14.2.7 (2026-10). Part of scpsl-commands-and-permissions.

## Two unrelated systems

| | Game flags | LabAPI strings |
|---|---|---|
| Type | `PlayerPermissions` (ulong bit flags) | `string` such as `"myplugin.heal"` |
| Source of truth | RA config (`config_remoteadmin.txt`: `Members`, groups, `Permissions:` map) | `permissions.yml` in the LabAPI configs folder |
| Check on a sender | `sender.CheckPermission(PlayerPermissions.X, out response)` (game's `Misc` extension) | `sender.HasPermission("x")` / `HasPermissions(...)` / `HasAnyPermission(...)` from `LabApi.Features.Permissions` |
| Check on a Player | `player.HasPermission(PlayerPermissions)` | `player.HasPermission("x")` |
| Multiple | `CheckPermission(PlayerPermissions[])` = ANY-of | `HasPermissions` = ALL-of, `HasAnyPermission` = any |
| Server console | always passes | always allowed |
| Plugin may define new | not supported (see below) | yes, just write the string |

The two do not know each other. `HasPermission(string)` does not consult `PlayerPermissions`, and `CheckPermission` does not read `permissions.yml`. Compile errors like "no overload takes PlayerPermissions" mean you used the wrong family.

## Game flags

- `PlayerPermissions` has 32 flags in three kinds: RA access, view access, misc. A player gets RA iff at least one of their flags is an RA-access kind.
- `IsPermitted(perms, check)` is `(perms & check) != 0`: ANY-of. The array overload ORs the flags, an empty array is true. For "needs both", call `CheckPermission` once per flag.
- `Player.HasPermission(PlayerPermissions)` is `HasFlag` = ALL-of and has no console handling. With a single flag both agree, so pass single flags.
- `CheckPermission` on a sender that is not a game `CommandSender` (a custom `ICommandSender`) always fails. The `out response` overload fills the standard "required permission" text.
- Senders: `PlayerCommandSender` has `Permissions` = the hub's role snapshot, `KickPower`, `FullPermissions=false`. `ServerConsoleSender` has all bits, kick power 255, `FullPermissions=true`.
- `ServerRoles.Permissions` is a snapshot taken at login or `SetGroup`. Editing the RA config does not change connected players. `RefreshPermissions()` grants/updates but cannot revoke for someone no longer in `Members`; revocation needs `SetGroup(null)`. Do not cache `sender.Permissions` across commands.
- `ServerStatic.PermissionsHandler` is REPLACED by a fresh instance on `/pm reload`, `/pm setgroup`, `/pm group ...`. Never hold the reference.
- `PermissionsHandler.RegisterPermission` (custom flags) has no callers in the game, LabAPI or EXILED, and its flags vanish at the next handler replacement. Do not use it; use strings.
- Northwood staff get implicit bits regardless of your config (admin chat, overwatch, hidden-badge view; full perms if the matching `enable_*_access` option is set).

## LabAPI string permissions

- `IPermissionsProvider` implementations are aggregated by static `PermissionsManager` (plain dictionary, LINQ + closure per call). `HasPermissions(all)` must be satisfied by ONE provider, not the union of providers. Only `DefaultPermissionsProvider` ships; you can `PermissionsManager.RegisterProvider<T>()` your own in `Enable()`.
- **Timing:** the default provider is registered after all plugins are enabled and before `ServerEvents.OnPluginsEnabled`. In `Enable()` every check returns false and `Server.DefaultPermissionProvider` is null (logs a warning). Check permissions only inside command/event handlers.
- `ICommandSender` extensions: a resolvable player is checked; the console/non-player sender is allowed; a `PlayerCommandSender` whose `Player` cannot be resolved (null user id, wrapper not cached yet) is DENIED. For a `PlayerCommandSender` use `Player.Get(p.ReferenceHub)` when in doubt. Player lookup is a cached dictionary hit, O(N) on first miss.
- `HasPermission` on a not-yet-authenticated player throws (null user id): check `player.IsReady` first.

### permissions.yml

- File: LabAPI `configs/permissions.yml`, one per OS user (not per port). Shape: `group: { inherited_groups: [...], permissions: [...] }`. Default content is one empty `default` group. The provider rewrites the file at every start (comments are lost).
- Group of a player = their RA `Members` group name, else `default`. A group name that is in `Members` but ABSENT from the yml gets an EMPTY group, not `default`. Add every RA group (at least `inherited_groups: [default]`). Players who hold RA rights through global/staff access, an override password or a runtime `SetGroup` look like "no group" to LabAPI.
- Matching: exact string, case-sensitive; `.*` = root; `a.b.*` grants `a.b.c` but NOT `a.b.c.d`; `a.*` does not grant `a.b.c` and does not grant `a`. List nested wildcards explicitly; keep strings lower-case dotted.
- Inheritance recursion has no cycle guard: a cycle in `inherited_groups` can overflow the stack (UNVERIFIED effect; validate before saving).
- `ReloadPermissions` / `ReloadAllPermissionsProviders` do NOT re-read the file. Edits need a restart (or your own provider that re-reads the file). A malformed file makes the provider constructor rethrow (effect on server start UNVERIFIED).
- `AddPermissions/RemovePermissions` edit the player's whole GROUP (every member), concatenate without dedupe, and rewrite the yml synchronously on the main thread; if the group is absent from the file the change goes to a throw-away group and is lost. Treat as a group editor, never a per-player temporary grant.
- Do not rely on `PlayerEvents.GroupChanged` to invalidate caches for string permissions: the provider reads the RA `Members` lookup, not the in-game group that event reports.

## Cost and caching

- Game flag check: one AND on a ulong. String check: dictionary hit + LINQ/closure + per-group `Contains` + recursion; `GetPermissions` allocates. Fine per command. Never per frame times per player.
- If you need a boolean often (HUD, per-tick), cache it per player at join/verification and refresh after your own changes. Main thread only: `PermissionsManager` and the default provider use plain collections and a file write.

## Dot commands and authorisation

- Dot commands run for ANY connected client. The dispatcher only rate-limits. Authorise inside `Execute` (string permission, role, state) and refuse politely.
- Design choice: admin-only features belong in RA commands (the game gates RA access); player features in dot commands with an explicit check.

## Re-verifying on a new build

- LabAPI `Features/Permissions`: `PermissionsExtensions` (string only? console allowed? unresolved player denied?), `PermissionsManager`, `Providers/DefaultPermissionsProvider` (matching, group resolution, reload), `PermissionGroup.Default`, `Wrappers/Players/Player` (`HasPermission(PlayerPermissions)` still `HasFlag`?).
- `PluginLoader.Initialize`: provider registration still after `LoadAllPlugins`?
- Game: `Misc.CheckPermission` overloads, `PermissionsHandler.IsPermitted`, whether anything now calls `RegisterPermission`.
- Runtime test: with a test group, check `a.b.*` against `a.b.c.d`, a missing yml group, a call in `Enable()`, and edit-then-reload.
