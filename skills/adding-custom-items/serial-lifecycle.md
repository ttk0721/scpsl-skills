# Serial lifecycle for custom items

Verified against LabAPI 1.1.7 / SCP:SL 14.2.7 (2026-10). Behaviour is from reading code, not from a running server, unless noted. Part of adding-custom-items. Wrapper facts are in labapi-wrappers (items-and-pickups.md); this file is what to DO with a tag at each transition.

## What happens to the serial

| Transition | Serial | Events raised | Tag action |
|---|---|---|---|
| `Player.AddItem(type)` | new, generated | no LabAPI item-added event | `Mark` from the returned item (null means nothing was created) |
| `Pickup.Create` | new, generated | `PickupCreated` is skipped | `Mark` from the returned pickup |
| Map spawn | minted at map generation, final | `ItemSpawning`, `ItemSpawned` | `Mark` from the args wrapper; door-gated pickups exist unspawned |
| Role loadout | new serials | `ReceivingLoadout`, `ReceivedLoadout`, then `ChangedRole` | to convert a loadout item, find the item of that type once in `ChangedRole` and `Mark` it |
| Client drop | same, new `Pickup` | `DroppingItem`, `DroppedItem` (+ throw pair) | none |
| API drop, death drop | same, new `Pickup` | none of the drop events; death raises `Dying` then `Death` | none; never clear on `Death` |
| Pick-up | same, new `Item` | `PickingUpItem` (cancellable), `PickedUpItem` | veto, or none |
| Grenade throw | `Item` and projectile share it for a moment | `ThrowingProjectile`, `ThrewProjectile` | never prune on an item removal with no pickup |
| Consumable used | item removed, no pickup | `UsingItem`, `UsedItem` | `Remove` the tag in `UsedItem` |
| Disconnect | held items removed, no pickup | `Left` | sweep, or `Remove` |
| Role change that resets the inventory | items removed, no pickup | `ReceivingLoadout` | sweep |
| Escape | items kept: pickups, then re-picked with the same serial | escape events | none |
| SCP-914 upgrade, ammo conversion, SCP-330 merge | identity lost: new serial or the item removes itself | SCP-914 processing events | decide: veto the upgrade, or re-tag the result |
| Round restart | every pickup destroyed, no per-item removal | `RoundRestarted`, `WaitingForPlayers`, a `PickupDestroyed` burst | `Clear` |
| Counter wrap after 65000 | a live serial can be reissued | none | validate `ItemType` on every lookup |

## Cleanup rules

1. `Clear` at `WaitingForPlayers` and `RoundRestarted`. Idempotent, because `RoundRestarted` can fire twice. Never persist serials.
2. Prune mid-round only if you cap live items or keep per-item state. Otherwise the table grows by one per admin action and the clear is enough.
3. A prune must prove the object is gone: not an `Item`, not a `Pickup`, checked at least one frame after the last event. A pickup made by game code (death drop) is wrapped a step later, so a one-shot check can see neither. Two sweeps a period apart is the cheap proof (`KitRegistry.Sweep`).
4. Do not prune from `PickupDestroyed` (a pick-up destroys the pickup, and its argument can be null), from `Death` (the item lives on as a pickup), or from a removal notice with no pickup (a thrown grenade).
5. `Item.TryGet(serial)` is linear and allocates on every call, and it only knows inventory items. Fine in a sweep, wrong in a hot path. In handlers take the serial from the event args.
6. `Pickup.TryGet(serial)` has no rescan: a same-frame pick-up and re-drop can leave a live pickup out of its cache. `KitRegistry.IsAlive` scans `Pickup.List` for serials that missed.

## Per-item game state keyed by serial

Magazine contents, attachment codes, cooldowns and keycard details live in game tables keyed by serial and are not cleared per serial on destroy. A recycled serial can show the previous item's ammo or attachments. The type check does not help when both items share a type. Nothing here fixes it; keep per-item state of your own in the tag, not in the game's tables.

## Cost

No numbers measured. Handlers cost a count check and at most one dictionary lookup. The sweep is O(tags x live items) every 30 s. The costs that matter are the ones you add: a hint per equip, a pickup per spawn (every pickup is visible to every connection, so spread bursts), attachment changes (broadcast to all). See scpsl-engine-cost-model.

## Re-verifying on a new build

- Log a map pickup's serial in `ItemSpawned` and again after the round starts: unchanged.
- In game, print `Count` after each step. Give a kit, drop it with the client, pick it up as another player (denied if `BindToOwner`), die holding it: `Count` stays 1 throughout. Use a kit: 0 at once. Give a kit and disconnect the holder: 0 after about two sweep periods. Restart the round: 0.
- Check that `UsedItem` still fires after the medkit has removed itself, and that the args wrapper still yields the serial.
- Check the item-added and item-removed statics, and whether LabAPI has gained an item-added event; if so, tag from it where it fits.
- Check the serial counter still wraps and is still not reset.
