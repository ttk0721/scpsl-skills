---
name: adding-custom-items
description: "Use when adding a special item to an SCP:SL LabAPI server that has no ItemType of its own (a buffed medkit, master keycard, tagged gun or coin, quest item) given by command, at spawn or on the map, and it must be recognised when held, used, dropped or picked up by someone else; or when tagged items go stale, duplicate, or outlive a round restart or a leaver."
---

# Adding Custom Items

Verified against LabAPI 1.1.7 / SCP:SL 14.2.7 (2026-10).

## Overview

The game has no "custom item". The only identity an item keeps from inventory to floor to another inventory is its `ushort` serial, so a custom item is a vanilla `ItemType` plus a plugin-owned table saying "this serial is special". Tag at creation, test in every event that carries an item, forget when the item or round is gone. Example: `examples/adding-custom-items` (`KitRegistry.cs`, `FieldKitPlugin.cs`, `FieldKitCommand.cs`).

## When to Use

- A medkit that heals more, a master keycard, a gun with fixed attachments, a quest coin.
- Not a new model, not persistence across restarts (serials die with the process), not ammo.
- Small addition to a plugin with a tag table: Decisions and Common Mistakes only, no ceremony. Bigger: brainstorming-scpsl-features, designing-scpsl-plugins.

## Ask First (one message)

1. LabAPI, EXILED or both? Exiled.CustomItems or UncomplicatedCustomItems installed (frameworks-and-variants.md)? 2. Which base item, what differs? 3. Given by command, at spawn, on the map? 4. Peak players. 5. Local test wanted? Ask, never push it. If they have a server, check whether it is public, never touch their ports, and ask whether to use an existing port or our own instance (running-a-test-server).

## Decisions

| Decision | Default | Change when |
|---|---|---|
| Identity | `Dictionary<ushort, tag>`; tag = `ItemType`, owner `UserId` (never a `Player`) | owner wants an existing framework |
| Set the tag | where you create the item (`AddItem`, `Pickup.Create` result); map items in `ItemSpawned` from the args wrapper | never `PickupCreated` |
| Give | `AddItem`, null check, optional drop at feet; guns: attachments, then ammo | ammo: `AddAmmo` |
| Held / used | `ChangedItem` / `UsedItem` (vanilla effect already applied); veto in `UsingItem` | |
| Picked up by someone else | `PickingUpItem`, cancellable | allowed: the tag follows the serial, no code |
| Dropped | nothing: the serial survives | client drops only raise `DroppingItem`/`DroppedItem`; API and death drops raise none |
| Cleanup | clear at `WaitingForPlayers` and `RoundRestarted`; two-strike sweep only for a cap or per-item state | never on `PickupDestroyed` or `Death` |

## Pattern

1. Config with kill switch and cap; `KitRegistry` with `Mark`, `TryGet(serial, actualType)`, `Clear`, `Sweep`. Main thread only.
2. Thin handlers: `Count == 0` returns first, then one lookup. Every event used is per-action or per-lifecycle (labapi-events).
3. Lifecycle: constant metadata, idempotent `Enable`, subscribe last, release in `Shutdown` (scpsl-game-lifecycle).
4. A give/spawn command behind `CheckPermission(PlayerPermissions.GivingItems, ...)` (scpsl-commands-and-permissions).
5. Verify (verifying-scpsl-work): give, equip, use, drop, re-pick, die, leave, restart. Say what you could not run.

## Key Parts of the Example

```csharp
// A tag whose type disagrees is stale: the serial counter wraps and is never reset.
if (found.Type != actualType) { _tags.Remove(serial); return false; }

Item item = player.AddItem(KitType);               // null at 8 non-ammo items
if (item != null) _registry.Mark(item.Serial, KitType, player.UserId);
Pickup pickup = Pickup.Create(KitType, position);  // already spawned; no Spawn()
_registry.Mark(pickup.Serial, KitType, null);      // tag at the creation site

ev.IsAllowed = false;                              // PickingUpItem: deny before it moves
void OnRoundBoundary() => _registry.Clear();       // WaitingForPlayers and RoundRestarted
```

`Sweep` drops a serial after two misses a period apart. Transitions and cleanup rules: serial-lifecycle.md.

## Common Mistakes

| Wrong | Right |
|---|---|
| "Serials restart each round" | Counter wraps at 65000, never reset. Clear at round boundaries, validate the type. |
| `Pickup.Create` then `Spawn()` | `Create` already spawns. |
| Door-handler sweep dropping serials missing from `Player.Items` and `Pickup.List` | A death drop is wrapped a step late. Two strikes a period apart, or clear at round boundaries only. |
| Prune on `PickupDestroyed` or `Death` | Pick-up destroys the pickup; death drops the item with the same serial. |
| A leaver's tagged item "stays on the map" | Disconnect removes held items with no pickup. Sweep, or clear the tag in `Left`. |
| `HasPermissions(PlayerPermissions.GivingItems)` | `sender.CheckPermission(flag, out response)`. |
| `AddItem(pickup)` to move an item | Leaves the pickup alive: a duplicate. Destroy it. |

## Links

labapi-wrappers, labapi-events, scpsl-game-lifecycle, scpsl-main-thread-and-timing, scpsl-commands-and-permissions, scpsl-roles-damage-effects, scpsl-hints-and-hud, porting-exiled-to-labapi, verifying-scpsl-work, running-a-test-server. Frameworks, firearms, keycards: frameworks-and-variants.md.
