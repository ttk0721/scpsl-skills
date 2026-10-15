---
name: adding-map-automation
description: "Use when adding round-scoped map scripting to an SCP:SL LabAPI plugin: opening, locking or breaking doors, elevator, tesla or light control, warhead or decontamination scripting, spawning AdminToys (markers, lights, props), or when rooms and doors are null or empty, toys lag joiners, or map changes vanish after a round restart."
---

# Adding Map Automation

Verified against LabAPI 1.1.7 / SCP:SL 14.2.7 (2026-10).

## Overview

The facility is a scene rebuilt every round, and it registers in stages. Resolve it once per round when ready, apply overrides per round, drop every reference at the boundary, and keep spawned objects few, static and spawned in the lobby.

## When to Use

- A plugin scripts doors, elevators, tesla, lights, warhead, decontamination, or spawns toys.
- Not for door events (labapi-events) or hints (scpsl-hints-and-hud).
- **Proportionality:** one door lock or one light colour is one table row plus the facts in labapi-wrappers. Write it; skip the pattern, config and tests.
- Ask first whether the map has schematics (MER) or teleporters: they add identities and move players without LabAPI events.

## Decisions

| Decision | Default |
|---|---|
| Resolve | `WaitingForPlayers`. `MapGenerated` has `Room.List` only. If a `Start`-registered list (`Elevator.List`, `LightsController.List`, `Tesla.List`, a named door) is empty or stale, rebuild ~100 ms later, bounded |
| Apply | Gameplay at `RoundStarted`, undo on `SlClock.After` (round scope) |
| Find a door | `Door.Get(DoorName)` (38 names), else `room.Doors`. Not `Door.Get(Zone\|Room)` |
| Lock | `Lock(AdminCommand, true)`, clear only that bit. `IsOpened` ignores locks |
| Let one player through | `InteractingDoor.CanOpen = true` |
| Elevator | `SetDestination(level, true)` (queues). `false` makes a moving cabin jump |
| Tesla off | `InactiveTime = float.MaxValue`, later `0` (finite values churn the SyncVar) |
| Lights | Colour override, or `FlickerLights(float.MaxValue)`/`(0)`; game effects overwrite blackouts |
| Warhead, decon | Check state first (`IsDetonationInProgress`, `DetonationTime > 10`, `Status`) |
| Toys | `Create(networkSpawn: false)`, configure, `IsStatic = true`, `Spawn()` |
| Toy budget | Dozens, not thousands, in lobby, in byte-budgeted slices |
| Parenting | To an already-spawned toy, passed to `Create`. Never to a room |
| Moving toys | Not per frame; move a static group by moving its parent |
| Cleanup | Drop references; the scene reload destroys the objects |
| Kill switch | `Config.IsEnabled` read at use; stops new work, not applied state |

## The pattern

1. Config: `IsEnabled` plus a toggle per feature (`Config` can be null).
2. `Enable()`: idempotent, fallible work first, subscribe last.
3. `WaitingForPlayers`: reset, build one `MapIndex` of plain lists, retry if incomplete, spawn toys.
4. `RoundStarted`: apply, schedule the undo.
5. `RoundRestarted` and `RoundRestart.OnRestartTriggered` (softrestart skips the first): dispose jobs, null the index. Same method on `Shutdown`.

## The example

[examples/adding-map-automation](../../examples/adding-map-automation/AutomationPlugin.cs): sealed doors, tesla grace, parked elevators, red warhead alert, room markers (`AutomationPlugin.cs`, `MapIndex.cs`, `ToySpawner.cs`, `AutomationConfig.cs`).

Key parts: `MapIndex.Build` and the bounded retry in `TryBuildMap`; `ToySpawner.Tick` (slice = budget / (toys x 134 B x players)); `ResetRound` (idempotent, four callers).

## Common Mistakes

| Wrong | Right |
|---|---|
| Door or light work in `MapGenerated` | Wrappers incomplete; `WaitingForPlayers` |
| Wrappers cached across rounds | New index per round |
| Locked door opened by cancel plus `IsOpened` | `ev.CanOpen = true`; a cancel shows others a denial |
| `Config.X` unguarded | Empty file gives null |
| `Create(...)` then `.Spawn()` | Create already spawns unless `networkSpawn: false` |
| `Room.Get(name).First()` | Names repeat (`HczTesla`); handle 0, 1, many; use `CachedRoom`, not per-tick `Player.Room` |
| World coordinates in code | Room-local offsets; the seed changes layout |
| Room hops as line of sight | Cells are 15 m; see scpsl-networking-for-plugins |
| Generator `Engaged = true` as "finished" | Flag only; no events or reward |
| Toys at `RoundStarted`, or one per tile | Spike on everyone; ~134 B per toy per player, held all round |
| Animated `Color`/`Type`; static toy moved then re-frozen next frame | One Material per colour, never freed; wait for send and smoothing |
| Automating without asking what the map holds | Ask about schematics and teleporters |

## Testing

Build and lint prove types only; door, tesla, light and toy behaviour needs a running server, and the owner may have none or not want one. Ask first, never start one unasked, never touch their ports; protocol, default paths and own-instance setup are in verifying-scpsl-work. List what you could not check.

## Related

labapi-wrappers (facility.md, toys-audio-stores-permissions.md), scpsl-game-lifecycle, scpsl-networking-for-plugins (toys-and-spawning.md), scpsl-main-thread-and-timing, labapi-events, designing-scpsl-plugins, verifying-scpsl-work.
