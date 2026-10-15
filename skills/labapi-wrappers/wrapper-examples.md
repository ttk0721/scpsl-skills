# Wrapper examples: ten operations done right

Verified against LabAPI 1.1.7 / SCP:SL 14.2.7 (2026-10). Behaviour is from reading the code, not from a running server, unless noted. Cost figures are estimates.

Complete project: `examples/labapi-wrappers-snippets/` (builds against the game assemblies, lints clean). Each class below is one operation; `WrapperSnippetsPlugin.cs` shows the wiring (per-round reset at `WaitingForPlayers`, per-player cleanup on `Left`, one clock for budgeted work).

| # | Operation | File | The cost or trap it handles |
|---|---|---|---|
| 1 | Find a player by `UserId` | `PlayerLookup.cs` | `Get(string)` miss is O(N); dummies share `ID_Dummy`; name lookups sort and throw |
| 2 | Iterate players | `PlayerSweeps.cs` | no snapshot; `Count` is a full filter and includes dummies |
| 3 | Give an item | `ItemGiving.cs` (`ItemGiver`) | `AddItem` returns null at 8 items; serials wrap; leavers drop nothing |
| 4 | Give or drop ammo | `ItemGiving.cs` (`AmmoOps`) | `Ammo` dictionary writes never reach the client; `DropAmmo` rounds up |
| 5 | Spawn and move pickups | `PickupOps.cs` | `Create` already spawns; `AddItem(pickup)` leaves a duplicate; spawn cost scales with players |
| 6 | Doors | `DoorOps.cs`, `FacilityIndex.cs` | `IsLocked = false` clears one bit; `Door.List` holds sub-doors; named doors are few |
| 7 | Rooms | `RoomOps.cs`, `FacilityIndex.cs` | `Player.Room` is uncached; `RoomName` is not unique; coordinates change per seed |
| 8 | Teleport and aim | `Teleporting.cs` | setters are no-ops for non-first-person roles; `Rotation` pitch is wrong |
| 9 | Status effects | `StatusEffects.cs` | duration 0 never expires; string lookup is linear |
| 10 | Broadcasts, hints, console | `Messaging.cs` | host throws on hints; one message per call; resend of equal text |

## 1. Find a player

Key on `UserId`, resolve on use, refuse everything that is not an authenticated human:

```csharp
if (!Player.TryGet(userId, out Player player)) return null;
if (player.IsDestroyed || !player.IsPlayer || !player.IsReady) return null;
return player;
```

`IsDestroyed` is a cheap managed check, so it goes first. `IsPlayer` is what stops a stored key resolving to "some dummy". A cache hit is O(1); a miss, and any offline id, scans every player because negative results are never cached. For many lookups per tick keep a `Dictionary<string, PlayerSession>` filled from `Joined` and emptied on `Left` (`SessionIndex`). Prefix search filters `ReadyList` itself instead of `GetByName`.

## 2. Iterate players

```csharp
_humans.Clear();                       // reused List<Player>
foreach (Player p in Player.List)      // live view, one boxed enumerator
    if (p.IsPlayer && p.IsReady) _humans.Add(p);
return _humans;                        // iterate by index; safe across joins and leaves
```

Copy into a buffer when the body can yield, spawn or call `Player.Get` on a fresh hub. A synchronous read-only `foreach` needs no copy. `Player.GetAll(...)` is a lazy iterator, so `ToArray()` is the snapshot. Never enumerate per frame (scpsl-lint SL011): poll at 1-5 Hz.

## 3. Give an item

```csharp
if (IsAmmo(type) || player.IsInventoryFull) return null;
Item item = player.AddItem(type);      // reason defaults to AdminCommand
if (item == null || item.Serial == 0) return null;
_tagged.Add(item.Serial);
```

`AddItem` returns null at 8 non-ammo items or for an unknown type, and throws before the server is active. Category limits are skipped on this path. Eight gives in one frame are one inventory message; a firearm also broadcasts a small message to every connection. Tag by serial, never persist serials, `Clear()` at `WaitingForPlayers`, and drop a leaver's serials from `Left` (disconnect removes held items with no pickup). Whether the inventory is still readable inside `Left` is UNVERIFIED; the round-boundary clear is the backstop.

## 4. Ammo

`AddAmmo`/`SetAmmo` are the only supported writes. `DropAmmo(type, n, checkMinimals: false)` drops `n`; the default raises small requests to about half a pickup.

## 5. Pickups

```csharp
Pickup pickup = Pickup.Create(type, pos);          // already network-spawned; no Spawn()
...
Item item = player.AddItem(pickup);                // does NOT destroy the source
if (item != null && pickup.Type != ItemType.SCP330 && !(pickup is AmmoPickup))
    pickup.Destroy();                              // gone on clients now, wrapper next frame
```

Configure before spawn with `networkSpawn: false`, then `Spawn()` once and `Destroy()` on any failure. A gun from `Create` has no magazine and default attachments, so `AddItem(gun)`, set `AttachmentsCode`, then `StoredAmmo = MaxAmmo`, then `DropItem()` (serial-keyed state travels with the serial). `PickupBurst.Pump(budget)` spreads a mass spawn across clock ticks; each pickup is serialised once per connection.

## 6. Doors

Build `FacilityIndex` at `WaitingForPlayers` (rooms by name, doors by room, sub-doors excluded). `Lock(DoorLockReason.AdminCommand, true/false)` touches one bit; read `LockReason` to see what still holds the door after you unlock. `Break` uses `TryDamage(float.MaxValue, ...)` and checks the result. `IsOpened = true` is deliberately absent: it ignores locks and permissions.

## 7. Rooms

`Player.CachedRoom` per tick (null for dead players); one pass for a headcount (`CountByRoom`), never `Room.Players` per room. `PickUnique(name, zone)` returns null for zero or many matches. Store `room.Transform.InverseTransformPoint(world)`, rebuild the room each round.

## 8. Teleport and aim

```csharp
if (!(player.RoleBase is IFpcRole)) return false;   // setter would silently do nothing
player.Position = target;
player.LookRotation = new Vector2(Mathf.Clamp(pitch, -88f, 88f), yaw);
```

Each write is one uncoalesced message to that client. Use `LookRotation`, not `Rotation`.

## 9. Effects

`player.EnableEffect<Slowness>(intensity, seconds, addDuration)`; `HasEffect<T>()`, `TryGetEffect<T>()`, `DisableEffect<T>()`. Duration 0 never expires. Prefer generics over `TryGetEffect(string)`, a linear scan.

## 10. Messages

`Server.SendBroadcast` is one RPC to everyone. Targeted sends go to ready humans only, never the host (`SendHint` throws for it). `Messaging.Hint` skips equal text; `Console` joins lines into one message. For HUD output see scpsl-hints-and-hud.

## Not covered here

Generators, elevators, warhead, waves, Cassie and 914 (facility.md); toys, audio, stores, permissions (toys-audio-stores-permissions.md); roles and damage (scpsl-roles-damage-effects).

## Re-verifying on a new build

- Rebuild the project against the new `LabApi.dll`; a changed signature is a compile error here.
- Re-check `Player.TryGet(string)` caching, `AddItem(Pickup)` still leaving the source alive, `Pickup.Create`'s `networkSpawn` default, `DropAmmo`'s `checkMinimals` default, `Door.IsLocked`'s setter bit and the `Position` setter's first-person guard against the LabAPI source.
- In game: read the inventory inside `Left`, and check `Door.Get(DoorName)` right after `WaitingForPlayers`.
