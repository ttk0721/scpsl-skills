# Item and Pickup wrappers

Verified against LabAPI 1.1.7 / SCP:SL 14.2.7 (2026-10). Behaviour is from reading code, not from a running server, unless noted. Cost figures are estimates. UNVERIFIED marks what was not observed.

## Identity: the serial

- The `ushort` serial is the identity of the logical object. A held item and the pickup it becomes share one serial, but they are different Unity objects with different wrappers (`Item` and `Pickup`) in different caches. Keep `(kind, serial)`, never the wrapper.
- Serial 0 means "nothing". Passing 0 where a serial is accepted means "generate one". LabAPI refuses to cache an `Item` with serial 0.
- The generator is a bare `ushort` counter. It wraps after 65000 and nothing in the game calls its `Reset()`. It is not reset per round, so it runs across rounds until it wraps. It is not atomic, so main thread only.
- Serials are unique per kind, not globally. A thrown grenade keeps the held item's serial for its projectile while the `ThrowableItem` still sits in the inventory for a moment, so one serial can be live as both an Item and a Pickup.
- The game keys a lot of per-item state by serial in static dictionaries: magazine contents, attachment codes, pump and chamber state, SCP-127 tier progress, keycard details, MicroHID energy, coin flips, usable cooldowns. Most are cleared only by client-ready hooks, never per serial on destroy. Whether a dedicated server reaches those hooks on every round restart is UNVERIFIED. So a reused serial can pick up stale state, and a plugin that stores serials across rounds can get false hits.
- Identity is lost across SCP-914 upgrades, role-change loadouts, ammo conversion and SCP-330 bag merging, which mint new serials or remove the item.
- Map pickups keep the serial minted when the map distributor creates them. A second serial is generated at spawn, but the assignment is a no-op because the pickup's sync-info equality ignores the serial. It is safe to tag in `ItemSpawning`/`ItemSpawned` using the wrapper from the event args. Door-gated map pickups exist un-spawned (`IsSpawned` false) with their final serial.
- Never invent serials by hand. Reusing a LIVE serial is legitimate only to carry the same object between Item and Pickup (what `AddItem(Pickup)` and the game's own drop and pick-up code do).

### Tagging custom items

```csharp
sealed class TaggedSerials
{
    readonly HashSet<ushort> _items = new();
    public void Mark(ushort serial) { if (serial != 0) _items.Add(serial); }
    public bool Has(ushort serial) => _items.Contains(serial);   // test on Item AND Pickup events
    public void Clear() => _items.Clear();                       // from WaitingForPlayers / RoundRestarted
}
// Giving:  var it = player.AddItem(ItemType.KeycardMTFOperative); if (it != null) tags.Mark(it.Serial);
// Do not persist serials to disk or a database.
```

- Clear the set at round boundaries. This is the recommended pattern when the set grows by one per admin action. Do not run a periodic sweep.
- If you must prune: do not drop a serial on `PickupDestroyed` (a pickup is destroyed when somebody picks it up), and do not treat an inventory-removal notice with a null pickup as proof of death (a thrown grenade's Item is removed that way while the projectile still carries the serial). Prune only when neither `Item.Get(serial)` nor `Pickup.Get(serial)` resolves, checked at least one frame after the last event. A pickup created by game code, for example the drops on death, gets its wrapper only in its start step on a later engine step, so a same-frame check can see neither the destroyed item nor the not-yet-wrapped pickup.
- `Player.Items` allocates wrappers on first sight. Do not scan it on a timer to find tagged items.
- A round restart does not raise a removal notice per item. Clear at the restart.
- Validate a stored serial before use with `Item.Get(serial)?.Type == expected`, and expect the lookup to miss when the serial is currently a pickup.

## Wrapper creation and caching

- Item wrappers are created when the item object starts, or lazily by `Item.Get(ItemBase)`. `Player.AddItem`, `Player.Items`, `Player.CurrentItem` and every event args that carries an item create them. Pickup wrappers likewise.
- The wrapper class is the exact runtime type. Unregistered types fall back to plain `Item` or `Pickup`; for pickups that logs a warning every time.
- A wrapper is cached only if it is not destroyed, not a prefab template, has a non-zero serial, and its object is active. A wrapper that fails this is returned but not stored, so each `Get` hands back a new object and reference equality is meaningless. Template wrappers behave this way, and their setters mutate the shared prefab. Key your own data by serial or `Base`.
- `Item.Get(ushort)`: cache hit, else an O(live items) scan with a closure. The closure is allocated on every call, hit or miss. It knows inventory items only; a serial that is currently a pickup returns null. Misses are never cached. Avoid it in hot paths.
- `Pickup.Get(ushort)` is a pure cache lookup with no rescan. If a second pickup with the same serial was wrapped before the first one's cleanup ran (same-frame pick-up then re-drop), the live pickup can be missing from `SerialCache` for good while still in `Pickup.List`. Scan `Pickup.List` if you must find it.
- `ServerEvents.PickupCreated` is unreliable: it fires only if no wrapper existed yet, and `Pickup.Create`, `Item.DropItem` and the drop and throw event args all create the wrapper first. Tag at the call site that creates or drops the pickup. `PickupDestroyed` can carry a null `Pickup` when the pickup was never cached.
- `Item.GetAll(...)` returns a pooled list. Return it with `NorthwoodLib.Pools.ListPool<Item>.Shared.Return`.
- `Item.List`, `Pickup.List`, `Player.Items` mutate when wrappers are created or items are removed. Snapshot with `ToArray()` before a loop whose body can create, drop, remove or `Get`.

## Item to pickup to item

- Drop keeps the serial and creates a new pickup object. `Item.DropItem()` returns the new `Pickup`; `PreviousOwner` is set. The old item is destroyed at end of frame, so `Item.Get(serial)` still returns the dead item for the rest of that frame.
- Pick-up reverses it: a new item object and wrapper with the same serial; the old pickup wrapper goes at end of frame.
- `Player.DropItem`, `Item.DropItem` and the death drop call the game's drop directly and raise none of the dropping/dropped/throwing/threw events; those fire only for the client-command path. They do raise `PlayerDroppingAmmo` for ammo. The API drop has no throw velocity.
- Death: items are dropped as ordinary pickups after `Dying` and before `Death`. Read the inventory in `Dying`. Exception: a silent damage handler that prevents the drop leaves items until the role change. `Dying` fires only on the normal damage path.
- Disconnect: when a player leaves, every item they hold is removed with no pickup (`disconnect_drop` only adds a death chain for a living leaver first, which is the death drop above). Nothing stays on the map, so a tagged, special or "master" item does NOT survive its owner as a pickup. Only if the player dropped it earlier does a pickup exist. Your own serial sets are not cleaned for you: drop the serial in `Left` (or on a later prune), or dead serials keep counting toward any cap you keep. During a round restart the per-item removal is skipped entirely, so no per-item removal notice arrives either.
- Primed grenade: dropping a held grenade whose pin is pulled throws a live projectile (same serial) and destroys the pickup that `DropItem()` returned, within the same call. `Pickup.IsDestroyed` stays false until end of frame, so it does not tell you. Check whether the held item is a primed `ThrowableItem` before dropping.
- Throw-denied reinsert: an item with the same serial is recreated in the frame the old one is queued for destruction.

## Giving, removing, ammo

- `Player.AddItem(ItemType, reason = AdminCommand)` returns `Item?`. It returns null when the player holds 8 non-ammo items, or the type is unknown, and throws if the server is inactive. Check `IsInventoryFull` and the result.
- Category limits (for example keycards) are enforced only on the pick-up path, so API gives bypass them. Check the game's limit helper yourself if you need it.
- A non-null result can still wrap an item that already removed itself: ammo types convert to reserve ammo and remove themselves, and an SCP-330 bag merges into an existing bag.
- Item changes sync as one full-list refresh per player per frame. Giving eight items in one frame is one message. Ammo likewise.
- Ammo: use `AddAmmo`/`SetAmmo` (clamped 0..65535, no per-type cap). `Player.Ammo` is the live dictionary and direct writes never reach the client. `AddItem(ammoType)` is the wrong tool.
- `Player.DropAmmo(type, amount, checkMinimals = true)` raises small requests to about half a pickup, so `DropAmmo(type, 5)` can drop more than 5 (about half a pickup's worth, so the exact amount depends on the ammo type). Pass `checkMinimals: false`.
- `Player.AddItem(Pickup)` and `Item.MoveTo(Player)` do NOT destroy the source pickup, so you get a duplicate (item in inventory plus a live pickup, same serial). After `AddItem(pickup)` returns non-null, call `pickup.Destroy()`. Do not use `MoveTo`. Exceptions: an SCP-330 pickup (candies that did not fit stay on the source pickup) and ammo pickups.
- `RemoveItem(...)` destroys without dropping. The `ItemType`, `DropAllItems`, `ClearItems` forms snapshot internally.

## Creating and destroying pickups

- `Pickup.Create(type, pos, rot, scale, networkSpawn = true)` already network-spawns. Do not call `Spawn()` afterwards (a second spawn only logs a warning). It returns null for `None` or a type with no template. The scale is applied before spawn so it ships in the spawn message. `LastOwner` is null.

```csharp
// Simple: Create spawns it.
var pk = Pickup.Create(ItemType.Coin, pos);

// Configure before spawn, then spawn once. You now own Spawn() and Destroy().
var locked = Pickup.Create(ItemType.Coin, pos, Quaternion.identity, Vector3.one, networkSpawn: false);
if (locked != null) { locked.IsLocked = true; locked.Spawn(); }
```

- `networkSpawn: false` leaves a simulated server-only object with no network traffic and no out-of-range cleanup. Always `Spawn()` or `Destroy()` it. Falling into the void is never cleaned until round restart.
- Spawned pickups beyond about 128 units of the nearest map waypoint on any axis are destroyed, but only while moving (not frozen). Keep positions in range.
- `Weight`, `IsLocked`, `IsInUse` write the sync info; `IsLocked` blocks pick-up. `Position`/`Rotation` write the transform; whether this wakes a sleeping rigidbody so clients see the move is UNVERIFIED. After teleporting a resting pickup, wake the rigidbody and check on a client.
- Destroy: the net object disappears from clients immediately; the server object and wrapper go at end of frame, and `IsDestroyed` stays false until then. Resolve again next frame.
- Round restart destroys every pickup and raises `PickupDestroyed` once per pickup.
- `Pickup.ToString()` and `Pickup.Room` resolve the room (coordinate lookup, then raycasts). Do not log pickups in loops.
- `Pickup.Create(gun)` yields a gun with no magazine and default attachments (see Firearms).
- `Category`/`TierFlags`/`ThrowSettings` on an `Item` are per-instance and do not carry to the dropped pickup (the pickup reads the template).

## Projectiles and grenades

- Create live grenades with `TimedGrenadeProjectile.SpawnActive(pos, type, owner, timeOverride)`.
- If you cancel `ProjectileExploding`, the game never retries: its one-shot guard is already set and rewriting `RemainingTime` does not re-arm it. Destroy the grenade, or call `TimedGrenadeProjectile.FuseEnd()` later, which re-raises the event.
- `FuseEnd()` bypasses the one-shot guard. Calling it on a grenade whose time was already reached but not yet processed can detonate twice (UNVERIFIED in play), and calling it early on non-explosive timed grenades can run the end twice (UNVERIFIED).
- Each explosion does an allocating overlap query and wakes nearby sleeping pickups, which then send physics updates. Big blasts amid piles of pickups are expensive.
- Thrown-by-player flow raises `PlayerThrowingProjectile` (cancellable) then `PlayerThrewProjectile`.

## Firearms

- State lives in static dictionaries keyed by serial (magazine, attachments, chamber), so it survives item to pickup transitions and is shared by every wrapper of that serial.
- Reason `AdminCommand` (the `AddItem` default) and `Scp2536` give a loaded, full magazine; `StartingItem` loads from reserve; `PickedUp` leaves existing data. `Pickup.Create` gives no entry in either dictionary.
- To build a loaded, attached gun: `AddItem(type)` (needs a free slot), set `AttachmentsCode` first, then `StoredAmmo` (for example to `MaxAmmo`), then `DropItem()`. Attachments change the magazine maximum; lowering it pushes the excess into the player's reserve, raising it leaves the old fill. Magazine setters assume an owner, so they are for held items.
- `MagazineInserted = false` returns the stored rounds to the player's reserve.
- For the personal-preference reasons (`StartingItem`, `AdminCommand`, `Scp2536`) the game overrides the attachment code with the player's saved preference during item setup, before your setter runs. Your setter then applies on top; read `AttachmentsCode` back to confirm.
- `AttachmentsCode` and `FlashlightEnabled` broadcast to every connection. Batch changes; never set per frame or per player tick. Joining players receive the whole serial-to-code table in one message, which grows with every firearm serial seen.
- There is no damage property on `FirearmItem`. Damage numbers are protected-setter members on the hit-registration module. Options are event-time scaling in `PlayerHurting` or Harmony (scpsl-harmony-patching); which to use is a design choice.
- Per shot the wrapper layer costs two dictionary lookups plus event-args allocation.

## Usable items

- The normal path is: item held, client start message, validation (cooldown, speed, `PlayerUsing` cancellable), completion in a static update, `PlayerItemUsageEffectsApplying`, `PlayerUsedItem`. Changing the held item cancels the use.
- `UsableItem.IsUsing = true` goes through that pipeline but only works for the currently held item and fails silently if a check rejects it.
- `UsableItem.Use()` calls completion directly: no events, no cooldown, no held check, and a consumable is removed at once. Equip and set `IsUsing = true` to get the normal pipeline.

## Wrapper defects to avoid

| Member | Defect | Workaround |
|---|---|---|
| `FirearmItem.BaseWeight` | Returns the base length | Read `Base.BaseWeight` |
| `ThrowableItem.FullThrowRelativePosition` | Reads and writes the start torque | Read the throw settings on `Base` |
| `Player.DropAmmo` | `checkMinimals` defaults to true | Pass false |
| `Player.AddItem(Pickup)`, `Item.MoveTo` | Leave the pickup alive | Destroy it, avoid `MoveTo` |
| `UsableItem.Use()` | Skips the use pipeline | `IsUsing = true` |
| `TimedGrenadeProjectile.FuseEnd()` | Bypasses the one-shot guard | Call once, deliberately |
| `KeycardItem.CreateCustomCard` | Arguments live in static fields of the detail classes; a failed add leaves them for the next custom card, and a reused serial keeps its old snapshot | Check the result, always pass full arguments |
| `ServerEvents.PickupCreated` / `PickupDestroyed` | Created skipped, destroyed may carry null | Tag at the call site, null-check |
| `ToString()` | Resolves the room | Do not log in loops |

## Cost model (estimates, not measurements)

| Operation | Main thread | Network |
|---|---|---|
| `Item.Get(base)` hit | one dictionary lookup | none |
| `Item.Get(ushort)` | closure every call; scan on a miss | none |
| `Player.AddItem` | prefab clone (large for firearms) | firearm: one small broadcast to ALL connections; list: one refresh per player per frame |
| `Pickup.Create` + spawn | instantiate, physics body, spawn message built once per connection | roughly 2-4 microseconds CPU per connection per object (estimate); at 100 players about 0.2-0.4 ms per pickup, so a burst of a few hundred in one frame is tens of milliseconds |
| Awake pickup | physics step plus a physics RPC every 0.25 s to everyone | on the order of 20 KB/s per awake pickup at 100 players (estimate) |
| Sleeping pickup | tens of nanoseconds per frame | none |
| Held items | roughly 1.5-2.5 microseconds per player per frame, firearms dominate (estimate) | none |

Every pickup is visible to every connection, so spawn and awake costs scale with player count. Spread bursts across frames, avoid stacking dozens at one point, and do not leave piles awake.

## Traps (symptom, cause, fix)

| Symptom | Cause | Fix |
|---|---|---|
| Duplicate item after `AddItem(pickup)` | Source pickup not destroyed | `pickup.Destroy()` |
| `PickupCreated` never fires for my pickups | Wrapper created before the start step | Tag at creation |
| Wrapper looks alive but object is gone | Destroy is deferred a frame | Re-resolve next frame |
| `==` on wrappers gives surprising results | Uncached wrappers are re-created per `Get` | Key by serial |
| Tag fires on a different object next round | Serial wrap and no reset | Clear sets at restart |
| Players say "no magazine" on a spawned gun | State is serial-keyed and `Pickup.Create` sets none | `AddItem` then `DropItem` |
| `AddItem` returned null | 8-slot cap or unknown type | Check `IsInventoryFull` |
| `AddItem(ammo)` returns a vanishing item | Ammo converts to reserve | `AddAmmo` |
| Grenade never explodes after a cancel | One-shot guard | Destroy it or call `FuseEnd()` |
| `Death` handler sees an empty inventory | Drop happens before `Death` | Read in `Dying` |
| Spawned pickup vanishes | Out of encodable range while moving | Keep near waypoints |
| Frame spike spawning many pickups | Per-connection spawn serialization | Spread over frames |
| Edits to a prefab wrapper leak into later items | Template wrapper mutates the shared prefab | Do not mutate templates |

## Threading

Items and pickups touch Unity objects, the network server, unlocked dictionaries and a non-atomic counter. Main thread only. Hand other threads copies of `ushort` and `ItemType`. LabAPI offers no marshalling helper for these wrappers; use your own main-thread queue or `SlKit` `MainThread.Post`. Handlers must not assume a wrapper's base object is alive.

## Re-verifying on a new build

- Re-check the serial generator: still a `ushort`, still wraps at 65000, and whether anything now calls its reset. Log `Info.Serial` of a map pickup in `ItemSpawned` and again after round start.
- Check `Player.AddItem(Pickup)` and `Item.MoveTo` still leave the pickup alive, `DropAmmo`'s default, `FirearmItem.BaseWeight` and `ThrowableItem.FullThrowRelativePosition`.
- Check `Pickup.AddPickup` still wraps only if no wrapper exists, and that `Pickup.Create` still defaults `networkSpawn` to true.
- In game: log the `Count` of the serial-keyed game tables across several round restarts to see whether they are ever cleared.
- In game: teleport a sleeping pickup and watch a client. Create a pickup of each item type to find types without a drop model.
