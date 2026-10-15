# EXILED features without a LabAPI counterpart, and what EXILED costs

Verified against LabAPI 1.1.7 / SCP:SL 14.2.7 (2026-10). EXILED side checked against 9.15.x.

Companion to `SKILL.md` and `mapping.md`. Cost figures are **estimates from code reading** unless a
row says measured; nothing here was benchmarked on a live server. Measure with `sl-probe` or SlKit's
`FrameProfiler` before and after removing EXILED (see measuring-scpsl-performance).

## 1. Permissions (Exiled.Permissions -> LabAPI permissions)

LabAPI ships a permissions manager with a default provider. The data model is similar; the semantics
are not, and the file is silently rewritten.

| Aspect | EXILED | LabAPI |
|---|---|---|
| Group of a player | Remote Admin group of `player.Group`, falling back to the group flagged `default: true` | Name from the Remote Admin members map **by UserId only**, else the group named `default` |
| Runtime-assigned groups (override password, badge, `Player.Group = ...`) | Honoured | **Ignored**: the player is treated as `default` |
| Member whose group is missing from the file | Falls back to the default group | Gets an **empty** group, not `default` |
| Wildcard | `a.b.c` granted by `.*`, `a.*`, `a.b.*` or exact; case-insensitive | `.*` (root), exact (case-**sensitive**), or `parent.*` for the **immediate parent only** |
| Inheritance key | `inheritance` | `inherited_groups` |
| Default group | `default: true` flag | A group literally named `default` |
| File | `EXILED/Configs/permissions.yml` | `LabAPI/configs/permissions.yml` |

The LabAPI deserializer ignores unmatched keys, so un-renamed EXILED keys (`inheritance`, `default`) are
**dropped without an error**, and LabAPI then rewrites the file. A grant of `plugin.*` does not cover
`plugin.admin.kick`.

Port recipe: flatten wildcards (list `plugin.admin.*` and each level explicitly), rename `inheritance`
to `inherited_groups`, name the default group `default`, lower- or upper-case consistently, and make sure
every admin appears in the Remote Admin members map. In code use `player.HasPermission("x.y")` (string
permissions); it returns false inside `Enable()` because the provider registers after plugins enable. Game `PlayerPermissions` flags are a different check: `sender.CheckPermission(PlayerPermissions.X,
out response)`. See scpsl-commands-and-permissions.

## 2. Config and translations

| | EXILED | LabAPI |
|---|---|---|
| Config class | `Plugin<TConfig>`; shared per-port file or per-plugin file (default) | `Plugin<TConfig>`; `LabAPI/configs/<port>/<plugin name>/config.yml` or `global/` |
| Translations | `Plugin<TConfig, TTranslation>`, reloaded on round restart if configured | **No concept** |
| Comments in YAML | `Description` attributes | `YamlConfigParser` |

Translation replacement: a second class loaded in `LoadConfigs()` with `this.TryLoadConfig<T>("translation.yml",
out var t)`, reloaded by hand (for example on `WaitingForPlayers`).

```csharp
public override void LoadConfigs()
{
    base.LoadConfigs();
    if (!this.TryLoadConfig<Translation>("translation.yml", out var t)) t = new Translation();
    _translation = t;
    RebuildCaches();                 // LoadConfigs REPLACES the Config object: re-derive caches here
}
```

Two traps that EXILED hid: `labapi reload configs` **replaces** the `Config` object, so a cached
reference goes stale (read `Config.X` at use time, or rebuild in `LoadConfigs`); an empty or
comment-only `config.yml` deserialises to null (`Config ??= new Config()`). See designing-scpsl-plugins.

## 3. Exiled.CustomItems

EXILED model: an abstract `CustomItem` with a global registry, a tracked-serial set, `Check(item)` by
serial, `Spawn`/`Give`, and event wiring on `Dying`, `DroppingItem`, `DroppingAmmo`, `ChangingItem`,
`Escaping`, `PickingUpItem`, `ItemAdded`, `Handcuffing`, `ChangingRole`, SCP-914 upgrades. It also owns a
Harmony set it removes with an id-less `UnpatchAll()` on disable.

LabAPI replacement (no framework, about one class):

- `Dictionary<ushort, CustomItemDef>` keyed by **serial**. The serial survives drop and pickup. Fill it
  when you grant or spawn the item.
- Dispatch from `ChangedItem`, `DroppingItem`, `PickedUpItem`, `Hurting` (weapons: `FirearmDamageHandler`),
  `ShootingWeapon`, `Scp914Events.ProcessingInventoryItem` / `ProcessingPickup`. "Item added/removed" needs the game's
  `InventoryExtensions.OnItemAdded` / `OnItemRemoved`.
- Cleanup: **clear the dictionary at round boundaries** (`WaitingForPlayers`, `RoundRestarted`). Do not drop an
  entry on `PickupDestroyed` (a pickup is destroyed when somebody picks it up, and the item keeps the serial), on
  `OnItemRemoved` (a drop removes the item and creates a pickup with the same serial) or on `Death` (death drops the
  inventory as pickups with the same serials). Any of these loses the item's identity.
- Serials are a `ushort` counter that wraps and is never reset, and they are unique **per kind** (item vs
  pickup), not globally. Stale entries can false-hit after reuse, hence the round-boundary clear. Do not run a
  periodic "sweep" by testing `Item.Get`/`Pickup.Get`: a pickup created by game code (for example on death) gets
  its wrapper a step later, and a sweep in that gap deletes a live custom item. If you must prune, drop a serial
  only when neither lookup resolves, checked at least one frame after the last event. See labapi-wrappers.
- Spawn with `Pickup.Create(type, position)`; it already spawns.

Do not install `Exiled.CustomItems` only for this. Disabling or reloading it removes **every owner's**
Harmony patches (section 6).

## 4. Exiled.CustomRoles

EXILED model: a tracked-player set; `AddRole` sets the role, then uses a delayed call to clear and grant
inventory, ammo, health, scale, gravity and custom info; abilities with key-press activation through
server-specific settings; subscribes to `ChangingNickname`, `ChangingRole`, `SpawningRagdoll`, `Destroying`.

LabAPI replacement:

- Per-player state in `CustomDataStore<T>`. It is removed after the `Left` handler returns, so it is still
  readable inside `Left`.
- Apply with `player.SetRole(role, RoleChangeReason.X, flags)`. **No delayed call is needed**: grant the
  inventory inside `ReceivingLoadout` (`AddItem`, `ClearItems`, `SetAmmo` on the args) or `ReceivedLoadout`
  or `ChangedRole`. `ReceivingLoadout.InventoryReset` is ignored by the game.
- Never call `SetRole` inside `ChangingRole` unguarded: it re-enters and recurses. Change `ev.NewRole`, or
  defer one frame behind a guard flag.
- Clear on `ChangedRole` / `Death`. Set `CustomInfo` (validate it).
- Key-press abilities: the game's server-specific settings (LabAPI has no wrapper for them in 1.1.7). See
  scpsl-server-specific-settings.
- Do not hold `Player` references in your own sets across leave or round restart; store `UserId` and
  re-resolve (scpsl-game-lifecycle).

## 5. Other EXILED-only runtime features

All implemented as always-on Harmony patches in EXILED, none has a LabAPI equivalent: per-player ammo and
category limits, individual friendly fire, stamina regen/usage multipliers, GhostMode, FakeRoles,
`CurrentHint` tracking, offline-mode ids, server-name patch, SCP-079 recontain and SCP-173 look
adjustments, vision-information parsing, keycard detail patches, and the `Core` actor/object component
framework (`Player.AddComponent`, `StateMachine`).

Port each as an explicit Harmony patch on the game method (scpsl-harmony-patching) or drop the feature.
For visibility and fake roles prefer the LabAPI `ValidatedVisibility` event and the game's
`RoleSyncEvent` over a transpiler, and read scpsl-networking-for-plugins first: hiding a player from the
position list does not silence footsteps, voice or gunshots, and SCP mechanics that raycast server-side
break if you hide an SCP they consider visible.

## 6. What EXILED costs on a server

Estimates from code reading, not measured.

| Cost | Mechanism | Size |
|---|---|---|
| Position path, GhostMode | Always-on transpiler on `FpcServerPositionDistributor.WriteAll`; per receiver-target pair it does two `Player.Get`, a role check and an invisibility-set lookup | Per frame, pairs grow about N^2. Estimate 0.8-2 ms per frame (unmeasured) |
| Position path, RoleSync | EXILED subscribes `RoleSyncEvent` permanently; the game runs the handler for every pair (two `Player.Get`, dictionary lookup), plus an extra transpiler that also runs it for the receiver and each non-FPC target | Per frame. Estimate: roughly doubles the GhostMode cost (unmeasured) |
| Hints | A postfix on `HintDisplay.Show` per text hint: `Player.Get`, dictionary lookup, kill and start a MEC coroutine, allocate a `Hint` object | Per hint per player. Estimate: about 20k coroutines in one frame at round start |
| Hint wire | EXILED puts the text in both the text field and a string parameter | Doubled text; wire size UNVERIFIED |
| Event dispatch | EXILED: two pooled arrays plus per-handler try/catch per raise. LabAPI: one `Delegate[]` per raise with subscribers. In both, args are built even with no subscriber | Per event |
| Patch footprint | About 368 Harmony patch attributes (events about 256, generic about 83, fixes about 29). With dynamic patching (default) only generic, fixes, a fixed set of always-on event patches and events that have a subscriber are applied. Never unpatched on `-=` | Startup plus lazily at first subscribe |
| Internal handlers | `Spawned` and `Died` loop over all players with LINQ even with no fake roles; `Verified` loops all hubs and sends a scale message per hub to the joiner | O(N) per event; mass spawn at round start O(N^2) |
| Collections | `Player.List` / `Count` / `Server.PlayerCount` copy a dictionary to a list per access. `Item.Get(serial)` and `Pickup.Get(serial)` are linear | One list per call |
| Memory | Two wrapper graphs (EXILED and LabAPI) for Player, Item, Pickup, Door, Room; only `Player` is bridged | Per object |
| Loader | Updater thread when auto-update is on (default). EXILED refuses to load if its game version differs from the server's (unless configured) and prints one console line | Once |
| Harmony hazard | `Exiled.CustomItems` disables with an id-less `UnpatchAll()`; the installed Harmony removes **every** owner's patches when the id is null. Triggered by plugin reload or config disable, not by loading. UCR, UCI and MER also call `UnpatchAll(null)` | Your patches vanish silently |

Decision rule: if the server does not need GhostMode, FakeRoles or EXILED's hint tracking, removing EXILED
removes the two always-on `WriteAll` transpilers and the permanent `RoleSyncEvent` subscriber, which is
the largest recurring cost on a full server. Keep EXILED loaded only for plugins you cannot port, and
budget the position path.

## 7. Hosting a LabAPI plugin inside EXILED, and running both stacks

- A LabAPI-native plugin dropped in the EXILED plugin folder is hosted by EXILED: EXILED calls `Enable()`
  after all EXILED plugins. It is not enabled by LabAPI's own pass, so `Priority` ordering against other
  LabAPI plugins does not apply. A reload can call `Enable()` again: make it idempotent or leaking
  subscriptions and coroutines result (designing-scpsl-plugins).
- Keep each behaviour on exactly one layer. Registering the same behaviour on an EXILED event and its
  LabAPI twin runs it twice, at different points, and for the "before" anchors an EXILED cancel hides the
  occurrence from your LabAPI logging or auditing plugin (mapping.md section 0).
- A cancelled EXILED `ChangingRole` returns early but the game still raises LabAPI `ChangedRole` with old
  role equal to new role (code reading, UNVERIFIED at runtime).
- Test with EXILED loaded and unloaded. Reference only LabAPI types in the ported plugin.
- Your Harmony patches: unique id, `UnpatchAll(yourId)` only, `GetPatchInfo` at lifecycle points to detect
  someone else's `UnpatchAll(null)`. Transpilers on shared methods (`WriteAll`, `ProcessShot`,
  `InitializeNewRole`): choose priority deliberately. `Priority.Last` sees EXILED's modified IL (match
  tolerantly), `Priority.First` sees vanilla IL; neither is established as safer.

## Re-verifying on a new build

- Re-check the permissions semantics by loading a test `permissions.yml` with an un-renamed EXILED key and
  confirming it is dropped; re-check wildcard depth and case sensitivity with `HasPermission`.
- Re-check that `Plugin<TConfig>` still has no translation concept and that `TryLoadConfig` keeps its
  signature.
- After an EXILED release, count patch attributes again and re-read whether `CustomItems` still calls an
  id-less `UnpatchAll`; run `GetPatchInfo` on your targets after toggling it.
- Re-measure section 6: frame period with and without EXILED (`FrameProfiler` or `sl-probe`), at a realistic
  player count. Treat every number above as a hypothesis until you have done so.
- Re-check that `Pickup.Create` still spawns by default and that `ReceivingLoadout` still ignores
  `InventoryReset`.
