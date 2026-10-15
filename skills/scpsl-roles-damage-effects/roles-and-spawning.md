# Roles and spawning (reference)

Verified against LabAPI 1.1.7 / SCP:SL 14.2.7 (2026-10). Behaviour is described from reading the
game's logic, not copied. Items marked UNVERIFIED were derived from code order and not run on a
server. Millisecond figures are estimates, not measurements.

## One `ServerSetRole`, step by step

`Player.SetRole(role, reason, flags)` and the `Player.Role` setter both end in
`PlayerRoleManager.ServerSetRole`. There is no "same role, do nothing" shortcut: setting the role a
player already has is a full respawn (stats reset, effects off, loadout re-granted, spawnpoint).

| # | Step | LabAPI event | What exists after it |
|---|---|---|---|
| 1 | Pre-change | `PlayerEvents.ChangingRole` (cancellable; `NewRole`, `ChangeReason`, `SpawnFlags` are copied back) | Nothing changed yet. Old role fully alive. |
| 2 | Game `OnServerRoleSet` subscribers (about 22 static + per-player ones) | none | Still the old role. |
| 3 | Old role disabled, new pooled role taken, `Init`, spawnpoint chosen | `Spawning` then `Spawned` (only for FPC roles with a spawnpoint handler) | New role object is current, but its model, FPC module and stats are NOT set up yet. |
| 4 | New role's objects spawned (model, movement module) | none | Position API becomes valid here. |
| 5 | Game `OnRoleChanged` subscribers (about 51): stats reset, effects disabled, loadout granted | `ReceivingLoadout` / `ReceivedLoadout` inside | Health, AHP, stamina, hume, effects, items of the NEW role. |
| 6 | Spawn protection applied (only if enabled in config) | none | `SpawnProtected` is already on in `ChangedRole`. |
| 7 | Role info sent to every player (one message per receiver) | `ChangedRole` | Everything valid. |

Consequences:

- Health, AHP, stamina, effects and inventory set in `ChangingRole`, `Spawning` or `Spawned` are
  wiped at step 5. The hume shield value is zeroed only for roles without a hume shield; for hume
  roles it is driven by the role's module afterwards. Set all of them in `ChangedRole` (or one
  frame later).
- Exception to "valid in `ChangedRole`": with `RoleChangeReason.Escaped` and the
  `keep_items_after_escaping` config, old items are dropped and re-picked one player per frame from
  a queue, so the old inventory is not back yet in `ChangedRole`.
- A mid-flow `SetRole` on the same player is unsafe (see Nested role changes).

## Redirecting a role change

Mutate the event, never call `SetRole` again:

```csharp
private static void OnChangingRole(PlayerChangingRoleEventArgs e)
{
    // O(1), no allocations, no Player.List scans: this runs once per player in mass spawns.
    if (e.ChangeReason != RoleChangeReason.RoundStart) return;
    if (e.NewRole != RoleTypeId.ClassD) return;
    if (!ShouldBeGuard(e.Player)) return;
    e.NewRole = RoleTypeId.FacilityGuard;        // supported
    // e.ChangeReason and e.SpawnFlags are also honoured when mutated.
}
```

This is also the cheapest way to customise a vanilla mass spawn: no second `ServerSetRole`, so no
second round of O(N) role-sync messages.

Cancelling `ChangingRole` is dangerous. The game's kill and escape code call `ServerSetRole` and
carry on as if it worked. A cancelled death role change still raises `Death` with the player
unchanged. An escape whose role change is cancelled leaves the player in the escape zone, and the
escape message, the `Escaped` event and the achievements hook repeat every frame (see the `Escaping`
entry in the labapi-events catalog; `Escaping` itself is HOT, gate it on `EscapeScenario != None`).
Redirect instead of cancelling, or guard hard.

`ChangingRole`/`ChangedRole` also fire on join (None to Spectator), for dummies and for the host
hub. Skip `p.IsHost` and dummies up front.

## Nested role changes

Do not call `SetRole` on the same player from `ChangingRole`, `Spawning` or `Spawned` (derived from
code order, runtime effect UNVERIFIED). Two distinct failures:

| From | What goes wrong |
|---|---|
| `Spawning` / `Spawned` | The outer call already took role A from the pool without finishing setup. The nested call's disable of A is a silent no-op, A stays active, then the outer call finishes setting up A after role B is already current. Orphaned role object, spawn position overwritten, `OnRoleChanged`/`ChangedRole` fire again. |
| `ChangingRole` | Nothing is orphaned, but `ChangingRole` is raised again for the nested call (unbounded recursion if unguarded), the role is spawned twice, and the outer call overwrites the result with its own `NewRole`. |

`SetRole` from `ChangedRole` is outside this hazard (the outer call has completed) but still needs a
same-role and re-entry guard, because `ChangedRole` fires again.

## `RoleChangeReason` is not cosmetic

Values: `None, RoundStart, LateJoin, Respawn, Died, Escaped, Revived, RemoteAdmin, Destroyed,
RespawnMiniwave, ItemUsage, Resurrected`.

| Reason | Engine side effect |
|---|---|
| `RoundStart`, `LateJoin` | Round summary adjusts spawned-team counters. |
| `Escaped` | Escape counters (only if not disarmed); loadout provider keeps/re-picks items per config. |
| `Revived` | Zombie counters. |
| `Respawn`, `RespawnMiniwave` | Human role init skips the unit-name decrement. |
| any reason | The wave spawner records the player's PREVIOUS faction for wave priority on every role set. |

Plugin default: `RoleChangeReason.RemoteAdmin` with `RoleSpawnFlags.All`. Never fake
`RoundStart/LateJoin/Escaped/Revived` unless you want those counters to move.

## `RoleSpawnFlags`

`None = 0`, `AssignInventory = 1`, `UseSpawnpoint = 2`, `All = -1`.

- `AssignInventory` gates the loadout grant and therefore `ReceivingLoadout`. Without it, add items
  yourself from `ChangedRole`. `ReceivingLoadout` is also not raised while the round is restarting.
- A class that defines its own inventory (custom class, event role) must leave `AssignInventory` out
  and clear the inventory before giving its items. With the flag, the role's default loadout is granted
  first and the class items are added on top, so the player ends up with both sets.
- `UseSpawnpoint` gates the teleport. The game itself clears it for item-triggered transformations
  and uses `None` for SCP-1509 resurrection; Remote Admin `/forceclass` accepts the number.
- A respawn from Spectator with only `AssignInventory` lands at the corpse position (Spectator
  parks the hub at y=6000 and the disable step puts it back at the death position), not at a
  spawnpoint.

## `Spawning` / `Spawned`

- Raised only for FPC roles that have a spawnpoint handler. NOT raised for Spectator, SCP-079,
  `None`, or any FPC role whose handler is null (which FPC roles are null is UNVERIFIED, prefab
  data). Do that setup in `ChangedRole`.
- Cancelling `Spawning` stops the teleport and `Spawned` is not raised.
- `SpawnLocation`, `HorizontalRotation` and `SetSpawnpoint(...)` set `UseSpawnPoint = true` and OR
  the `UseSpawnpoint` flag into the role. Setting only `UseSpawnPoint = true` on a role that lacks
  the flag does nothing.
- In `Spawning`/`Spawned`, do not trust `Player.Position`: the movement module's cached position and
  owner fields are assigned a step later, and a pooled module can still hold the previous owner's
  values (UNVERIFIED at runtime). Read the position from the event; teleport with `Player.Position`
  only from `ChangedRole`.

## Role templates, holiday roles, dead roles

- Roles missing from the loaded role table silently become `None`: an error is written to the log,
  `ChangingRole` still shows the requested role, and `ChangedRole` reports `None`. Holiday roles
  (the Flamingo family, SCP-1507) are filtered out unless the holiday is active or the build is
  Development/Nightly. Before assigning optional roles check the generic extension
  `role.TryGetRoleTemplate<PlayerRoleBase>(out var template)` (in the `PlayerRoles` namespace) and
  refuse when it fails.
- Role objects are pooled prefab clones. Default pool size is 10 with "create more" overflow, and
  pools never shrink. Pool misses `Instantiate` a whole prefab (cold-pool cost in
  ragdolls-and-mass-events.md).

## Teams, factions, enemies

| Team | Faction |
|---|---|
| NTF (`FoundationForces`), Scientists | FoundationStaff |
| Chaos, ClassD | FoundationEnemy |
| SCPs | SCP |
| Flamingos | Flamingos |
| Dead, OtherAlive (Tutorial) | Unclassified |

- `IsAlive` is `Team != Dead`. The game's `IsHuman` extension (on `RoleTypeId` and `ReferenceHub`) is
  "not Dead, not SCPs, not flamingo" (Tutorial counts). The LabAPI wrapper `Player.IsHuman` is
  looser: alive and not SCP, so a flamingo counts as human there.
- `HitboxIdentity.IsEnemy(a, b)` compares factions: ClassD vs Chaos and NTF vs Scientist are NOT
  enemies; Dead is never an enemy; SCP vs SCP is never an enemy; Tutorial reads as an enemy of every
  other faction at this layer (whether another layer overrides that: UNVERIFIED).
- `RoleTypeId.GetTeam()` reads the role template; `Player.Team` reads the live role.

## Role-keyed decisions: roles that are server-side mechanics

A filter keyed on "alive FPC role" is wrong whenever it changes what the server or an SCP ability
sees. Several SCP mechanics are server-side visibility or raycast logic keyed on the target: SCP-173
observation, SCP-096 targeting, SCP-049 Sense, zombie bloodlust, SCP-1344 detection. Hiding or
altering a target that one of them treats as visible freezes 173, or makes an honest player a 096
target while his own client shows nothing. (Network-layer detail: scpsl-networking-for-plugins.)

Safe defaults for any role-based visibility, targeting or "who counts" logic:

| Case | Rule |
|---|---|
| Target is not a `HumanRole` (SCPs, flamingos, any other non-human role class) | Exempt. Leave vanilla. |
| Target is in noclip | Exempt (the client freezes it visible at the old position). |
| Target is on a moving elevator or in a position with no room | Exempt. |
| Receiver is SCP-939, SCP-096 Distressed/Enraged, SCP-106 stalking, or a dummy | Leave untouched. |
| SCP-049 Sense target while `HasTarget` | Stays visible. |
| Spectator, Overwatch, SCP-079, `None` | Exempt from movement/activity checks (no real FPC state). |

When you need a "player really moved" signal, re-baseline the stored position on `ChangedRole` so
the spawn teleport is not counted as movement.

## Pitfalls

| Symptom | Cause | Fix |
|---|---|---|
| Health/effects/AHP set in `Spawned` vanish | Stats and effects reset after spawn, in step 5 | Set in `ChangedRole` or next frame |
| `Player.Position` wrong or moves someone else in `Spawned` | Position fields assigned after the event | Use `e.SpawnLocation`; teleport from `ChangedRole` |
| Player appears at corpse position | Flags lacked `UseSpawnpoint` | Keep `All`, or set a spawnpoint in `Spawning` |
| `Spawning`/`Spawned` never fires | Role has no spawnpoint handler | Use `ChangedRole` |
| `SetRole(Flamingo)` gives `None` | Holiday template missing | Check template first |
| Nested `SetRole` duplicates events or recurses | See Nested role changes | Mutate `e.NewRole`; defer `SetRole` one frame |
| Round summary or wave priority skewed | Fake reasons; every set updates previous-faction memory | Use `RemoteAdmin` unless emulating the real flow |
| Same-role set resets everything | No early-out | Compare roles yourself before calling `SetRole` |
| Items you gave in `ChangingRole`/`Spawned` are gone | The new role's inventory is only valid from step 5 on (the loadout grant runs after those events) | `ReceivingLoadout` (edit `Items`/`Ammo`) or add in `ChangedRole` |

`ReceivingLoadout`: edit the `Items` and `Ammo` lists; do not keep references to them (pooled).
`InventoryReset` is ignored (SL003). Cancelling keeps the old inventory but, with
`Escaped` + `keep_items_after_escaping`, the old items were already dropped, so the player ends up
with neither.

## Unverified

1. Nested `SetRole` consequences (derived from code order, not reproduced).
2. Stale owner fields on pooled movement modules before spawn.
3. Which FPC roles have no spawnpoint handler.
4. Relative order of the many `OnRoleChanged` subscribers. Only the end result (valid in
   `ChangedRole`) is dependable.
5. Whether anything beyond the faction rule gates Tutorial damage.

## Re-verifying on a new build

- Re-read `PlayerRoleManager.ServerSetRole` and `InitializeNewRole`: order of disable, init,
  spawnpoint, setup, `OnRoleChanged`, spawn protection, role-info send, `ChangedRole`.
- Re-check that the three `ChangingRole` setters are still copied back and that
  `PlayerSpawningEventArgs` setters still OR in the flag.
- Grep `RoleChangeReason`, `RoleSpawnFlags` for new values; grep who passes them (round summary,
  wave spawner, loadout provider).
- Check `PlayerRoleLoader` for new holiday/availability filters.
- Run `scpsl-lint` (SL003) and re-read the catalog entries for `ChangingRole`, `Spawning`,
  `ReceivingLoadout` in labapi-events.
- On a dev server: set the role from `Spawned` and confirm (or refute) the orphan behaviour.
