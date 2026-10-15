# Gameplay rule events: what the game reads back (reference)

Verified against LabAPI 1.1.7 / SCP:SL 14.2.7 (2026-10). Companion to `SKILL.md`. Source of truth
for every row: `events-catalog.md` and `tools/catalog/labapi-events.json` (labapi-events). This file
only collects the rows a rule author needs and adds the rule-specific consequences.

## Honoured and ignored, per event

| Event | Honoured | Ignored or trap |
|---|---|---|
| `PlayerEvents.Hurting` | `IsAllowed`; changes to the SAME handler object (`Damage` on a `StandardDamageHandler`) | `DamageHandler = x` (the game applies its local handler). Not raised for god-mode or spawn-protected victims. `Damage` is raw: hitbox, the game's friendly-fire multiplier, effects and armour run afterwards. `IsFriendlyFire`/`IsSuicide` are false here |
| `PlayerEvents.Hurt` | nothing (not cancellable) | Final numbers. Raised even when the result was `Nothing` |
| `PlayerEvents.Dying` | `IsAllowed` | Raised AFTER damage was applied: cancelling leaves HP at or below 0 and nobody dead. `DamageHandler` ignored |
| `PlayerEvents.InteractingDoor` | `IsAllowed`, `CanOpen` | On a locked door the game raises it with `CanOpen = false` and returns `ev.CanOpen` as the lock verdict, so `true` opens the lock. Cancelling there still plays denial feedback and fires `InteractedDoor(false)` |
| `PlayerEvents.InteractingElevator` | `IsAllowed` | The game pre-sets it `false` for locked or moving elevators and still raises the event. Writing `true` force-sends the elevator |
| `PlayerEvents.InteractingLocker` | `IsAllowed`, `CanOpen` | `CanOpen = true` bypasses the keycard check |
| `PlayerEvents.PickingUpItem` | `IsAllowed` | Raised at search COMPLETION; cancel returns the pickup to the world. Ammo, armor and SCP-330 have their own events |
| `PlayerEvents.SearchingPickup` | `IsAllowed` | Raised at search start for EVERY pickup kind, before the game's own validation, and also from non-player paths (SCP-914, escape item return, add-or-drop). Cancelling there turns the item into a dropped pickup |
| `PlayerEvents.UsingItem` | `IsAllowed` | Raised when use STARTS; the effect lands in `UsedItem` |
| `PlayerEvents.DroppingItem` | `IsAllowed`, `Throw` | Client command only: not raised for death drops. Cancel does not resync the client's inventory UI (UNVERIFIED desync) |
| `PlayerEvents.ThrowingItem` | `IsAllowed` | Raised after the drop: cancel re-adds the item and destroys the pickup |
| `PlayerEvents.Escaping` | `IsAllowed`, `NewRole`, `EscapeScenario` | HOT. Raised every frame per FPC player in the zone, also with scenario `None`. `EscapeScenario = None` makes it a no-op. A cancel does not remove the player from the zone, so the event repeats every frame; the same happens if another plugin cancels the resulting `ChangingRole` |
| `ObjectiveEvents.EscapingCompleting` | `IsAllowed`, `Faction`, `InfluenceToGrant`, `TimeToGrant`, `SendToPlayers`, `Player` | Once per escape: the right place for reward rules. It cannot stop the escape itself |
| `PlayerEvents.ChangingRole` | `NewRole`, `ChangeReason`, `SpawnFlags` | Cancelling during death or escape is dangerous: `Death`/`Escaped` still fire with the player unchanged |

## Rules compose only if they commute

Handlers run in subscription order with no short-circuit, and the game reads each field once after
the last handler (labapi-events, event-mechanics.md sections 5 and 8). Two plugins that both
**assign** the same field leave the later one's value. Two that **multiply** or **cancel** leave a
result that does not depend on order.

- Cancel: write `false` only, never `= !allowed`.
- Scale: `h.Damage *= k`. Never `h.Damage = k` unless replacing every other plugin is the point.
- Grant: `CanOpen = true` is an assignment. A cancel from any plugin still wins, because the game
  returns on `!IsAllowed` before reading `CanOpen`. Two plugins disagreeing on `CanOpen` is last
  writer wins.
- Plugin `Priority` is a byte and lower enables first, so `LoadPriority.Lowest` runs last and gets
  the last word. Do not rely on it for a rule that must hold: design the rule so order does not matter.
- A subscription made later (lazily, per round) lands after every plugin-load-time subscription.
  The example wires in `Enable()` and on config reload only.
- On EXILED servers its own Harmony patches can re-emit or truncate the LabAPI raise
  (labapi-events). Test a door or damage rule with EXILED loaded if the server mixes them.

## Hot paths

- `Escaping` is the only hot event a rule commonly needs. First line: `EscapeScenario == None` return.
  Cancelling is one write; do the hint and counter through a per-player cooldown.
- `Hurting` and `Hurt` are per action, but per frame per victim under DoT, SCP-3114 strangle and
  Micro-HID fire. Cheap type test first (`is AttackerDamageHandler` drops all of them), no logging in
  the body, rate-limited logging in the `catch` (`RateLimitedLog.Error`).
- Do not use `ValidatedVisibility`, `ReceivingVoiceMessage` or `AddingObserver` for a rule.
  Express the rule on a lower-frequency event or gate it (labapi-events).

## Pre-checks every handler needs

| Case | What to do |
|---|---|
| Handler is not `AttackerDamageHandler` | Environment, DoT and warhead damage: leave alone unless the rule says so |
| `ev.Attacker == null` | Attacker's hub is gone (read the `Footprint`) or environmental damage |
| Instakill (`Damage == -1`) | No modifier chain runs; scaling is meaningless |
| Role-keyed rule | Name SCPs, Tutorial, dummies and dead explicitly (scpsl-roles-damage-effects) |
| Self damage | `attacker.PlayerId == victim.PlayerId`: usually exempt from a friendly-fire rule |
| Config values | Clamp multipliers; a typo must not heal or one-shot |
| Server friendly-fire setting | The game's own friendly-fire handling and its multiplier (default 0.4 when on) run AFTER your event. What it does with the setting off is UNVERIFIED: test `Scale` with the setting both ways |

## Re-verifying on a new build

- Re-read the catalog rows above (`ignoredSetters`, `consumption`, `notes`) after regenerating
  `tools/catalog/labapi-events.json`; diff `Hurting`, `InteractingDoor`, `InteractingElevator`,
  `PickingUpItem`, `SearchingPickup`, `Escaping`.
- Confirm `PlayerStats.DealDamage` still applies its local handler after `Hurting`, and that
  `IsFriendlyFire` is still assigned only inside the modifier chain.
- Confirm `DoorVariant.TryResolveLock` still returns `ev.CanOpen` and that the elevator panel path
  still pre-sets `IsAllowed` to `false` for locked or moving chambers.
- Confirm `Escape.ServerHandlePlayer` still raises `Escaping` for every FPC player in the zone each frame.
- Rebuild `examples/adding-gameplay-rules` and run `scpsl-lint` on it.
