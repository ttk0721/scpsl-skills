# Damage, kills and attribution (reference)

Verified against LabAPI 1.1.7 / SCP:SL 14.2.7 (2026-10). Behaviour described from reading the
game's logic. UNVERIFIED marks code-derived claims not run on a server.

## The pipeline: `PlayerStats.DealDamage(handler)`

Every damage source ends here: bullets, melee, explosions, SCP attacks, every effect tick, the
LabAPI `Player.Damage(...)`/`Player.Kill(...)` wrappers.

| Step | What happens | Event |
|---|---|---|
| 1 | God mode on the target: return `false`. Active `SpawnProtected` effect (regardless of config flags): return `false`. | **None fires.** |
| 2 | Roles implementing the handler-processing hook (SCP-106, SCP-3114) may REPLACE the handler. | none |
| 3 | Pre-damage hook | `Hurting` (cancellable). `Damage` is the RAW value; no modifier ran yet. |
| 4 | `handler.ApplyDamage(hub)`: hitbox multiplier, friendly-fire rules, damage-modifier effects, armour, then AHP, then Hume, then Health. Result `Nothing`, `Damaged` or `Death`. A role whose health bar tracks another player (Spectator) returns `Nothing` before anything else. | none |
| 5 | Post-damage | `Hurt` (not cancellable, damage already applied, `Damage` is final). `DealtHealthDamage`, `AbsorbedAhpDamage`, `AbsorbedHumeDamage` live on the `StandardDamageHandler`, not on the event args. Raised even when the result is `Nothing`. |
| 6 | On `Death` | `Dying` (cancellable, AFTER damage is applied), game death handlers, kill, then `Death`. |

Kill step: spawns the ragdoll and drops the inventory (unless the handler opts out), then
`ServerSetRole(Spectator, Died, ...)`. In `Death` the victim is already Spectator, the ragdoll
exists and the items are pickups; use `e.OldRole` / `e.OldPosition`. In `Dying` the victim still
has role, position and inventory.

Instakill: `Damage == -1` zeroes AHP and health and returns `Death` WITHOUT running the modifier
chain, so no hitbox/friendly-fire/armour processing and the friendly-fire and suicide flags are
never set.

## Where to intercept

| Goal | Do | Do not |
|---|---|---|
| Scale or zero damage | In `Hurting`: `if (e.DamageHandler is StandardDamageHandler h) h.Damage *= 0.5f;` Mutate the SAME handler object. | `e.DamageHandler = new ...` (ignored, SL003). |
| Block damage | `e.IsAllowed = false` in `Hurting` (returns `false` from `DealDamage`). | Block in `Dying` (damage already applied). |
| Read the real numbers | `Hurt`: cast `e.DamageHandler` to `StandardDamageHandler`, read `Damage`, `DealtHealthDamage`, `AbsorbedAhpDamage`, `AbsorbedHumeDamage` (the three split fields stay 0 for a `-1` instakill). | Assume `Hurting.Damage` is what the victim loses. |
| Prevent death | Cancel `Hurting`; or cancel `Dying` AND restore health in the same handler. | Cancel `Dying` and walk away: the victim stays alive at 0 or negative HP and the next damage re-enters the death path. |
| Replace the handler | Cancel `Hurting` and call `Player.Damage(newHandler)` yourself. It re-enters the whole pipeline (including `Hurting` for the same victim and `Hurt`), so guard against recursion. | Harmony just to swap a handler, unless you truly need to (scpsl-harmony-patching). |

`Hurting` sees raw damage, so whatever you set is then multiplied by the head x2 / limb x0.7
hitbox factor (firearms), the friendly-fire multiplier (`friendly_fire_multiplier`, default 0.4 when
friendly fire is on), every enabled damage-modifier effect, and armour. To know the final value,
read it in `Hurt`.

Cast-check the handler type before touching it. All concrete handlers expose a `Damage` setter,
including `WarheadDamageHandler` (its constructor sets -1; a `Hurting` handler can lower it and make
the warhead non-lethal for that victim).

Hurting handler skeleton (O(1), no allocations before the early-out):

```csharp
private static void OnHurting(PlayerHurtingEventArgs e)
{
    if (e.DamageHandler is not StandardDamageHandler h) return;   // cheap type test first
    if (h.Damage <= 0f) return;                                   // instakill (-1) or nothing to scale
    if (!Config.ScaleEnabled) return;                             // read Config at use time
    h.Damage *= Config.Multiplier;                                // same object: honoured
}
```

## Killing

| Call | Returns | Notes |
|---|---|---|
| `Player.Kill()` (no args) | **`void`** | Cannot be checked for success. |
| `Player.Kill(reason, cassie)` | `bool` | |
| `Player.Damage(handler)` and the other `Damage(...)` overloads | `bool` | `false` for god mode, spawn-protected, cancelled `Hurting`/`Dying`, zero damage after processing. |

- `Player.Health = 0` does not kill. Health is a plain stat with no death logic; only
  `DealDamage` returns `Death`.
- Neither wrapper checks that the target is alive. A Spectator (and any role whose health bar
  points at another player's stats) is skipped inside `ApplyDamage`: the result is `Nothing`, so
  `Hurting` and `Hurt` still fire, `Damage(...)` returns `false` and nothing dies. This was read
  from the game's logic, not run. Guard with `p.IsAlive` to avoid the event noise.
- To kill a spawn-protected player: disable the effect first (`DisableEffect<SpawnProtected>()`).
- Silent removal (no ragdoll, no item drops, empty CASSIE, "unknown" death screen):
  `p.Damage(new SilentDamageHandler())`. It still raises `Hurting`/`Dying`/`Death` and sets
  Spectator. It is the only stock handler that opts out of both ragdoll and drops.
- Handler types: `UniversalDamageHandler(damage, DeathTranslation, cassie?)` (no attacker; ids for
  Warhead, Bleeding, Poisoned, Decontamination, Falldown, Tesla, Explosion, Scp207, Hypothermia,
  CardiacArrest and more), `CustomReasonDamageHandler(reason, damage?, cassieText?)`,
  `WarheadDamageHandler`, and attacker handlers (`FirearmDamageHandler`, `ExplosionDamageHandler`,
  `ScpDamageHandler`, per-SCP ones) deriving from `AttackerDamageHandler`.
- Wrappers: `Kill()` = universal handler with -1; `Damage(amount, reason, cassie)` = custom reason;
  `Damage(amount, attacker, force, penetration)` = explosion handler.

## Attribution

- `AttackerDamageHandler.Attacker` is a `Footprint` snapshot taken when the handler was built: hub,
  `PlayerId`, `NetId`, `LifeIdentifier`, role, unit, nickname, user id, IP. It survives the attacker
  leaving, but `Attacker.Hub` may be destroyed/null (LabAPI's `e.Attacker` is then null). Read
  nickname/role/user id from the `Footprint` when the hub is gone.
- Environmental and effect damage has no attacker. The engine keeps no "last attacker" per victim;
  assist and last-hit logic must be built by you from `Hurt`.
- `IsFriendlyFire` and `IsSuicide` are set inside the modifier chain: FALSE in `Hurting`, valid from
  `Hurt` on, and never set for `-1` instakills or when self-damage/spawn-protected-attacker rules
  zero the damage early. For instakill attacker handlers compare `Attacker.PlayerId` and teams
  yourself.
- Detect a stale attacker (died and respawned since the shot) by comparing
  `Attacker.LifeIdentifier` with the attacker's current role's life identifier.
- Each `Footprint` construction allocates; the game does one per damage call. Don't build your own
  per hit.

## Damage over time uses the same pipeline

`Poisoned`, `Bleeding`, `Hemorrhage`, `SeveredEyes`, `Decontaminating`, `Corroding`, `CardiacArrest`,
`SeveredHands`, `Asphyxiated`, `Strangled`, `PocketCorroding`, `Scp207`, `PitDeath`, `Metal`,
`Spicy` call `DealDamage` from their ticks, so each tick raises `Hurting`/`Hurt` with no attacker.
Poison tick damage doubles each tick (clamped); decontamination damage scales with max health plus
a per-tick term.

Per-frame `Hurting` sources (so the event is HOT there): SCP-3114 `Strangled` (every frame,
delta-time scaled), MicroHID primary fire (every frame while firing), plus every pellet. See the
catalog entries for `Hurting` and `Hurt` in labapi-events. Early-out first; no logging, no
`Player.List`, no wrapper work before the type test.

## Team rules that surprise people

Friendly fire uses faction, not team (see roles-and-spawning.md): ClassD vs Chaos and NTF vs
Scientist are not enemies.

## Pitfalls

| Symptom | Cause | Fix |
|---|---|---|
| `e.DamageHandler = x` does nothing | Local handler is used; setter is dead | Mutate fields; or cancel and `Damage(x)` |
| Damage differs from what you set in `Hurting` | Modifiers run after `Hurting` | Read `Hurt` |
| `Kill()`/`Damage()` appears to do nothing | God mode or spawn protection returns before any event | Check the bool; disable the effect |
| Cannot check `Kill()` result | No-arg overload returns `void` | Use `Kill(reason, cassie)` or `Damage(handler)` |
| `Health = 0` leaves player alive | No death hook on the stat | `Damage`/`Kill` |
| Player alive at <=0 HP after cancelling `Dying` | Damage applied before `Dying` | Restore health in the handler |
| Victim `Position`/`Role` already Spectator in `Death` | Kill ran before the event | Use `OldRole`/`OldPosition`, or hook `Dying` |
| `Attacker` null though an SCP hit | Environment handler, or attacker hub gone | Use the `Footprint`; null-check |
| Cancelling every DoT tick costs a handler call per tick per player | The effect keeps ticking and calling `DealDamage` | Disable the effect instead of cancelling each tick |

## Unverified

- Which other roles besides Spectator have a health bar that targets another player's stats (their
  damage also resolves to `Nothing`).
- Whether an extra layer gates Tutorial damage beyond the faction test.
- Per-frame event rates on a live server (the catalog's HOT conditions are static estimates).

## Re-verifying on a new build

- Re-read `PlayerStats.DealDamage`: order of god mode, spawn protection, handler-processing hook,
  `Hurting`, `ApplyDamage`, `Hurt`, `Dying`, kill. Confirm the local handler is still the one used
  after the events (setter trap).
- Re-read `StandardDamageHandler.ApplyDamage`/`ProcessDamage` for the -1 shortcut and modifier order.
- Check `Player.Kill` overload return types in the LabAPI source (public repository).
- Confirm `SilentDamageHandler` still opts out of ragdoll and drops.
- Re-run the catalog generator or re-read its `Hurting`, `Hurt`, `Dying`, `Death` entries; run
  `scpsl-lint` (SL003, SL001).
