---
name: adding-custom-roles
description: "Use when adding a custom or special role to an SCP:SL LabAPI plugin: a player who spawns as a base role with a different loadout, health, effects, label or abilities (medic, boss, event role), role chances, or holders another feature must recognise later. Also when custom-role stats vanish after spawn, labels survive death, or a role leaks into the next round."
---

# Adding custom roles

Verified against LabAPI 1.1.7 / SCP:SL 14.2.7 (2026-10).

## Overview

The game has no custom roles. A custom role is a normal base role plus a layer you apply and remove: loadout, stats, effects, label, abilities. Decide in `ChangingRole`, apply in `ChangedRole`, key by `UserId`, undo at that player's next `ChangedRole`.

## When to Use

Scale the process to the task:

| Task | Do |
|---|---|
| One stat or item tweak on a vanilla role | A `ChangedRole` handler (scpsl-roles-damage-effects). No registry, config or process. |
| Special players you must recognise later (abilities, HUD, kill stats) | This recipe, one definition. |
| Several roles, chances, a command, other plugins asking | The full example. |

## Decisions

| Decision | Default | Why |
|---|---|---|
| Base role | The role whose team, spawnpoint and rules you want | Every other plugin sees this role |
| Trigger | Roll in `ChangingRole` for vanilla spawn reasons; `Grant` (`SetRole`, `RemoteAdmin`, `All`) for commands | Redirect, never a second `SetRole` |
| Identity | `UserId` to {role id, `Player.LifeId`} | `Player` dies at restart; `LifeId` changes per role instance |
| Loadout | `ReceivingLoadout`: edit `Items`/`Ammo` | No give-then-remove; needs `AssignInventory` |
| Stats | `ChangedRole`, max before value | The game resets them after `Spawned` |
| Label | Custom info plus info area, remember the previous | A role change does not reset them |
| Ability | Passive: effect. Periodic: one `SlClock` job over holders. Reactive: handler gated on holder count | No coroutine per holder |
| Cleanup | Next `ChangedRole`, `Left`, `WaitingForPlayers`, `Shutdown`, tick self-heal | Death is a role change |
| Kill switch | Config flag read at use | Reload applies it |

## Pattern

1. `ChangingRole`: vanilla reason, kill switch, definition, chance, cap, then store `UserId` to {role id, frame}. O(1).
2. `ReceivingLoadout`: same frame and base role, then swap the lists.
3. `ChangedRole`: release the old holding, take the same-frame decision, check the arriving role is the base role, apply.
4. One 1 Hz job: regen, announce once `IsReady`, drop entries whose `LifeId` changed.

```csharp
// Valid only inside the call stack that decided it; a cancelled change goes stale next frame.
_registry.SetPending(id, def.Id, Time.frameCount);            // ChangingRole
if (_registry.TryRemoveActive(id, out Assignment old)) Release(p, old);   // ChangedRole: also death
if (!_registry.TryTakePending(id, Time.frameCount, out PendingGrant g)) return;
if (def.BaseRole != e.NewRole.RoleTypeId) return;             // another plugin redirected it
```

Compiled and linted: `examples/adding-custom-roles/` (`CustomRolesPlugin.cs` pipeline, `RoleRegistry.cs` state, `CustomRolesConfig.cs`, `CustomRoleCommand.cs`). Test with `customrole <id> <role>` on real players (dummies share one `UserId`). Ask before starting a local server and never touch the owner's ports (verifying-scpsl-work).

## Common Mistakes

| Wrong | Right |
|---|---|
| `Dictionary<Player, ...>` or `PlayerId` keys | `UserId` plus `LifeId`, cleared in `WaitingForPlayers` |
| Health or items in `Spawned` | `ChangedRole`; items in `ReceivingLoadout` |
| `SetRole` inside a role handler | Mutate `e.NewRole`; defer the rest |
| Label left on the dead | Restore only what is still yours |
| Pending flag instead of frame | A cancelled change hands the role to the next spawn |
| `_cfg = Config` in `Enable` | `Config` is replaced on reload: read at use |
| Setup in `Joined`, nickname in `ChangedRole` | Late joiners get `ChangedRole` before `Joined`; announce from the tick |
| Ability handler on `Hurt` with no gate | First line: `if (holders == 0) return` |
| Heal every frame | Whole points at 1 Hz; dead players receive every change |
| `EnableEffect` with the default duration | Pass it; `0` means forever and is deliberate here |
| Roll for `Escaped` or admin sets | Vanilla spawn reasons only |
| Another plugin reads your class in `ChangedRole` | If you assign the class after the role change (as role frameworks do), say so in your API docs or raise your own "class assigned" signal; readers in `ChangedRole` otherwise see the vanilla role |
| A class with its own items set with `RoleSpawnFlags.All` or `AssignInventory` | The role's default loadout lands first and the class items pile on top: pass `UseSpawnpoint` only and `ClearInventory()` before giving the class items |

## Reference

- spawn-timeline.md: call stack, late joiners, restarts, appearance, effects.
- coexisting-with-role-plugins.md: who wins, shared state, the query API.

Facts: scpsl-roles-damage-effects, scpsl-game-lifecycle, labapi-events, labapi-wrappers, scpsl-main-thread-and-timing, scpsl-commands-and-permissions, scpsl-hints-and-hud, designing-scpsl-plugins, adding-player-hud.
