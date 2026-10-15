---
name: adding-player-abilities
description: "Use when a player should trigger a power by pressing a key on their own client (dash, heal, scan, summon, toggle, class skill) through Server-Specific Settings keybinds in a LabAPI plugin, or when ability keys do nothing, fire twice, ignore cooldowns, kick the player or collide with another plugin's settings."
---

# Adding Player Abilities

Verified against LabAPI 1.1.7 / SCP:SL 14.2.7 (2026-10).

## Overview

A key press is client data: no wrapper, no rate limit, no check the player was ever sent the entry. Pattern: one keybind entry per ability, one guarded handler, wall-clock cooldowns keyed by `UserId`, short hints, cleanup on `Shutdown`.

## When to Use

- A player-initiated power on a key: heal, dash, speed burst, scan, summon, toggle.
- Symptoms: key dead, fires on release, cooldown resets on rejoin, kick on press, other plugin's settings vanished.

Not for: commands (scpsl-commands-and-permissions), powers triggered by using an item (adding-custom-items), always-on effects (adding-gameplay-rules), settings that only tune values (scpsl-server-specific-settings).

**Proportionality.** One more ability in a plugin that already has this shape is path A: add one entry, run the guard chain, build. No brainstorming. A first ability is path B. Ask only what flips the design: who else writes hints or owns SSS on that server, which permission plugin, cooldown per round or per life, peak players.

## Decisions

| Decision | Default | Otherwise |
|---|---|---|
| Control | `SSKeybindSetting`, act on press edge | Hold-style (sprint): `SyncIsPressed` as a level. No key wanted: `SSButton` |
| Ids | FNV-1a hash of `"plugin:key"` | Never `null`, never a small enum |
| `DefinedSettings` | Append, remove only your own; leave `SendOnJoinFilter` and `Version` alone | Registry plugin present: register through it |
| Recipients | Join hook, plus `SendToPlayer` for connected ready players | Staff-only entries still need handler checks |
| Permission | `HasPermissions("plugin.x")` | Game flags: `CheckPermission` |
| Cooldown clock | Stopwatch ticks (SlKit `StopwatchTime`) | Never `Time.time` or MEC Update waits |
| Cooldown scope | Per round by `UserId` | Per life: clear on `ChangedRole` |
| Feedback | Short hints; `TimespanHintParameter` countdown; `SSKeybindHintParameter` key name | Another plugin owns the HUD: submit to it (adding-player-hud) |
| "Ready again" | One `SlClock.Every` job over a list | Never a coroutine per use |

## Pattern: the guard chain

Every press passes these in order, inside one try/catch:

1. Ours: `setting is SSKeybindSetting` and id in your table.
2. `SyncIsPressed` (release also arrives).
3. Resolve `Player`; skip host, dummy, destroyed.
4. Per-player token bucket.
5. Permission.
6. Role: `IsAlive`, SCP rule. Client-side spectator gating is advisory.
7. Cooldown.
8. Apply the effect. Only on success start the cooldown and queue the ready notice.

```csharp
if (!(setting is SSKeybindSetting key) || !_byId.TryGetValue(key.SettingId, out Ability ability)) return;
if (!key.SyncIsPressed) return;
// resolve player, state.Input.TryTake(now), permission, role, remaining cooldown ...
if (!ability.Apply(player, config, out string refusal)) { Deny(player, state, now, config, refusal); return; }
long readyAt = AbilityCooldowns.Start(state, ability.Index, now, ability.CooldownSeconds(config));
```

## The example

`examples/adding-player-abilities/` (`Examples.AddingPlayerAbilities`, builds and lints clean):

- `AbilitiesPlugin.cs`: lifecycle, handler, ready-notice job.
- `AbilitySettings.cs`: merge, collision and 255 checks, send, `Withdraw`.
- `AbilityCooldowns.cs`: pure logic, plain-.NET testable.
- `Abilities.cs`: abilities as data, hashed ids.

Variants (hold, toggle, charges, staff-only views, retuning): abilities-variants.md.

## Testing

A dummy has no client, so only a real client proves a key press. Unit-test `AbilityCooldowns` (shape: `examples/testing-pattern`), build, lint. Before any server run, ASK: is there a dedicated server, do they want local tests at all, is it at the default path (paths, private check, ports: verifying-scpsl-work, test-server.md)? Never touch the owner's ports (7777, 7778): offer a private instance on an unused port (e.g. 9999) and ask which they want. Otherwise report "not tried with a real client".

## Common Mistakes

| Wrong | Right |
|---|---|
| `DefinedSettings = mine` | Append; remove only your instances |
| `switch (SettingId)` on enum or `null` ids | Hashed id; ownership and type first |
| Acting on release too | `SyncIsPressed` gate |
| Unguarded handler | try/catch: an escape disconnects the presser |
| `HasPermissions(PlayerPermissions.X)` (no such overload) | String permission, or `CheckPermission` |
| Cooldown on `Time.time` or MEC Update | Stopwatch deadlines (idle mode scales Update by 0.01) |
| Cooldown spent before the effect worked | Start it after success |
| State per `Player` or `PlayerId` across rounds | `UserId` key, clear at `WaitingForPlayers` |
| Cleanup in `Disable()`; caching `Config` | `Shutdown`; read `Config` at use |
| `Logger` with `using UnityEngine` | `using Logger = LabApi.Features.Console.Logger;` |

## Domain skills

scpsl-server-specific-settings (sss-rules.md), scpsl-hints-and-hud, adding-player-hud, scpsl-main-thread-and-timing, scpsl-game-lifecycle, labapi-events, labapi-wrappers, scpsl-roles-damage-effects, verifying-scpsl-work.
