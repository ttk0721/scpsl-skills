---
name: porting-exiled-to-labapi
description: "Use when porting or replacing an EXILED plugin with pure LabAPI, when an EXILED event, Player/Server/Map/Round member, CustomItems, CustomRoles, Permissions or translation has no obvious LabAPI counterpart, when a ported handler fires at a different moment or its cancel is ignored, when you need an EXILED-to-LabAPI before/after example, or when asked what EXILED costs on a server."
---

# Porting EXILED Plugins to LabAPI

Verified against LabAPI 1.1.7 / SCP:SL 14.2.7 (2026-10).

## Overview

Porting is hook re-derivation, not renaming. EXILED raises its events from its own Harmony patches at its own point inside a game method; LabAPI raises its events inline at another point. Same name, different moment, different cancel effect.

## When to Use

- Moving an EXILED plugin to LabAPI, or deciding whether to keep EXILED installed.
- A ported plugin compiles but misbehaves: items vanish, cancel ignored, `Left` counters wrong, lookups null.
- Not for new plugins (designing-scpsl-plugins) or choosing events (labapi-events).
- A one-event port: use the tables, skip the procedure.
- Adding a feature that merely existed in EXILED: use the recipe skill for that feature type (adding-custom-items, adding-custom-roles, adding-gameplay-rules).

## Procedure

1. List every EXILED event and member the plugin uses.
2. Look each up in `mapping.md`. Rows marked TIMING need the timing re-read, not a rename.
3. Common cases have a before/after pair in `pairs.md` (LabAPI side compiled, EXILED side illustrative): copy its shape, then check the timing row.
4. No LabAPI event: use a game-native C# event (no per-handler try/catch: wrap every body), else your own Harmony patch (scpsl-harmony-patching).
5. EXILED-only features (CustomItems, CustomRoles, permissions, translations) go through `replacements.md`.
6. Put each behaviour on one layer only. Compile against LabAPI alone, test with EXILED loaded and unloaded.
7. Test join, late join, leave, role change, round restart. Run scpsl-lint. Live tests only if the owner wants them: ask first, never touch their ports (running-a-test-server).

## Pairs

`pairs.md` holds eight EXILED-and-LabAPI pairs: subscribe, join and leave, messages, loops to SlClock, player iteration, damage, config and permissions, loadout. The LabAPI side is compiled in `examples/porting-exiled-pairs/`. The riskiest one, inline:

```csharp
// EXILED: ev.Amount *= 0.5f;   LabAPI: mutate the same handler, never assign ev.DamageHandler
StandardDamageHandler h = ev.DamageHandler as StandardDamageHandler;
if (h == null) return;
if (ev.Attacker != null && ev.Attacker.Faction == ev.Player.Faction) h.Damage *= 0.5f;
```

## Quick Reference

| EXILED | LabAPI | Catch |
|---|---|---|
| `Verified` | `Joined` | Later (after badges). Skipped on badge mismatch. |
| `Destroying` | `Left` | Fires for unauthenticated hubs, NPCs, dummies. |
| `Joined`, `Left` | none | Hub-start and network-disconnect moments. |
| `Spawned` | `ChangedRole` / `ReceivedLoadout` | LabAPI `Spawned` precedes the loadout. |
| `Hurting` | `Hurting` | Runs after spawn-protect; setter ignored. |
| `Dying` cancel | `Dying` cancel | Leaves 0 HP: restore `Health`. |
| `Player.Get(string)` | `Get(userId)` | Name lookup is gone, not renamed. |
| `Broadcast(dur, msg)` | `SendBroadcast(msg, dur)` | Argument order swapped. |
| `Mute(bool)` | `Mute(bool)` | Bool is now `isTemporary`, default true. |

## Rules

- Compare the EXILED patch point and the LabAPI fire site inside the same game method.
- `CassieAnnouncing`, `SearchedAmmo`, `SearchedArmor` are never raised; `AimingWeapon` does not exist.
- Cancel with `IsAllowed = false` only. Handlers do not short-circuit; the last writer wins.
- Do inventory work in `ReceivingLoadout`, never in `Spawned`.
- Do not call `SetRole` inside `ChangingRole`; change `ev.NewRole`.
- Flush state on `ServerEvents.Shutdown`; LabAPI never calls `Disable()`.

## Common Mistakes

- **Wrong:** assuming `Joined` fires when the hub appears, and adding a welcome delay. **Right:** `Joined` fires at the end of authentication, so `IsReady` is already true. It is skipped on badge mismatch (a serial mismatch disconnects the player; a UserID or nickname mismatch leaves them connected without `Joined`).
- **Wrong:** guarding `Left` with `IsReady`. **Right:** a set filled in `Joined`.
- **Wrong:** treating a late joiner's role events as a ready `Player`. **Right:** after round start the role is assigned before `Joined`: `IsReady` false, `Nickname` `"(null)"`.
- **Wrong:** renaming `Mute()` and `Player.Get("name")`. **Right:** re-read each in `mapping.md`.
- **Wrong:** porting a MEC loop as a coroutine. **Right:** `SlClock`; idle mode's `timeScale` 0.01 stretches Update-segment waits.
- **Wrong:** installing `Exiled.CustomItems` for one feature. **Right:** its disable removes every plugin's Harmony patches; use a serial dictionary.
- Round restart raises `Left` for every hub: scpsl-game-lifecycle.

Reference files: `pairs.md` (examples), `mapping.md` (events, members), `replacements.md` (no-counterpart features, EXILED cost).
