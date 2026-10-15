# Custom item frameworks and item-type variants

Verified against LabAPI 1.1.7 / SCP:SL 14.2.7 (2026-10). Part of adding-custom-items. Framework internals marked UNVERIFIED were not read for this library.

## Which framework

LabAPI has no custom item system. Options:

| Option | Use when | Watch for |
|---|---|---|
| Own tag table (default) | one or a few items, no framework installed, you want control of cost | you own cleanup, spawn rules and any abilities |
| Exiled.CustomItems | the server runs EXILED and the owner's other plugins already register items there | model: an abstract item class, a registry, a set of tracked serials, many EXILED event hooks. Its disable path calls Harmony `UnpatchAll` without an id, which removes every owner's patches: avoid enabling or reloading it only for this (scpsl-harmony-patching) |
| UncomplicatedCustomItems | the owner already defines items there; a LabAPI build exists | its API and serial tracking are UNVERIFIED here. It can also remove all Harmony patches. Ask the owner what it defines |

Rules:

1. Ask which one is installed before writing anything (Ask First, step 1). The owner may expect new items to show up in their framework's registry or config.
2. Never tag the same serial from two tables. Two frameworks do not know each other's serials, so both would run their handlers on one item. Use different base `ItemType`s or let one table own it.
3. Do not install a framework to avoid fifty lines of tag table.
4. Porting EXILED items: porting-exiled-to-labapi. There is no one-to-one mapping.

## Variants by item type

| Base item | What changes | Facts |
|---|---|---|
| Consumable (medkit, painkillers) | tag ends at use: `Remove` in `UsedItem` | `UsedItem` fires after the vanilla effect. `UsableItem.Use()` skips the events; use the normal pipeline |
| Firearm | give with `AddItem`, set `AttachmentsCode` first, then `StoredAmmo` | `Pickup.Create(gun)` gives an empty gun (state is serial-keyed). Attachment changes broadcast to every connection: never per tick. Personal-preference reasons override the attachment code before your setter runs; read it back |
| Weapon damage | event-time change | scale in the hurting event by mutating the existing damage handler's fields; assigning the event's handler is ignored. scpsl-roles-damage-effects |
| Keycard | `CreateCustomCard` for a custom look or permissions | arguments are shared static state: a failed add leaks them to the next custom card. Check the result, always pass full arguments |
| Grenade | `ThrewProjectile` | the projectile keeps the serial; `Item` and `Pickup` coexist briefly. Do not prune on removal |
| Coin, radio, flashlight | same as the example | no special rules |
| Ammo | not an item | `AddAmmo`; `AddItem(ammo)` returns an item that already removed itself |

## Other decisions

- Timer-based behaviour (cooldown, charge): keep the state in the tag, drive it from one `SlClock` job, never a coroutine per item (scpsl-main-thread-and-timing).
- Feedback: one-shot hints from per-action handlers are fine. A persistent indicator belongs to the HUD owner (scpsl-hints-and-hud).
- Giving on spawn: `ChangedRole`, not `RoundStarted`. An inventory reset on the next role change removes the item without a pickup, so the sweep frees it.
- Kill switch: with `IsEnabled` false, `Enable` subscribes nothing. Items already given become ordinary items.
- Permissions: the game flag for the give command, a LabAPI string permission if the owner uses `permissions.yml` (scpsl-commands-and-permissions).

## Re-verifying on a new build

- Check whether LabAPI has gained a custom item or item-tag facility.
- Check the installed framework versions, and whether they still call `UnpatchAll` without an id.
- Check `AddItem(Pickup)` still leaves the pickup alive, `Pickup.Create` still spawns by default, and the keycard custom-card arguments are still static.
- Re-run the lifecycle checks in serial-lifecycle.md.
