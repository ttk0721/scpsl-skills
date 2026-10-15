# Custom-role spawn timeline (adding-custom-roles)

Verified against LabAPI 1.1.7 / SCP:SL 14.2.7 (2026-10). Derived from reading the game's logic and compiled against the real assemblies. Nothing here was run on a server unless it says so; the claims that matter most for a custom role are marked UNVERIFIED where they rest on code order alone. Compiled example: `examples/adding-custom-roles/`.

## One role change, as a custom role sees it

`Player.SetRole`, the `Player.Role` setter, round start, waves, late join and death all run the same synchronous call stack. Every step below happens inside ONE call, so one frame number identifies it.

| # | What happens | Event | What you do |
|---|---|---|---|
| 1 | Role about to change | `ChangingRole` (`NewRole`, `ChangeReason`, `SpawnFlags` are copied back) | Decide and record `UserId -> role id, frame`. O(1). No `SetRole`. |
| 2 | New role object taken, spawnpoint chosen | `Spawning`, `Spawned` | Only for a custom spawn position (`SetSpawnpoint`). Stats and items set here are wiped. |
| 3 | Stats reset, effects disabled, loadout granted | `ReceivingLoadout`, `ReceivedLoadout` | Replace `Items` and `Ammo`. Only raised when the flags include `AssignInventory` and the round is not restarting. |
| 4 | Role info sent to every player | `ChangedRole` | Apply stats, effects, appearance. Release whatever the old role instance held. |

Death is the same stack with `Spectator` as the new role: `ChangedRole` fires BEFORE `Death`, so one `ChangedRole` handler is the cleanup for role change, respawn and death. Do not add `Dying`/`Death` cleanup. Cancelling `Dying` keeps the victim alive and fires no `ChangedRole`, which is correct: the assignment is still valid.

## Why a frame number, not a flag

A decision taken in `ChangingRole` can be overruled: another plugin cancels the change or rewrites `NewRole` after you. A plain "pending" flag would then wait for the player's NEXT role change and hand the custom role to an unrelated spawn. Store the frame (`UnityEngine.Time.frameCount`) with the decision and accept it only when `ReceivingLoadout`/`ChangedRole` run in the same frame, and only when the role that actually arrived equals the base role you decided for. Sweep old entries at 1 Hz so one cancelled change cannot keep your early-outs permanently off.

## Who and when

| Situation | Behaviour | Consequence |
|---|---|---|
| Round start | `RoundStarted` fires first and roles are still `None`; roles are assigned later in the same frame, each with `ChangingRole` ... `ChangedRole` | Never assign from `RoundStarted`. Roll in `ChangingRole` (reason `RoundStart`). |
| Respawn wave | One role change per player in one loop, reason `Respawn` or `RespawnMiniwave` | Handlers run per player in one frame: no `Player.List` scans, no I/O. |
| Late joiner | `ChangingRole` and `ChangedRole` run BEFORE `Joined`; `UserId` is already set, the nickname and `IsReady` are not | Key by `UserId`. Do not read the nickname or send a hint in `ChangedRole`; announce from a later tick when `IsReady`. |
| Admin or API grant | Reason `RemoteAdmin`, flags `All` | A same-role set is a full respawn (stats, effects, loadout, position). Never fake `RoundStart`/`Escaped`: counters move. |
| Escape | Reason `Escaped`; with `keep_items_after_escaping` old items are re-picked later | Do not roll for it. Your loadout would fight the game's item return. |
| Round restart | Scene reload: every hub and `Player` is destroyed, `Left` fires per hub, statics survive | Clear in `WaitingForPlayers`; `Left` removes per-player entries as the hubs go. No custom role survives a restart. |
| Process shutdown | `ServerEvents.Shutdown`, then exit; `Disable()` is never called | Unsubscribe and dispose the clock job there. |

## Appearance outlives the role

Custom info text and the info-area flags live on the player's nickname component, not on the role. Nothing in the role-change path resets them (code-read, not run): a player who dies as a "Heavy Guard" would still show the label as a spectator. Record the previous values, write yours, and on release restore them only if the current values are still yours, so a plugin that took over the label in between is not overwritten.

## Effects as free cleanup

Status effects are all disabled on a role change (a few re-apply themselves: spawn protection, pocket corrosion). A passive ability expressed as an effect (`DamageReduction`, `MovementBoost`) therefore needs no handler and no cleanup on the role-change path. `duration: 0` means "never expires" and also erases an existing timer, so pass it deliberately. When you release a holder who KEEPS the same role instance (kill switch, definition removed from the config), the game will not wipe the effects: disable them yourself. Compare `Player.LifeId` with the stored value to tell the two cases apart.

## Spawn position

Use `Spawning` with `SetSpawnpoint(position, yaw)` or `SpawnLocation`; both add the spawnpoint flag. Read positions from the event, not from `Player.Position`, which is not valid yet. `Spawning` is not raised for roles without a spawnpoint handler (Spectator, SCP-079, `None`, possibly others: UNVERIFIED which). For those, teleport from `ChangedRole`. The decision from `ChangingRole` is available in `Spawning` through the same frame check.

## Re-verifying on a new build

- Re-read the sequence in the role manager: `ChangingRole`, disable old role, init, spawnpoint, stats reset, loadout, spawn protection, role-info send, `ChangedRole`. Confirm `ChangedRole` still comes last and the loadout event still sits inside it.
- Confirm the three `ChangingRole` setters are still copied back and `ReceivingLoadout.Items`/`Ammo` are still read.
- Confirm that a death still sets `Spectator` (and raises `ChangedRole`) before `Death`.
- Grep the nickname component for any new reset of custom info or the info area.
- Confirm `Player.LifeId` still maps to a counter that changes on every role instance.
- Rebuild `examples/adding-custom-roles/` and run scpsl-lint on it.
- On a dev server: grant a role, kill the player, check the label is gone; cancel a `ChangingRole` from a second plugin and check nothing leaks to the next role.
