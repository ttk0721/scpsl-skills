---
name: labapi-wrappers
description: "Use when plugin code calls LabAPI wrapper types (Player, Server, Round, Item, Pickup, Room, Door, Elevator, Generator, Warhead, Announcer, AdminToy, SpeakerToy, CustomDataStore, permissions), or when a wrapper throws NullReferenceException or MissingReferenceException, a lookup is slow, a setter silently does nothing, or state vanishes after a round restart."
---

# LabAPI Wrappers

Verified against LabAPI 1.1.7 / SCP:SL 14.2.7 (2026-10).

## Overview

A wrapper is a thin shell over a Unity object and dies with it. Hold a stable key, re-resolve, treat every setter as a possible network message.

## When to Use

Reading or writing players, items, pickups, map objects, toys, audio, stores or permissions. Not for events (labapi-events), hints (scpsl-hints-and-hud), timers and threads (scpsl-main-thread-and-timing), restarts (scpsl-game-lifecycle), roles and damage (scpsl-roles-damage-effects), voice (scpsl-voice-and-audio).

## Lifetime and lookup rules

| Wrapper | Dies | Stable key |
|---|---|---|
| Player | after `Left` fires (`UserId`/`Nickname` readable inside `Left`; `IpAddress` may throw) | `UserId`; NPCs `NetworkId`; never `PlayerId` alone (recycled) |
| Item, Pickup | end of frame after removal | `(kind, serial)` |
| Room, Door, Elevator, Generator, Tesla, Hazard, lights | every round restart (scene reload) | name + zone + shape, re-resolved at `WaitingForPlayers` |
| `CustomDataStore` | leave; expect each restart | n/a |

1. Never keep a wrapper across leave, round restart or a frame you do not control. Test with `IsDestroyed` or re-resolve by key.
2. `Player.Get(hub|GameObject|netId)` and a `Get(string)` cache hit are O(1) but hash natively; per-pair and per-frame code uses `ReferenceHub`. `Get(int)`, `GetByName*`, `Item.Get(ushort)` are linear and allocate.
3. `Get` returns null for a destroyed object. Null-check it.
4. `ReadyList`, `GetAll`, `Count`, `NpcList` are lazy LINQ over the live dictionary: no snapshot. Snapshot before any `yield`, spawn or lazy `Get`; read `Count` once per tick.
5. In the frame an item or pickup is destroyed, `IsDestroyed` is still false.
6. Map wrappers are incomplete at `MapGenerated`. Use `WaitingForPlayers`; retry a frame later if a list is empty.
7. Main thread only. Copy plain values before handing data to another thread.

## Cost and traffic

| Kind | Members |
|---|---|
| One uncoalesced message per call | `Position`, `LookRotation`, `Scale`, `Role` (to every hub), `IsDisarmed`, `UserGroup`, `SendConsoleMessage`, `SendHint` |
| Deferred, one per frame | `Health`, `MaxHealth`, `ArtificialHealth`, items, ammo, SyncVars |
| Broadcast to every connection | firearm `AttachmentsCode`, `FlashlightEnabled`, each spawn, each audio frame |
| Expensive getters | `Player.Room`, `Room.Players`, `Player.Items`, `Door.Rooms`, `Pickup.ToString()`, `IpAddress`, `Announcer.IsValid` |
| Synchronous disk I/O | `Ban`, `Mute(false)`, `Whitelist`, `ReservedSlots`, `AddPermissions` |

`Position`/`Scale` no-op for non-first-person roles (`RoleBase is IFpcRole`). Aim with `LookRotation`.

Details: players-and-server.md, items-and-pickups.md (serials, pickups, firearms), facility.md (map, Cassie, waves, warhead, 914), toys-audio-stores-permissions.md.

## Compiled examples

`examples/labapi-wrappers-snippets/` (builds, lints clean): ten operations with costs, one class each, from `PlayerLookup.cs` (lookup by `UserId`) to `Messaging.cs`. Key parts in `wrapper-examples.md`.

## Common Mistakes

| Wrong | Right |
|---|---|
| "Serials restart each round." | The `ushort` counter wraps after 65000 and is never reset. Serials are unique per kind (Item vs Pickup) only. Clear serial sets at round boundaries; stale serial-keyed state can give false hits. |
| `Pickup.Create(type, pos)` then `pk.Spawn()` | `Create` already spawns (`networkSpawn: true`). A second `Spawn` only logs a warning. |
| A 60 s sweep dropping serials not found in `Player.Items` or `Pickup.List` | A death-drop pickup gets its wrapper a step later, so the sweep erases a live tagged item. Clear at round boundaries; prune only when neither `Item.Get` nor `Pickup.Get` resolves a frame later. |
| "`ReadyList` is safe because LabAPI snapshots it." | No snapshot. A synchronous `foreach` survives only because hub start is engine-scheduled, destroy is deferred and `Kick` completes later. A lazy `Player.Get` on a fresh dummy in the loop can throw. |
| "A leaver's items drop as pickups." | Disconnect removes held items with no pickup. Remove the serial in `Left`, or dead serials count toward your caps. Only a leaver killed first by `disconnect_drop` leaves a death drop. |
| `AddItem(pickup)` and done | The source pickup stays: destroy it after a non-null result. |
| `player.ReferenceHub == null` in a delayed closure | `IsDestroyed`, or re-resolve by `UserId`. Better, do not capture the `Player`. |
| `Health = 0` to kill, `IsLocked = false` to unlock, `Engaged = true` to finish a generator | Each does less than it looks like. See the reference files. |
