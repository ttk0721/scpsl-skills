---
name: adding-gameplay-rules
description: "Use when adding a gameplay rule to an SCP:SL plugin through cancellable events: friendly fire, damage scaling or immunity, door, elevator, item or pickup restrictions, escape changes; or when a cancel, CanOpen, Damage or NewRole edit has no effect, damage differs from what you set, or two plugins' rules fight."
---

# Adding gameplay rules

Verified against LabAPI 1.1.7 / SCP:SL 14.2.7 (2026-10).

## Overview

A rule is a handler on one cancellable `-ing` event that **cancels** or **edits a field the game
reads back**. Pick the event that carries the decision, write only setters the game honours, and
use only cancel and multiply, which do not depend on plugin order.

## When to Use

Who hurts whom and how much, who opens which door, rides which elevator, picks up which item,
escapes into which role. Not for timers or HUDs (scpsl-main-thread-and-timing, scpsl-hints-and-hud),
role and spawn logic (scpsl-roles-damage-effects), or behaviour with no event (scpsl-harmony-patching).

**Size the process to the task.** One cancel or one multiplier: look the event up, write the
handler, stop. No config tables, rewiring or counters. Several rules, an owner-edited config or a
hot event: the full pattern.

## Decisions

| Decision | Choose | Because |
|---|---|---|
| Deny | `if (deny) ev.IsAllowed = false;` | `= !deny` un-cancels other plugins |
| Reshape damage | `Hurting`: cast, `h.Damage *= k` | `ev.DamageHandler = x` is ignored; damage is raw, modifiers run after |
| Friendly fire | Compare factions in `Hurting` | `IsFriendlyFire` is still false there |
| Grant a door | `CanOpen = true` only if `!Door.IsLocked` | `CanOpen` is also the lock verdict |
| Elevator | Write `false` only | The game pre-sets `false` when locked or moving |
| Pickup | `PickingUpItem` | `SearchingPickup` also fires for 914 and escape returns |
| Escape | First line: `EscapeScenario == None` return | `Escaping` is raised every frame per player in the zone |
| Ordering | Cancel and multiply, never assign | Last writer wins; `Lowest` priority runs last |

Setters: `rules-reference.md`. Frequency: `events-catalog.md`. Final damage: `Hurt`.

## Pattern

1. Catalog lookup: frequency, honoured setters, `pair`.
2. Early-outs first: `IsAllowed`, handler type, `Attacker == null`, instakill, role out of range.
3. Decide with array or set lookups. No LINQ, `Player.List` or logging.
4. Write one thing: `IsAllowed = false` or a multiplied field.
5. Hint through a per-player cooldown, cleared at `WaitingForPlayers`.
6. Subscribe only while the rule is on; a config reload re-wires; kill switch off means unsubscribed.
7. Own try/catch with `RateLimitedLog.Error`: a fault in `Hurting` repeats per hit.

```csharp
if (!ev.IsAllowed || !(ev.DamageHandler is AttackerDamageHandler h) || h.Damage <= 0f) return;
Player attacker = ev.Attacker;                        // null: hub gone
if (attacker == null || attacker.PlayerId == ev.Player.PlayerId) return;
Faction f = attacker.Faction;                         // SCP, Tutorial, dead: not ours to judge
if (f == ev.Player.Faction && (f == Faction.FoundationStaff || f == Faction.FoundationEnemy))
{
    if (t.Block) { ev.IsAllowed = false; return; }
    h.Damage *= t.Scale;                              // same object: honoured
}
```

## The example

`examples/adding-gameplay-rules/` builds with 0 errors; the linter gives 0 errors and one SL001 warning, the deliberate `Hurting` subscription (conditionally hot, gated by an early return):

- `RulesPlugin.cs`: five handlers; `Wire()` reconciles subscriptions with config on `Enable()` and each reload; the hot `Escaping` subscription carries a written SL001 suppression; counters via an SlKit clock job; `Shutdown` cleanup.
- `RuleTables.cs`: config compiled to arrays by role slot, swapped as one reference.
- `RulesConfig.cs`: every rule off by default, master kill switch, clamped multipliers.

## Common Mistakes

- **Cancel, then `door.IsOpened`, to open a locked door.** Set `ev.CanOpen = true`. On a locked door a cancel plays denial feedback and fires `InteractedDoor(false)`.
- **`ev.DamageHandler = new ...`.** Ignored; mutate the handler (lint SL003).
- **Cancelling `Dying`.** Damage is already applied, HP stays at or below 0. Cancel `Hurting`.
- **`IsAllowed = true` on an elevator or door.** Force-sends the elevator; un-cancels others.
- **`Escaping` ungated or logging.** A cancel repeats every frame.
- **"Only a lookup, so free"**. The game still builds args and a `Delegate[]` per raise.
- **Inventing an event** like `UsedElevator`. Use `InteractingElevator`/`InteractedElevator`.
- **Null `Config`; `RequiredApiVersion` from `CompiledVersion`**. `Config ??=`; `new Version(1, 1, 7)`.
- **Role-keyed rule that skips SCPs, Tutorial, dummies.** Name them.
- **Trusting `Lowest` priority to win.** Multiply or cancel instead.
- **No off switch, no counter.**
- **Starting a server to check the rule uninvited.** Ask first; never use the owner's ports (running-a-test-server).

## Domain skills

labapi-events (mechanics, hot gates), scpsl-roles-damage-effects (damage pipeline, teams),
labapi-wrappers (`Door`, `Pickup`), scpsl-game-lifecycle, designing-scpsl-plugins, reviewing-scpsl-plugins,
running-a-test-server and testing-scpsl-plugins (only if the owner wants a local test: ask, own port).
