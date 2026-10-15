# Stats and status effects (reference)

Verified against LabAPI 1.1.7 / SCP:SL 14.2.7 (2026-10). Behaviour described from reading the
game's logic. UNVERIFIED marks code-derived claims not run on a server. Microsecond and
millisecond figures are unmeasured estimates.

## Stats (`PlayerStats` and its modules)

Per player there are six stat modules: Health, AHP, Stamina, AdminFlags, HumeShield, Vigor. Each is
updated every frame.

| Stat | Wrapper | Semantics | Trap |
|---|---|---|---|
| Health | `Player.Health`, `MaxHealth` | Float on the server, `ushort` of `ceil(value)` on the wire. Setter does not clamp and raises no LabAPI event. Lowering max clamps current. | Writing 0 does not kill (damage-and-kills.md). Reset to the role's max on every role change. |
| AHP | `Player.ArtificialHealth`, `MaxArtificialHealth` | Driven by "processes" recomputed every frame. Max resets to 75 and all processes clear on role change. | Writing `AhpStat.CurValue` is overwritten next frame. The wrapper setter kills all processes and adds ONE non-decaying process with efficacy 1.0 (vanilla default 0.7), so it absorbs 100 % of damage until empty. Its XML doc says max resets "when AHP reaches 0"; the code resets it only on class change. |
| Hume shield | `Player.HumeShield`, `MaxHumeShield` | Regenerates/decays per frame from the role's module. A `MaxValue` write sets an override. | Override is cleared on class change only for roles that do NOT implement the hume-shield interface; humans and hume SCPs do, so it survives human to SCP to human. `MaxHumeShield = -1` clears it. For hume roles the current value is not zeroed on class change either. |
| Stamina | `Player.StaminaRemaining` | Normalised **0 to 1**, one byte on the wire. | Writing 100 does nothing useful. |

Real AHP API: `AhpStat.ServerAddProcess(amount, limit, decay, efficacy, sustain, persistent)`. Use
it when you need decay, sustain or vanilla efficacy; use `Player.ArtificialHealth` only for "give
exactly this much shield".

Hume regen wrappers (`HumeShieldRegenRate`, `HumeShieldRegenCooldown`) are guarded by a role-type
test, but then cast the role's hume module to one concrete controller type unchecked (the
`HumeShieldRegenRate` getter is the one member that does not cast). Which roles would throw
`InvalidCastException` is UNVERIFIED (module type is prefab data). Wrap in try/catch or avoid.

### When to write stats

All stat writes belong in `ChangedRole` or later. Order for AHP: set `MaxArtificialHealth` first if
you want more than 75, then `ArtificialHealth`. Set `MaxHumeShield` after the role is applied.

```csharp
private static void OnChangedRole(PlayerChangedRoleEventArgs e)
{
    Player p = e.Player;
    if (p.IsHost || p.IsDummy) return;                 // join and host hub also raise this
    if (p.Role != RoleTypeId.NtfCaptain) return;
    p.MaxHealth = 150f;                                // valid here: reset already ran
    p.Health = 150f;
    p.MaxArtificialHealth = 100f;                      // before the value, because the max resets to 75
    p.ArtificialHealth = 50f;
}
```

### Sync cost and granularity

- Health, AHP, Stamina, Hume and Vigor go to the owner AND to every dead player (and Overwatch). A
  stat change is evaluated against every hub with a per-hub predicate.
- A new spectator gets a full snapshot of every living player (about 12 messages per living player
  when someone becomes Spectator, estimate).
- Dirty detection compares against the last value sent, so writes finer than the wire step add no
  traffic, only CPU. Real traffic comes from crossing a `ceil` step often. `MaxHumeShield` and
  stamina `MaxValue` mark dirty on EVERY write even when unchanged.
- So: write in whole HP steps, at a few Hz, only for players who need it. Never heal everybody
  every frame.

## Status effects (`CustomPlayerEffects`)

One `StatusEffectBase` component per effect per player, built once at player creation.
Intensity (a byte) is replicated; duration and time-left are server-only.

### Calls

| Want | Call | Notes |
|---|---|---|
| Enable timed | `p.EnableEffect<Slowness>(intensity: 30, duration: 5f)` | ALWAYS pass a duration. |
| Enable forever | `duration: 0` | Means infinite AND erases an existing timer. |
| Extend | `addDuration: true` with duration > 0 | |
| Disable | `p.DisableEffect<T>()`, `DisableAllEffects()` | |
| Read | `p.GetEffect<T>()`, `TryGetEffect<T>`, `HasEffect<T>()` | Dictionary lookup by EXACT concrete type: a base class `T` returns null. |
| Lookup by name | `TryGetEffect(string)` | Linear scan, case-insensitive prefix match on the effect's object name, first match wins: store full names (`Blind` or `Scp` can hit another effect than you meant). Never per frame. An unknown name returns false: report it instead of failing silently. |

`EnableEffect<T>` returns `void` and is a silent no-op when the effect is absent. Call
`GetEffect<T>()` and test for null if you need to know.

### Rules the code enforces

- **Duration reset runs even when the intensity change is rejected.** Re-enabling "without
  duration" turns a timed effect (bleeding, poison) permanent: `EnableEffect<Bleeding>(1)` on a
  bleeding player makes it never expire.
- **Lowering is always accepted.** `EnableEffect<T>()` with the default intensity 1 on an effect
  running at 3 DOWNGRADES it to 1.
- **Raising can be refused.** `Negative` effects cannot be raised while `SpawnProtected` or
  `Vitality` is active (silent no-op). `SpawnProtected` only exists when `spawn_protect_enabled` is
  true (default false), but then it is already active in `ChangedRole` (applied at the end of the
  role change). Bypass deliberately: `effect.ForceIntensity(i)` then `effect.ServerChangeDuration(d)`.
  Some effects override the gate (Flashed, Slowness, Traumatized refuse during spawn protection;
  Decontaminating always allows).
- **Holiday effects are absent off-holiday.** They are excluded from the effect table, so
  `GetEffect<T>()` is null and `EnableEffect<T>()` does nothing.
- **Intensity meaning is per effect, and not clamped.** DamageReduction = 0.5 % per step
  (>= 200 gives a multiplier <= 0). MovementBoost = +%. Slowness = -% (100 disables sprint, >= 101
  gives a negative speed multiplier). Scp1576 writes its own intensity every frame; Scp1853 caps
  at 20.
- **Timers use frame time.** Time-left drops by `Time.deltaTime`, which is clamped by the engine's
  maximum delta time (0.333 s; unverified on other setups), so effects outlive
  wall-clock time on a stalled server.

### Reset on role change

All effects are disabled on role change (on death, `OnDeath` runs when going alive to Dead; both
default to disable). Exceptions: `BecomingFlamingo` (resets only when entering SCP-1507, never on
death), `SpawnProtected` (re-applies itself), `PocketCorroding` (re-applies if the new position is in
the Pocket Dimension), `InsufficientLighting` (resets its latch), parent effects (also disable
sub-effects). A role change therefore raises one `UpdatingEffect` per active effect. Re-apply your
own effects in `ChangedRole`.

### Events: when they fire

- `UpdatingEffect` / `UpdatedEffect` fire only when intensity actually changes. A same-value
  refresh of duration raises nothing. Disabling and expiry raise it with intensity 0.
- On the `EnableEffect`/`ServerSetState` path the duration is written AFTER the event, so
  `e.Duration` in `UpdatingEffect` is the OLD duration and your edits are overwritten. Read
  `effect.Duration`/`TimeLeft` one frame later, not inside `UpdatedEffect`.
- Steady sources: SCP-1576 (about one change per second per user), Hypothermia (while exposure
  moves), SCP-1853 and InsufficientLighting (on state flips). Handler load is bursty, not
  per-frame, but treat it as HOT once you cancel: callers that retry every frame or tick re-raise
  the event each time for that player.
- First line of the handler: `if (e.Effect is not Slowness) return;`, before any wrapper work.

### Per-frame engine cost (do not add yours on top)

Estimates, unmeasured: about 56 active effect updates plus a few extras per player per frame
(0.3 to 1.2 ms per frame at 100 players). Some effects work even when inactive:
`InsufficientLighting` does a room lookup per player per frame (a miss on Surface can cost up to two
short raycasts); `Hypothermia` runs a full update without an enabled guard; `Flashed` and SCP-1344
detection keep small per-frame state. Cache effect references per player (`GetEffect<T>` is cheap,
`TryGetEffect(string)` is not), and prefer `UpdatedEffect`/`Hurt` events over polling all players.

## Pitfalls

| Symptom | Cause | Fix |
|---|---|---|
| Stats/effects set in `Spawned` gone | Reset in `OnRoleChanged` after spawn | `ChangedRole` |
| AHP "resets itself" | Process-driven; max resets to 75 | `ArtificialHealth` after `ChangedRole`, max first |
| Effect never expires | `duration: 0` or default duration | Pass a duration |
| Effect got weaker | Default intensity 1 lowered it | Pass an explicit intensity, check current |
| Harmful effect does nothing to fresh NTF/Chaos | Spawn protection gate (only with config on) | Wait, disable `SpawnProtected`, or `ForceIntensity` |
| `EnableEffect<Prismatic>()` no-op | Holiday effect absent | Null-check `GetEffect<T>()` |
| Stamina 100 does nothing | Range is 0 to 1 | Use 0..1 |
| `MaxHumeShield` sticks to the next SCP | Override kept for hume roles | Set `-1` when switching |
| Dead players flooded with stat messages | Healing every frame | Integer steps, few Hz |
| Stale duration in `UpdatingEffect` handler | Duration written after the event | Read next frame |
| Handler for `UpdatingEffect` in profiles | Runs for every effect of every player | Type filter first line |

## Unverified

- Whether the effect-intensity sync list uses Observers mode on the real prefab.
- Which roles make the hume-regen cast throw.
- The 0.3 to 1.2 ms per frame per-100-players effect cost, and the Surface lighting lookup cost.
- The maximum delta time value on other servers and builds.

## Re-verifying on a new build

- Re-read `PlayerStats.OnClassChanged` and each module's class-change handler (reset values, AHP
  max 75, hume override rule).
- Re-read `StatusEffectBase`: intensity setter (`value <= current || AllowEnabling`),
  `ServerSetState`, `ServerChangeDuration`, `ForceIntensity`, and who overrides `AllowEnabling`.
- Re-read `PlayerEffectsController` (effect table build, holiday exclusion, role-change reset).
- Check LabAPI `Player.cs` for the `ArtificialHealth` setter (efficacy argument) and effect wrappers
  (return types).
- Count effect classes with a per-frame `Update` that lacks an enabled guard.
- Measure with sl-probe or SlKit `FrameProfiler` before trusting any cost figure here.
