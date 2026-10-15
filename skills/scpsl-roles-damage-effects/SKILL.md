---
name: scpsl-roles-damage-effects
description: "Use when an SCP:SL LabAPI plugin sets roles, spawns or teleports players, changes health/AHP/hume/stamina, intercepts damage or kills, applies status effects, handles ragdolls, or hooks ChangingRole, Spawning, Hurting, Dying, UpdatingEffect; or when stats vanish after SetRole, Kill() does nothing, lag spikes at round start, warhead or waves."
---

# Roles, Damage, Effects

Verified against LabAPI 1.1.7 / SCP:SL 14.2.7 (2026-10).

## Overview

The game rebuilds a player on every role change and runs damage through one pipeline. Hook the
right step, mutate what the game actually reads back, and keep handlers O(1): mass events run
them for every player in one frame.

## When to Use

Role changes, spawn positions, loadouts, HP/AHP/hume/stamina, damage scaling, kills, effects,
ragdolls, round-start/wave/warhead spikes. Not for hints (scpsl-hints-and-hud), event frequency
(labapi-events), visibility filtering wire format (scpsl-networking-for-plugins).

## Quick Reference

| Need | Do | Never |
|---|---|---|
| Swap a role | `ChangingRole`: mutate `NewRole`/`ChangeReason`/`SpawnFlags` | `SetRole` same player from `ChangingRole`/`Spawning`/`Spawned`; cancel it |
| Stats, effects, items of new role | `ChangedRole` (valid only from there) | Earlier events: reset wipes them |
| Spawn position | `Spawning.SetSpawnpoint` | `Player.Position` before `ChangedRole` |
| Change damage | `Hurting`: cast, mutate the SAME handler's `Damage` | `e.DamageHandler = x` (dead setter) |
| Final damage numbers | `Hurt` | Trust `Hurting` (raw, pre-modifier) |
| Kill | `Kill(reason, cassie)` / `Damage(handler)`, check bool, `IsAlive` first | `Health = 0`; trusting no-arg `Kill()` (returns void) |
| Cancel death | Cancel `Hurting`; if `Dying`, restore HP too | Cancel `Dying` and leave 0 HP |
| AHP | `ArtificialHealth` after `ChangedRole`, raise max first | `AhpStat.CurValue` |
| Effect | `EnableEffect<T>(intensity, duration)`, null-check `GetEffect<T>()` | `duration: 0` by accident; default intensity on a stronger effect |
| Corpse | `SilentDamageHandler`, cap and `Destroy()` ragdolls | Assume server ragdolls expire |

Gotchas that bite first: god mode and `SpawnProtected` return from damage with NO event; `Hurting`
sees raw damage; stamina is 0 to 1; holiday roles/effects silently become `None`/null; role
reason and flags have side effects (see roles-and-spawning.md).

```csharp
private static void OnHurting(PlayerHurtingEventArgs e)
{
    if (e.DamageHandler is not StandardDamageHandler h) return;  // cheap filter first
    h.Damage *= Config.Multiplier;                                // same object: honoured
}
```

## Reference Files

- roles-and-spawning.md: flow of one `ServerSetRole`, reasons, flags, spawnpoints, teams, role-keyed filters.
- damage-and-kills.md: `DealDamage` pipeline, interception, kill API, attribution, DoT.
- stats-and-effects.md: stat semantics and wire cost, effect rules, reset on role change.
- ragdolls-and-mass-events.md: ragdoll lifecycle, mass-event costs, queue-and-stagger pattern.

Mass events (round start, waves, warhead, decontamination) raise your handlers per player in one
frame. Queue heavy work and spread it. **REQUIRED SUB-SKILL:** scpsl-main-thread-and-timing. Hot
event gating: labapi-events. Lint: scpsl-lint SL003 (dead setters), SL005, SL011.

## Common Mistakes

- **Filtering every alive FPC role** in a visibility/anti-ESP style filter, exempting only 079,
  Overwatch, Filmmaker and the dead. SCP-173 observation, 096 targeting, 049 Sense, zombie
  bloodlust and 1344 run on server-side raycast/visibility checks keyed on the target. Hiding an
  SCP they consider visible freezes 173 or makes an honest player a 096 target while his client
  shows nothing. Exempt all non-`HumanRole` targets, noclip targets and moving-elevator/no-room
  targets; leave receivers 939, 096 Distressed/Enraged, 106 stalk and dummies untouched; keep a 049
  Sense target visible while `HasTarget`.
- Setting HP/effects in `Spawned`; they are reset afterwards. Use `ChangedRole`.
- `Player.Kill()` return value, `Health = 0`, `e.DamageHandler = ...`: see Quick Reference.
- Skip host, dummies and Spectator/Overwatch/079/`None` in role handlers; re-baseline stored
  positions on `ChangedRole`. Cache the role there: `Left` still reads it (scpsl-game-lifecycle).
- Per-player scans, `Logger` calls or `SetRole` inside mass-event handlers; wrap handler bodies in
  try/catch (reviewing-scpsl-plugins).

## Red Flags

- A coroutine or `Player.List` scan inside `ChangedRole`, `Hurting` or `UpdatingEffect`.
- `SetRole` called from a role/spawn event handler.
- `EnableEffect` with no duration argument.
- A role-keyed filter that does not name SCPs, noclip or elevators.
