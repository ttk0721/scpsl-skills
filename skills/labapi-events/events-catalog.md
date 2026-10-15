# LabAPI event catalog (LabAPI 1.1.7, SCP:SL 14.2.7)

Generated from code-level research: for every event, where the game raises it, how often, whether
cancellation and setters are honoured. Full per-event detail (traps, hot conditions, what the game
does with the args after the call): `tools/catalog/labapi-events.json`. Grep it by event name.

Three flags describe how often a handler runs. They are derived from the frequency class, not set by hand:

- **hot**: raised every frame, every frame per observer x target pair, or per incoming network packet at high rate
  (more than ~100 raises/s at 60 players). Every subscriber pays that rate for the whole round.
- **burst**: raised once per shot, bullet hit, hitmarker or damage application. The rate follows the fire rate:
  idle most of the time, a spike in a firefight. All shot/hit events are classed alike.
- **conditionallyHot**: the row carries `hotConditions`. Cheap in the common case, per-frame in the listed situations.

`scpsl-lint` warns (SL001) when a plugin subscribes to a hot or conditionally hot event, and reports
expensive per-raise work inside such a handler as an error (SL018; the same work in a burst handler is a warning, SL019).

## Hot events — keep the handler trivial and gated

| Event | Frequency | Why it is hot |
|---|---|---|
| `PlayerEvents.IdlingTesla` | per-frame-per-pair | FixedUpdate (50 Hz default, not TPS-scaled). For each active gate with no remaining inactive time it loops all alive hubs; the event is raised for each hub in idle range until one is allowed (the check is skipped once an earlier hub was allowed). So it fires every physics step per gate that has a live player in idle range even when nothing changes, and once per in-range hub per step if handlers cancel. Allocates args each time; estimated at 50-150 args per second. |
| `PlayerEvents.ValidatedVisibility` | per-frame-per-pair | Per receiver and target pair, every position tick: the position distributor sends one position message per verified connected hub at an interval of one over the server tickrate (clamped to 10..60), and building each message loops all FPC hubs (excluding the receiver and the local host). 60 players at 60 tick: about 60 x 59 x 60, roughly 212,000 raises/s. Player is the receiver, Target the observed player. IsVisible is pre-seeded from the role's ICustomVisibilityRole validator. Setting IsVisible to false hides the target's position from that receiver (invisibility plugins), true forces it visible even against the SCP-079 and SCP-3114 visibility rules. Anything beyond a dictionary lookup or flag test in this handler is a measurable cost; the args constructor does two Player.Get dictionary lookups plus one args-object allocation per pair. Receivers are every verified non-host hub (spectators and SCPs included), targets every other FPC-role hub. |
| `Scp173Events.AddingObserver` | per-frame-per-pair | Raised from Scp173ObserversTracker.UpdateObserver, which the per-frame update calls for EVERY hub in ReferenceHub.AllHubs on every SCP-173 instance (server only). Event args are only allocated and raised on the not-observing to observed edge, so a non-cancelling handler sees roughly the edge rate (flicker as players turn). TRAP: if a handler cancels, the observer set is not updated, so the same edge condition is re-evaluated and the event re-fires EVERY FRAME for that (173, target) pair (up to 60 targets at 60 fps per 173). Only fires for enemies (HitboxIdentity.IsEnemy); non-enemy targets are silently removed from the observer set with no Removing/Removed event. Target is the potential observer, Player is the SCP-173. Also fires for dummies. Observer removal on player leave or role change raises nothing. IsAllowed has no effect beyond skipping the add. |
| `Scp173Events.RemovingObserver` | per-frame-per-pair | Raised from Scp173ObserversTracker.UpdateObserver, which the per-frame update calls for EVERY hub in ReferenceHub.AllHubs on every SCP-173 instance (server only). Fires on the observed to not-observed edge. TRAP: cancelling keeps the target in the observer set (173 stays frozen versus that player) and re-raises the event every frame until the target is observing again. Cancelling this is the supported way to make 173 sticky-frozen, but it costs a handler call per frame per pair. |
| `PlayerEvents.Escaping` | per-frame | HOT while players stand in the escape zone: the game's per-frame update delegate loops ALL hubs every frame and ServerHandlePlayer raises Escaping for every player for whom CanEscape is true (an FPC role, including SCPs and spectator-like FPC roles, inside an escape zone), even when the computed scenario is None, so the handler is called every frame for those players although nothing will happen. Gate cheaply on EscapeScenario not being None before doing work. Setting EscapeScenario to None makes the event a no-op even with IsAllowed true. Setting NewRole to a value while the scenario stays valid changes the role granted. Fires for dummies. The zone is small, so the total rate is (players in zone) x server frame rate, typically well under 100/s but unbounded if many camp the surface. The delegate runs once per rendered server frame over all hubs; the escape zone list is mutable and CanEscape only needs an FPC role inside one of its bounds, so the event also fires every frame for FPC players whose scenario is None (the game returns None for a role that has been active under 10 seconds and for non-Human roles). SELF-INFLICTED PER-FRAME ESCAPE: if a handler cancels a player who has a valid scenario, nothing removes them from the zone, so the event refires every frame for the whole stay. Worse, if Escaping is allowed but PlayerEvents.ChangingRole cancels the ServerSetRole call, the player stays in the zone and the sequence escape message, achievements hook and Escaped event repeats EVERY FRAME. Allocation: a PlayerEscapingEventArgs is built per player per frame before the event even when no handler is subscribed (UNVERIFIED whether LabApi skips on an empty invocation list; the args constructor still runs). Cost is bounded by players standing in the zone, normally only a few seconds at round end. |
| `PlayerEvents.StayingInHazard` | per-frame | Not cancellable and has no Player property (it implements IHazardEvent only). AffectedPlayers has no setter, but the list itself is mutable: remove entries from it to exempt players. It is a per-hazard event: the hazard update runs every frame when the server is active (temporary hazards go through the same update, so they raise it too), once per hazard instance that currently has at least one player already inside. Players who entered this very frame are not in the list (the enter effect happens first and they join the list next frame). The list is a pooled List<Player> returned to the pool right after the event: do NOT keep a reference. Removing a player from AffectedPlayers skips the stay effect for that player (tantrum slow, decontamination damage, etc.), the only effective lever. Allocation: one args object plus one pooled list rent per raise; Player.Get per affected hub is a dictionary lookup. The game returns before the event when no player is inside, so an empty hazard costs only the scan of all hubs (an in-area check per FPC hub per frame, hazards times players). Not a before/after event: the related events are EnteringHazard and LeavingHazard. |
| `PlayerEvents.TriggeringTesla` | per-frame | Runs in the physics step (fixed update) for every active gate that is not in its inactive period and for every ALIVE player who is in range while the gate is not already in progress. At Unity's default step of 0.02 s that is 50 times a second; the dedicated server's setting is UNVERIFIED. While a player stands in the trigger range and the handler cancels, it re-fires EVERY fixed step per such player (a cancelled trigger is retried each tick, so a cancel-everything handler on a crowded gate is hot). Also fires for non-human roles (SCPs, dummies) in range; role immunity is handled inside the range check: roles that cannot activate the shock and non-FPC roles return false before the event. The loop stops for a gate once one player was allowed, so at most one allowed trigger per gate per tick. Hot only when players are inside gate range; on an idle server: zero. |
| `PlayerEvents.UsedRadio` | per-frame | Raised every frame for EVERY radio item that is usable (switched on with battery left) and whose UsingRadio was not cancelled, on the server, including radios not currently held: the update has no held-item gate (the held check only guards the status message), so any switched-on radio in an inventory keeps draining. It therefore scales with the number of switched-on radios, e.g. 40 radios at 60 fps is 2400 events/s. Carries the drain amount applied. |
| `PlayerEvents.UsingIntercom` | per-frame | Fires from the intercom update every frame while at least one human within intercom range is transmitting voice (the check short-circuits on range, human role, sending voice and not being muted, all before the event). In the Ready state the scan stops at the first player that passes, so cancelled players are re-evaluated EVERY frame (a cancel handler fires per frame per qualifying speaker); in the InUse state the current speaker is checked every frame. An idle intercom costs only the cheap pre-checks, no events. Only one intercom exists (singleton). The State arg is the current state; the check is only reached from the Ready and InUse branches, never Starting, so State is Ready or InUse in practice. Its setter is ignored. Cancel in InUse does not end the session at once: a failed check means the sustain timer is not restarted, and the session continues until the sustain time (a prefab value, UNVERIFIED) or the speech timer expires, then Cooldown and UsedIntercom. Cancelling every frame therefore does not silence instantly; to block use cancel in Ready. |
| `PlayerEvents.UsingRadio` | per-frame | Per frame per switched-on radio with battery (see UsedRadio). The default Drain scales with frame time and with the radio's range level (talking costs more than idle). Cancelling means no drain at all this frame. Setting Drain to 0 gives an infinite battery, but still costs the per-frame handler. Efficient infinite-battery alternative: keep the handler trivial (no allocations) or avoid subscribing and set the battery directly. |
| `Scp106Events.ChangedVigor` | per-frame | HOT, see ChangingVigor. NewValue is the value the setter received BEFORE it is clamped to the 0..1 range, so it can be below 0 or above 1 while the stat itself holds the clamped value. Fires every frame even when the clamped stat did not change. Raised only when ChangingVigor was NOT cancelled. |
| `Scp106Events.ChangingVigor` | per-frame | HOT: raised on EVERY server frame per active SCP-106 while stalking (vigor drain) and for about 2 s after moving more than 5 m while not stalking (vigor regeneration). Value is the NEW absolute value (current plus or minus the frame's delta), clamped only after the handler. Cancelling freezes vigor, so stalking never drains and becomes infinite because the vigor-zero exit is never reached. The update runs on any 106, including dummy-driven ones. Also raised with no real change (for example when vigor is already clamped). The setter throws InvalidOperationException on a client. Not raised while the sinkhole animation is running, and an idle, non-stalking 106 that has not moved more than 5 m in the last 2 s raises nothing. Cost per call is two EventArgs allocations plus a Player.Get lookup per frame per active 106 before any handler runs, so keep handlers allocation-free and return early when Value equals OldValue. Rare non-per-frame sources also exist: the Hunter's Atlas teleport cost and the Scp106Attack vigor gain of 0.3. |
| `PlayerEvents.ReceivingVoiceMessage` | high-rate-network | Per voice packet x per listener: ServerReceiveMessage is the Mirror handler for every VoiceMessage (Opus frames of 480 samples at 48 kHz, i.e. 100 packets per second per talking player, see SendingVoiceMessage). The game loops over all hubs and allocates a PlayerReceivingVoiceMessageEventArgs (its constructor calls Player.Get) for EVERY hub with an IVoiceRole, dummies included, even with no subscriber. With N talkers and 60 listeners that is about N x 100 x 60 raises per second. Player = the LISTENER, Sender = Speaker. Message is a ref property to a struct (no setter; edit its fields in place): editing Message.Channel changes the channel for that listener; the game pre-sets the channel from its receive validation, so a Channel of None means 'not audible' and setting IsAllowed to true is ignored then. Because the same message struct is reused across listeners in the loop, non-Channel edits may leak to following listeners (UNVERIFIED). Related, but not a before/after pair: SendingVoiceMessage, raised once per packet before the per-listener loop. Keep handlers allocation-free. |
| `PlayerEvents.SendingVoiceMessage` | high-rate-network | Mirror handler for every VoiceMessage packet from each talking client. Message is a ref property without a setter: mutate its fields in place. A voice packet carries 10 ms of audio (480 samples at 48 kHz), so a speaker sends about 100 packets/s and 60 simultaneous talkers is thousands per second; with push-to-talk usually far fewer. Fires only after the cheap gates passed: the speaker is not null and matches the connection, has a voice role, passes the rate limit and the mute check, and the channel validation returned something other than None. After this event the game loops ALL hubs and raises ReceivingVoiceMessage per receiver (O(players) per packet): that is the real cost multiplier. The game caps each speaker at roughly 256 packets/s, so a client cannot exceed that. The LabAPI args object is allocated per raise (it is a class); the Player lookup for the speaker in its constructor is a dictionary lookup, not an allocation. Anything else in the handler multiplies at this rate. Changing Message.Channel here is overwritten per receiver afterwards, so channel changes are NOT honoured for receivers. |

## Burst events (per shot / per hit)

Not hot by the definition above, but a firefight raises them in bursts. Keep the handler allocation-free
and early-out; do not log or build strings per raise.

- `PlayerEvents.CheckedHitmarker`
- `PlayerEvents.DamagedShootingTarget`
- `PlayerEvents.DamagedWindow`
- `PlayerEvents.DamagingShootingTarget`
- `PlayerEvents.DamagingWindow`
- `PlayerEvents.Hurt`
- `PlayerEvents.Hurting`
- `PlayerEvents.PlacedBlood`
- `PlayerEvents.PlacedBulletHole`
- `PlayerEvents.PlacingBlood`
- `PlayerEvents.PlacingBulletHole`
- `PlayerEvents.SendingHitmarker`
- `PlayerEvents.SentHitmarker`
- `PlayerEvents.ShootingWeapon`
- `PlayerEvents.ShotWeapon`

## Declared but never raised by the game (subscribing does nothing)

- `PlayerEvents.SearchedAmmo` — Declared but never raised: LabAPI defines the handler and its dispatcher, but nothing in the game build calls it, so subscribing does nothing. The game's ammo search completion raises SearchedPickup instead. Use SearchedPickup (check that Pickup is an AmmoPickup) or PickedUpAmmo.
- `PlayerEvents.SearchedArmor` — Declared but never raised: the dispatcher has no caller in the game build, so subscribing does nothing. The game's armor search completion raises SearchedPickup, then PickingUpArmor.
- `ServerEvents.CassieAnnounced` — Declared but never raised in 14.2.7 (the only reference to its raise method is the ServerEvents wrapper definition).
- `ServerEvents.CassieAnnouncing` — Event never raised, so every setter is moot. Declared but dead in game 14.2.7: the CASSIE system was rewritten (CassieAnnouncement and CassieAnnouncementDispatcher.AddToQueue) and nothing calls ServerEvents.OnCassieAnnouncing. Subscribing never triggers. Use CassieQueuingScpTermination for SCP-death lines; for other lines you must Harmony-patch CassieAnnouncementDispatcher.AddToQueue. The LabApi.Examples project still lists it, which is misleading.

## Events that are hot only under conditions

- `PlayerEvents.Hurt` — Per-frame per victim in the same cases as Hurting (SCP-3114 Strangled effect, MicroHID primary fire, per-tick damage-over-time effects) and once per bullet/pellet; additionally raised when ApplyDamage returned Nothing (the event is raised before the game checks the handler output).
- `PlayerEvents.Hurting` — Per-frame per victim in these cases: the SCP-3114 Strangled effect (it deals time-scaled damage every frame), MicroHID primary fire (damage every server update while firing; the broken and charged fire modes deal damage through HitregUtils), plus per-tick damage-over-time effects (Bleeding, Poisoned, Hemorrhage, Scp207, Asphyxiated, Decontaminating, SCP-244 Hypothermia) and every bullet/pellet. Steady-state baseline is low but bursts of tens to hundreds per second per server are possible; keep the handler allocation-free and early-out. Not hot by default; hot during those scenarios.

## Setters the game ignores (assigning them has no effect)

Always ignored: `ignoredSetters` in the JSON. `scpsl-lint` SL003 reports an assignment to one of these.

- `PlayerEvents.DamagingShootingTarget`: DamageHandler
- `PlayerEvents.DamagingWindow`: DamageHandler
- `PlayerEvents.DetectedByScp1344`: Target
- `PlayerEvents.DroppingAmmo`: Type, Amount
- `PlayerEvents.Dying`: DamageHandler
- `PlayerEvents.InteractingGenerator`: ColliderId
- `PlayerEvents.Muting`: IsIntercom
- `PlayerEvents.PlacedBlood`: RaycastStart
- `PlayerEvents.PlacedBulletHole`: DecalType
- `PlayerEvents.PlacingBulletHole`: DecalType
- `PlayerEvents.ReceivingLoadout`: InventoryReset
- `PlayerEvents.SendingAttachmentsPrefs`: FirearmType
- `PlayerEvents.SpawningRagdoll`: DamageHandler
- `PlayerEvents.TogglingRadio`: NewState
- `PlayerEvents.Unmuting`: IsIntercom
- `PlayerEvents.UsingIntercom`: State
- `Scp049Events.ResurrectingBody`: Player
- `Scp079Events.ChangedCamera`: Camera
- `Scp173Events.Snapping`: Target
- `Scp173Events.Teleported`: IsAllowed
- `Scp939Events.Attacked`: Damage
- `ServerEvents.BlastDoorChanging`: NewState
- `ServerEvents.ItemSpawning`: ItemType

## Setters the game ignores only in some situations

`conditionallyIgnoredSetters` in the JSON. `scpsl-lint` reports an assignment to one of these as SL003 at info
severity and quotes the condition; the setter works in the common case, so read the condition before relying on it either way.

- `PlayerEvents.Hurting` `DamageHandler`: ignored when the property is reassigned to a different handler object; mutating the original handler in place still takes effect
- `PlayerEvents.PreAuthenticating` `CanJoin`: ignored when the event is cancelled, because CanJoin is only read when IsAllowed stays true
- `PlayerEvents.PreAuthenticating` `CustomReject`: ignored when IsAllowed stays true, because the rejection payload is only used when the event is cancelled
- `PlayerEvents.PreAuthenticating` `ForceReject`: ignored when IsAllowed stays true, because the force flag is only used when the event is cancelled
- `PlayerEvents.ReceivingVoiceMessage` `IsAllowed`: ignored when the message's Channel is None, because the game treats the message as not audible and sends nothing regardless of IsAllowed
- `PlayerEvents.SendingVoiceMessage` `IsAllowed`: ignored when the message channel is None: the game does not forward the packet even when the event is allowed
- `PlayerEvents.Uncuffing` `IsAllowed`: ignored when the uncuffer is on the SCP team: the game rejects the uncuff regardless of the event result, so setting it to true cannot grant it
- `PlayerEvents.UpdatingEffect` `Duration`: ignored when the change comes through ServerSetState or EnableEffect: the game applies its own duration right after the event and overwrites the handler's value
- `Scp079Events.LevelingUp` `IsAllowed`: ignored when you want to block the level gain itself: the tier counter is already raised before the event, so false only suppresses the tier-changed notification and LeveledUp
- `Scp127Events.Talking` `Priority`: ignored when VoiceLine is not also changed to a different voice line, because Priority is only applied together with a new voice line
- `Scp939Events.Attacking` `Target`: ignored when the attack comes from the secondary lunge site: the game damages the original victim there, and the new target only affects the loop's skip check
- `ServerEvents.CommandExecuted` `ExecutedSuccessfully`: ignored when the command type is Client: the event is raised after the reply was already sent to the player, so the game never reads the value back
- `ServerEvents.CommandExecuted` `Response`: ignored when the command type is Client: the event is raised after the reply was already sent to the player, so the game never reads the value back
- `ServerEvents.CommandExecuting` `Command`: ignored when CommandFound is false: dispatch keeps using the original lookup result, so assigning a command for an unknown name does not make it run

## All events by category

`Pair`: `→ X` means X is the after-event raised once the action happened, `← X` means X is the before-event (the -ing event). Empty: no single before/after partner.
`Ignored setters`: always ignored; a trailing `*` marks a setter ignored only in some situations (listed above).

### ObjectiveEvents

| Event | Cancel | Frequency | Pair | Ignored setters | Raised from |
|---|---|---|---|---|---|
| ActivatedGeneratorCompleted |  | per-action | ← ActivatingGeneratorCompleting |  | GeneratorActivatedObjective.OnGeneratorEngaged (subscribed to Scp079Generator.OnGeneratorEngaged) |
| ActivatingGeneratorCompleting | yes | per-action | → ActivatedGeneratorCompleted |  | GeneratorActivatedObjective.OnGeneratorEngaged (subscribed to Scp079Generator.OnGeneratorEngaged) |
| Completed |  | per-action | ← Completing |  | EscapeObjective; GeneratorActivatedObjective; HumanDamageObjective; HumanKillObjective; ScpItemPickupObjective |
| Completing | yes | per-action | → Completed |  | EscapeObjective; GeneratorActivatedObjective; HumanDamageObjective; HumanKillObjective; ScpItemPickupObjective |
| DamagedScpCompleted |  | per-action | ← DamagingScpCompleting |  | HumanDamageObjective.OnPlayerDamaged (subscribed to PlayerStats.OnAnyPlayerDamaged) |
| DamagingScpCompleting | yes | per-action | → DamagedScpCompleted |  | HumanDamageObjective.OnPlayerDamaged (subscribed to PlayerStats.OnAnyPlayerDamaged) |
| EscapedCompleted |  | per-action | ← EscapingCompleting |  | EscapeObjective.OnServerRoleSet (subscribed to PlayerRoleManager.OnServerRoleSet) |
| EscapingCompleting | yes | per-action | → EscapedCompleted |  | EscapeObjective.OnServerRoleSet (subscribed to PlayerRoleManager.OnServerRoleSet) |
| KilledEnemyCompleted |  | per-action | ← KillingEnemyCompleting |  | HumanKillObjective.OnKill (subscribed to PlayerStats.OnAnyPlayerDied) |
| KillingEnemyCompleting | yes | per-action | → KilledEnemyCompleted |  | HumanKillObjective.OnKill (subscribed to PlayerStats.OnAnyPlayerDied) |
| PickedScpItemCompleted |  | per-action | ← PickingScpItemCompleting |  | ScpItemPickupObjective.OnItemAdded (inventory item-added callback) |
| PickingScpItemCompleting | yes | per-action | → PickedScpItemCompleted |  | ScpItemPickupObjective.OnItemAdded (inventory item-added callback) |

### PlayerEvents

| Event | Cancel | Frequency | Pair | Ignored setters | Raised from |
|---|---|---|---|---|---|
| ActivatedGenerator |  | per-action | ← ActivatingGenerator |  | Scp079Generator.ServerInteract (Switch collider, after Activating toggled to true) |
| ActivatingGenerator | yes | per-action | → ActivatedGenerator |  | Scp079Generator.ServerInteract (Switch collider, only when not already Activating) |
| AimedWeapon |  | per-action |  |  | LinearAdsModule.ServerProcessCmd |
| Banned |  | per-action | ← Banning |  | BanPlayer.BanUser(Footprint,ICommandSender,string,long) |
| Banning | yes | per-action | → Banned |  | BanPlayer.BanUser(Footprint,ICommandSender,string,long) |
| CancelledUsingItem |  | per-action | ← CancellingUsingItem |  | UsableItemsController.ServerReceivedStatus (StatusType.Cancel branch) |
| CancellingUsingItem | yes | per-action | → CancelledUsingItem |  | UsableItemsController.ServerReceivedStatus (StatusType.Cancel branch) |
| ChangedAttachments |  | per-action | ← ChangingAttachments |  | AttachmentsServerHandler.ServerReceiveChangeRequest |
| ChangedBadgeVisibility |  | on-request | ← ChangingBadgeVisibility |  | ServerRoles.RefreshGlobalTag; ServerRoles.RefreshLocalTag; ServerRoles.TryHideTag |
| ChangedItem |  | per-action | ← ChangingItem |  | Inventory.ServerSelectItem |
| ChangedNickname |  | on-request | ← ChangingNickname |  | NicknameSync.DisplayName (property setter) |
| ChangedRadioRange |  | per-action | ← ChangingRadioRange |  | RadioItem.ServerProcessCmd (RadioCommand.ChangeRange) |
| ChangedRole |  | per-lifecycle | ← ChangingRole |  | PlayerRoleManager.ServerSetRole |
| ChangedSpectator |  | per-action |  |  | SpectatorNetworking.Init (NetworkServer handler for SpectatedNetIdSyncMessage) |
| ChangingAttachments | yes | per-action | → ChangedAttachments |  | AttachmentsServerHandler.ServerReceiveChangeRequest |
| ChangingBadgeVisibility | yes | on-request | → ChangedBadgeVisibility |  | ServerRoles.RefreshGlobalTag; ServerRoles.RefreshLocalTag; ServerRoles.TryHideTag (bypass-bans local branch); ServerRoles.TryHideTag (custom text branch); ServerRoles.TryHideTag (global badge branch) |
| ChangingItem | yes | per-action | → ChangedItem |  | Inventory.ServerSelectItem |
| ChangingNickname | yes | on-request | → ChangedNickname |  | NicknameSync.DisplayName (property setter) |
| ChangingRadioRange | yes | per-action | → ChangedRadioRange |  | RadioItem.ServerProcessCmd (RadioCommand.ChangeRange) |
| ChangingRole | yes | per-lifecycle | → ChangedRole |  | PlayerRoleManager.ServerSetRole |
| CheckedHitmarker |  | per-action burst |  |  | Hitmarker.CheckHitmarkerPerms (result:true); Hitmarker.CheckHitmarkerPerms (role IHitmarkerPreventer, result:false); Hitmarker.CheckHitmarkerPerms (status effect preventer, result:false) |
| ClosedGenerator |  | per-action | ← ClosingGenerator |  | Scp079Generator.ServerInteract (Door collider, after the Open flag toggled off) |
| ClosingGenerator | yes | per-action | → ClosedGenerator |  | Scp079Generator.ServerInteract (Door collider, generator Unlocked and Open) |
| Cuffed |  | per-action | ← Cuffing |  | DisarmingHandlers (disarm message handler, cuff branch) |
| Cuffing | yes | per-action | → Cuffed |  | DisarmingHandlers (disarm message handler, cuff branch) |
| DamagedShootingTarget |  | per-action burst | ← DamagingShootingTarget |  | ShootingTarget.Damage |
| DamagedWindow |  | per-action burst | ← DamagingWindow |  | BreakableWindow.Damage |
| DamagingShootingTarget | yes | per-action burst | → DamagedShootingTarget | `DamageHandler` | ShootingTarget.Damage |
| DamagingWindow | yes | per-action burst | → DamagedWindow | `DamageHandler` | BreakableWindow.Damage |
| DeactivatedGenerator |  | per-action | ← DeactivatingGenerator |  | Scp079Generator.ServerInteract (CancelButton collider); Scp079Generator.ServerInteract (Switch collider, Activating became false) |
| DeactivatingGenerator | yes | per-action | → DeactivatedGenerator |  | Scp079Generator.ServerInteract (CancelButton collider); Scp079Generator.ServerInteract (Switch collider, when Activating) |
| Death |  | per-lifecycle | ← Dying |  | PlayerStats.DealDamage |
| DetectedByScp1344 |  | per-action |  | `Target` | Scp1344Detected.ServerRegisterObserver |
| DroppedAmmo |  | per-action | ← DroppingAmmo |  | InventoryExtensions.ServerDropAmmo (inside the pickup-spawn loop) |
| DroppedItem |  | per-action | ← DroppingItem |  | Inventory.UserCode_CmdDropItem__UInt16__Boolean |
| DroppingAmmo | yes | per-action | → DroppedAmmo | `Type`, `Amount` | InventoryExtensions.ServerDropAmmo |
| DroppingItem | yes | per-action | → DroppedItem |  | Inventory.UserCode_CmdDropItem__UInt16__Boolean |
| DryFiredWeapon |  | per-action | ← DryFiringWeapon |  | AutomaticActionModule.UpdateServer; DoubleActionModule.FireDry |
| DryFiringWeapon | yes | per-action | → DryFiredWeapon |  | AutomaticActionModule.UpdateServer; DoubleActionModule.FireDry |
| Dying | yes | per-lifecycle | → Death | `DamageHandler` | PlayerStats.DealDamage (only when the damage handler's output is Death) |
| EnteredHazard |  | per-action | ← EnteringHazard |  | Scp939AmnesticCloudInstance.OnEnter; SinkholeEnvironmentalHazard.OnEnter; TantrumEnvironmentalHazard.OnEnter |
| EnteredPocketDimension |  | per-action | ← EnteringPocketDimension |  | PocketCorroding.Enabled |
| EnteringHazard | yes | per-action | → EnteredHazard |  | EnvironmentalHazard.OnEnter (called from UpdateTargets in the per-frame Update) |
| EnteringPocketDimension | yes | per-action | → EnteredPocketDimension |  | PocketCorroding.Enabled |
| Escaped |  | per-action | ← Escaping |  | Escape.ServerHandlePlayer |
| Escaping | yes | per-frame **HOT** | → Escaped |  | Escape.ServerHandlePlayer (called per player from a StaticUnityMethods.OnUpdate delegate) |
| FlippedCoin |  | per-action | ← FlippingCoin |  | Coin.ServerProcessCmd |
| FlippingCoin | yes | per-action | → FlippedCoin |  | Coin.ServerProcessCmd |
| GroupChanged |  | per-lifecycle | ← GroupChanging |  | ServerRoles.FinalizeSetGroup |
| GroupChanging | yes | per-lifecycle | → GroupChanged |  | ServerRoles.SetGroup |
| Hurt |  | per-action burst cond-hot | ← Hurting |  | PlayerStats.DealDamage |
| Hurting | yes | per-action burst cond-hot | → Hurt | `DamageHandler`* | PlayerStats.DealDamage |
| IdledTesla |  | per-action | ← IdlingTesla |  | TeslaGateController.FixedUpdate |
| IdlingTesla | yes | per-frame-per-pair **HOT** | → IdledTesla |  | TeslaGateController.FixedUpdate |
| InspectedItem |  | on-request | ← InspectingItem |  | DrawAndInspectorModule; JailbirdItem; Scp1344NetworkHandler; Scp1509Item; SimpleInspectorModule |
| InspectedKeycard |  | on-request | ← InspectingKeycard |  | KeycardItem.ServerProcessCmd |
| InspectingItem | yes | on-request | → InspectedItem |  | DrawAndInspectorModule.ServerProcessCmd; JailbirdItem.ServerProcessCmd (Inspect case); Scp1344NetworkHandler.TryInspect; Scp1509Item.ServerProcessCmd (Inspect case); SimpleInspectorModule.ServerProcessCmd |
| InspectingKeycard | yes | on-request | → InspectedKeycard |  | KeycardItem.ServerProcessCmd (MsgType.Inspect, start=true) |
| InteractedDoor |  | per-action | ← InteractingDoor |  | DoorVariant.ServerInteract; DoorVariant.ServerInteract (locked, denied) |
| InteractedElevator |  | per-action | ← InteractingElevator |  | ElevatorChamber.ServerInteract |
| InteractedGenerator |  | per-action | ← InteractingGenerator |  | Scp079Generator.ServerInteract |
| InteractedLocker |  | per-action | ← InteractingLocker |  | Locker.ServerInteract (denied); Locker.ServerInteract (opened/closed) |
| InteractedScp330 |  | per-action | ← InteractingScp330 |  | Scp330Interobject.ServerInteract |
| InteractedShootingTarget |  | per-action | ← InteractingShootingTarget |  | ShootingTarget.ServerInteract |
| InteractedToy |  | per-action |  |  | InvisibleInteractableToy.ServerInteract |
| InteractedWarheadLever |  | per-action | ← InteractingWarheadLever |  | AlphaWarheadNukesitePanel.ServerInteract |
| InteractingDoor | yes | per-action | → InteractedDoor |  | DoorVariant.ServerInteract; DoorVariant.TryResolveLock |
| InteractingElevator | yes | per-action | → InteractedElevator |  | ElevatorChamber.ServerInteract |
| InteractingGenerator | yes | per-action | → InteractedGenerator | `ColliderId` | Scp079Generator.ServerInteract |
| InteractingLocker | yes | per-action | → InteractedLocker |  | Locker.ServerInteract |
| InteractingScp330 | yes | per-action | → InteractedScp330 |  | Scp330Interobject.ServerInteract |
| InteractingShootingTarget | yes | per-action | → InteractedShootingTarget |  | ShootingTarget.ServerInteract |
| InteractingWarheadLever | yes | per-action | → InteractedWarheadLever |  | AlphaWarheadNukesitePanel.ServerInteract |
| ItemUsageEffectsApplying | yes | per-action |  |  | UsableItemsController.Update |
| Joined |  | per-lifecycle |  |  | NicknameSync.CmdSetNick (offline mode path); PlayerAuthenticationManager (auth response processing) |
| Jumped |  | per-action |  |  | FpcJumpController.ForceJump; FpcJumpController.ProcessJump |
| Kicked |  | per-action | ← Kicking |  | BanPlayer.KickUser |
| Kicking | yes | per-action | → Kicked |  | BanPlayer.KickUser(ReferenceHub,ICommandSender,string) |
| LeavingHazard | yes | per-action | → LeftHazard |  | EnvironmentalHazard.OnExit |
| LeavingPocketDimension | yes | per-action | → LeftPocketDimension |  | PocketDimensionTeleport.TryExit; PocketDimensionTeleport.TryKill |
| Left |  | per-lifecycle |  |  | ReferenceHub.OnDestroy |
| LeftHazard |  | per-action | ← LeavingHazard |  | Scp939AmnesticCloudInstance.OnExit; SinkholeEnvironmentalHazard.OnExit; TantrumEnvironmentalHazard.OnExit |
| LeftPocketDimension |  | per-action | ← LeavingPocketDimension |  | PocketDimensionTeleport.Exit; PocketDimensionTeleport.Kill |
| MovementStateChanged |  | per-action |  |  | FpcSyncData.TryApply |
| Muted |  | on-request | ← Muting |  | IntercomMuteCommand.Execute; MuteCommand.Execute |
| Muting | yes | on-request | → Muted | `IsIntercom` | IntercomMuteCommand.Execute; MuteCommand.Execute |
| OpenedGenerator |  | per-action | ← OpeningGenerator |  | Scp079Generator.ServerInteract |
| OpeningGenerator | yes | per-action | → OpenedGenerator |  | Scp079Generator.ServerInteract |
| PickedUpAmmo |  | per-action | ← PickingUpAmmo |  | AmmoSearchCompletor.Complete |
| PickedUpArmor |  | per-action | ← PickingUpArmor |  | ArmorSearchCompletor.Complete |
| PickedUpItem |  | per-action | ← PickingUpItem |  | ItemSearchCompletor.Complete; Scp244SearchCompletor.Complete |
| PickedUpScp330 |  | per-action | ← PickingUpScp330 |  | Scp330SearchCompletor.Complete |
| PickingUpAmmo | yes | per-action | → PickedUpAmmo |  | AmmoSearchCompletor.Complete |
| PickingUpArmor | yes | per-action | → PickedUpArmor |  | ArmorSearchCompletor.Complete |
| PickingUpItem | yes | per-action | → PickedUpItem |  | ItemSearchCompletor.Complete; Scp244SearchCompletor.Complete |
| PickingUpScp330 | yes | per-action | → PickedUpScp330 |  | Scp330SearchCompletor.Complete |
| PlacedBlood |  | per-action burst | ← PlacingBlood | `RaycastStart` | ImpactEffectsModule.ServerSendPlayerHit |
| PlacedBulletHole |  | per-action burst | ← PlacingBulletHole | `DecalType` | ImpactEffectsModule.ServerSendImpactDecal |
| PlacingBlood | yes | per-action burst | → PlacedBlood |  | ImpactEffectsModule.ServerSendPlayerHit |
| PlacingBulletHole | yes | per-action burst | → PlacedBulletHole | `DecalType` | ImpactEffectsModule.ServerSendImpactDecal |
| PreAuthenticated |  | per-lifecycle | ← PreAuthenticating |  | CustomLiteNetLib4MirrorTransport.ProcessConnectionRequest (offline branch); CustomLiteNetLib4MirrorTransport.ProcessConnectionRequest (online branch) |
| PreAuthenticating | yes | per-lifecycle | → PreAuthenticated | `CanJoin`*, `CustomReject`*, `ForceReject`* | CustomLiteNetLib4MirrorTransport.ProcessConnectionRequest (offline branch, online mode off); CustomLiteNetLib4MirrorTransport.ProcessConnectionRequest (online branch, after preauth token validation) |
| ProcessedJailbirdMessage |  | per-action | ← ProcessingJailbirdMessage |  | JailbirdItem.ServerProcessCmd |
| ProcessedScp1509Message |  | per-action | ← ProcessingScp1509Message |  | Scp1509Item.ServerProcessCmd |
| ProcessingJailbirdMessage | yes | per-action | → ProcessedJailbirdMessage |  | JailbirdItem.ServerProcessCmd |
| ProcessingScp1509Message | yes | per-action | → ProcessedScp1509Message |  | Scp1509Item.ServerProcessCmd |
| RaPlayerListAddedPlayer |  | on-request | ← RaPlayerListAddingPlayer |  | RaPlayerList.ReceiveData |
| RaPlayerListAddingPlayer | yes | on-request | → RaPlayerListAddedPlayer |  | RaPlayerList.ReceiveData (per listed hub) |
| ReceivedAchievement |  | per-action |  |  | AchievementHandlerBase.ServerAchieve |
| ReceivedLoadout |  | per-lifecycle | ← ReceivingLoadout |  | InventoryItemProvider.ServerGrantLoadout |
| ReceivingLoadout | yes | per-lifecycle | → ReceivedLoadout | `InventoryReset` | InventoryItemProvider.ServerGrantLoadout |
| ReceivingVoiceMessage | yes | high-rate-network **HOT** |  | `IsAllowed`* | VoiceTransceiver.ServerReceiveMessage (inner loop per listener hub) |
| ReloadedWeapon |  | per-action | ← ReloadingWeapon |  | AnimatorReloaderModuleBase.StopReloadingAndUnloading |
| ReloadingWeapon | yes | per-action | → ReloadedWeapon |  | AnimatorReloaderModuleBase.ServerProcessCmd (Reload header) |
| ReportedCheater |  | per-action | ← ReportingCheater |  | CheaterReport.UserCode_CmdReport |
| ReportedPlayer |  | per-action | ← ReportingPlayer |  | CheaterReport.UserCode_CmdReport |
| ReportingCheater | yes | per-action | → ReportedCheater |  | CheaterReport.UserCode_CmdReport (report to Global Moderation branch) |
| ReportingPlayer | yes | per-action | → ReportedPlayer |  | CheaterReport.UserCode_CmdReport (local report branch, not sent to Global Moderation) |
| RequestedCustomRaInfo |  | on-request |  |  | RaPlayer.ReceiveData (resolved player list is empty) |
| RequestedRaPlayerInfo |  | on-request | ← RequestingRaPlayerInfo |  | RaPlayer.ReceiveData (single branch) |
| RequestedRaPlayerList |  | on-request | ← RequestingRaPlayerList |  | RaPlayerList.ReceiveData |
| RequestedRaPlayersInfo |  | on-request | ← RequestingRaPlayersInfo |  | RaPlayer.ReceiveData (multi-select branch) |
| RequestingRaPlayerInfo | yes | on-request | → RequestedRaPlayerInfo |  | RaPlayer.ReceiveData (single player selected) |
| RequestingRaPlayerList | yes | on-request | → RequestedRaPlayerList |  | RaPlayerList.ReceiveData |
| RequestingRaPlayersInfo | yes | on-request | → RequestedRaPlayersInfo |  | RaPlayer.ReceiveData (more than one player selected) |
| RoomChanged |  | per-action |  |  | CurrentRoomPlayerCache.ValidateCache (called from Update and from TryGetCurrent/TryGetLastValid) |
| Scp1509Resurrected |  | per-action | ← Scp1509Resurrecting |  | Scp1509Item.ServerProcessKill |
| Scp1509Resurrecting | yes | per-action | → Scp1509Resurrected |  | Scp1509Item.ServerProcessKill |
| SearchToyAborted |  | per-action |  |  | InvisibleInteractableToy.ServerHandleAbort |
| SearchedAmmo |  | never-fired | ← SearchingAmmo |  |  |
| SearchedArmor |  | never-fired | ← SearchingArmor |  |  |
| SearchedPickup |  | per-action | ← SearchingPickup |  | AmmoSearchCompletor.Complete; ArmorSearchCompletor.Complete; ItemSearchCompletor.Complete (through the shared base-class completion); PickupSearchCompletor.Complete (base); Scp244SearchCompletor.Complete; Scp330SearchCompletor.Complete |
| SearchedToy |  | per-action | ← SearchingToy |  | InvisibleInteractableToy.InteractableToySearchCompletor.Complete |
| SearchingAmmo | yes | per-action | → SearchedAmmo |  | AmmoSearchCompletor.ValidateStart |
| SearchingArmor | yes | per-action | → SearchedArmor |  | ArmorSearchCompletor.ValidateStart |
| SearchingPickup | yes | per-action | → SearchedPickup |  | PickupSearchCompletor.ValidateStart |
| SearchingToy | yes | per-action | → SearchedToy |  | InvisibleInteractableToy.InteractableToySearchCompletor.ValidateStart |
| SendingAttachmentsPrefs | yes | per-action | → SentAttachmentsPrefs | `FirearmType` | AttachmentsServerHandler.ServerReceivePreference |
| SendingHitmarker | yes | per-action burst | → SentHitmarker |  | Hitmarker.SendHitmarkerDirectly(ReferenceHub,float,bool,HitmarkerType) |
| SendingVoiceMessage | yes | high-rate-network **HOT** |  | `IsAllowed`* | VoiceTransceiver.ServerReceiveMessage |
| SentAttachmentsPrefs |  | per-action | ← SendingAttachmentsPrefs |  | AttachmentsServerHandler.ServerReceivePreference |
| SentHitmarker |  | per-action burst | ← SendingHitmarker |  | Hitmarker.SendHitmarkerDirectly |
| ShootingWeapon | yes | per-action burst | → ShotWeapon |  | AutomaticActionModule.UpdateServer; DisruptorActionModule.ServerProcessStartCmd; DoubleActionModule.FireLive (server side only); PumpActionModule.UpdateServer |
| ShotWeapon |  | per-action burst | ← ShootingWeapon |  | AutomaticActionModule.UpdateServer; DisruptorActionModule.ServerProcessStartCmd; DoubleActionModule.FireLive; PumpActionModule.UpdateServer |
| Spawned |  | per-lifecycle | ← Spawning |  | RoleSpawnpointManager.SetPosition (after the position is applied); RoleSpawnpointManager.SetPosition (spawnpoint flag unset or UseSpawnPoint false path) |
| SpawnedRagdoll |  | per-lifecycle | ← SpawningRagdoll |  | RagdollManager.ServerSpawnRagdoll(ReferenceHub,DamageHandlerBase) |
| Spawning | yes | per-lifecycle | → Spawned |  | RoleSpawnpointManager.SetPosition |
| SpawningRagdoll | yes | per-lifecycle | → SpawnedRagdoll | `DamageHandler` | RagdollManager.ServerSpawnRagdoll(ReferenceHub,DamageHandlerBase) |
| SpinnedRevolver |  | per-action | ← SpinningRevolver |  | RevolverRouletteModule.EndSpin |
| SpinningRevolver | yes | per-action | → SpinnedRevolver |  | RevolverRouletteModule.ServerTrySpin |
| StayingInHazard |  | per-frame **HOT** |  |  | EnvironmentalHazard.UpdateTargets |
| ThrewItem |  | per-action | ← ThrowingItem |  | Inventory.UserCode_CmdDropItem__UInt16__Boolean |
| ThrewProjectile |  | per-action | ← ThrowingProjectile |  | ThrowableItem.ServerProcessThrowConfirmation |
| ThrowingItem | yes | per-action | → ThrewItem |  | Inventory.UserCode_CmdDropItem__UInt16__Boolean |
| ThrowingProjectile | yes | per-action | → ThrewProjectile |  | ThrowableItem.ServerProcessThrowConfirmation |
| ToggledDisruptorFiringMode |  | per-action |  |  | DisruptorModeSelector.ServerProcessCmd |
| ToggledFlashlight |  | per-action | ← TogglingFlashlight |  | FlashlightNetworkHandler.ServerProcessMessage |
| ToggledNoclip |  | on-request | ← TogglingNoclip |  | FpcNoclipToggleMessage.ProcessMessage |
| ToggledRadio |  | per-action | ← TogglingRadio |  | RadioItem.ServerProcessCmd (disable); RadioItem.ServerProcessCmd (enable) |
| ToggledWeaponFlashlight |  | per-action | ← TogglingWeaponFlashlight |  | FlashlightAttachment.ServerProcessCmd |
| TogglingFlashlight | yes | per-action | → ToggledFlashlight |  | FlashlightNetworkHandler.ServerProcessMessage |
| TogglingNoclip | yes | on-request | → ToggledNoclip |  | FpcNoclipToggleMessage.ProcessMessage |
| TogglingRadio | yes | per-action | → ToggledRadio | `NewState` | RadioItem.ServerProcessCmd (Disable branch); RadioItem.ServerProcessCmd (Enable branch) |
| TogglingWeaponFlashlight | yes | per-action | → ToggledWeaponFlashlight |  | FlashlightAttachment.ServerProcessCmd |
| TriggeredTesla |  | per-action | ← TriggeringTesla |  | TeslaGateController.FixedUpdate |
| TriggeringTesla | yes | per-frame **HOT** | → TriggeredTesla |  | TeslaGateController.FixedUpdate |
| Uncuffed |  | per-action | ← Uncuffing |  | DisarmingHandlers.ServerProcessDisarmMessage |
| Uncuffing | yes | per-action | → Uncuffed | `IsAllowed`* | DisarmingHandlers.ServerProcessDisarmMessage |
| UnloadedWeapon |  | per-action | ← UnloadingWeapon |  | AnimatorReloaderModuleBase.StopReloadingAndUnloading |
| UnloadingWeapon | yes | per-action | → UnloadedWeapon |  | AnimatorReloaderModuleBase.ServerProcessCmd |
| UnlockedGenerator |  | per-action | ← UnlockingGenerator |  | Scp079Generator.ServerInteract |
| UnlockedWarheadButton |  | per-action | ← UnlockingWarheadButton |  | AlphaWarheadActivationPanel.ServerInteractKeycard |
| UnlockingGenerator | yes | per-action | → UnlockedGenerator |  | Scp079Generator.ServerInteract (Door collider branch, generator locked) |
| UnlockingWarheadButton | yes | per-action | → UnlockedWarheadButton |  | AlphaWarheadActivationPanel.ServerInteractKeycard |
| Unmuted |  | on-request | ← Unmuting |  | IntercomUnmuteCommand.Execute; UnmuteCommand.Execute |
| Unmuting | yes | on-request | → Unmuted | `IsIntercom` | IntercomUnmuteCommand.Execute; UnmuteCommand.Execute |
| UpdatedEffect |  | per-action | ← UpdatingEffect |  | StatusEffectBase.ForceIntensity |
| UpdatingEffect | yes | per-action | → UpdatedEffect | `Duration`* | StatusEffectBase.ForceIntensity; StatusEffectBase.ForceIntensity (reached from the Intensity setter, ServerSetState, DisableEffect, CokeBase, Scp1853) |
| UsedIntercom |  | per-action | ← UsingIntercom |  | Intercom.Update (transition from the in-use state to cooldown) |
| UsedItem |  | per-action | ← UsingItem |  | UsableItemsController.Update |
| UsedRadio |  | per-frame **HOT** | ← UsingRadio |  | RadioItem.Update |
| UsingIntercom | yes | per-frame **HOT** | → UsedIntercom | `State` | Intercom.CheckPlayer (called from Update in the Ready state for the first qualifying player, and in the InUse state for the current speaker) |
| UsingItem | yes | per-action | → UsedItem |  | Scp330NetworkHandler.ServerSelectCandy; UsableItemsController.ServerReceivedStatus (StatusType.Start) |
| UsingRadio | yes | per-frame **HOT** | → UsedRadio |  | RadioItem.Update |
| ValidatedVisibility |  | per-frame-per-pair **HOT** |  |  | FpcServerPositionDistributor.WriteAll |
| ZoneChanged |  | per-action |  |  | CurrentRoomPlayerCache.ValidateCache (called from Update every frame, and from TryGetCurrent) |

### Scp0492Events

| Event | Cancel | Frequency | Pair | Ignored setters | Raised from |
|---|---|---|---|---|---|
| ConsumedCorpse |  | per-action | ← ConsumingCorpse |  | ZombieConsumeAbility.ServerComplete |
| ConsumingCorpse | yes | per-action | → ConsumedCorpse |  | ZombieConsumeAbility.ServerComplete (from RagdollAbilityBase.Update when channel completes) |
| StartedConsumingCorpse |  | per-action | ← StartingConsumingCorpse |  | ZombieConsumeAbility.ServerProcessCmd |
| StartingConsumingCorpse | yes | per-action | → StartedConsumingCorpse |  | ZombieConsumeAbility.ServerValidateBegin (from RagdollAbilityBase.ServerProcessCmd) |

### Scp049Events

| Event | Cancel | Frequency | Pair | Ignored setters | Raised from |
|---|---|---|---|---|---|
| Attacked |  | per-action | ← Attacking |  | Scp049AttackAbility.ServerProcessCmd |
| Attacking | yes | per-action | → Attacked |  | Scp049AttackAbility.ServerProcessCmd |
| ResurrectedBody |  | per-action | ← ResurrectingBody |  | Scp049ResurrectAbility.ServerComplete |
| ResurrectingBody | yes | per-action | → ResurrectedBody | `Player` | Scp049ResurrectAbility.ServerComplete (from RagdollAbilityBase.Update, RagdollAbilityBase) |
| SenseKilledTarget |  | per-action |  |  | Scp049SenseAbility.ServerProcessKilledPlayer (called from Scp049DamageHandler ApplyDamage) |
| SenseLostTarget |  | per-action |  |  | Scp049SenseAbility.ServerLoseTarget (callers Update, OnServerHit) |
| StartingResurrection | yes | per-action |  |  | Scp049ResurrectAbility.ServerValidateBegin (called from RagdollAbilityBase in ServerProcessCmd) |
| UsedDoctorsCall |  | per-action | ← UsingDoctorsCall |  | Scp049CallAbility.ServerProcessCmd |
| UsedSense |  | per-action | ← UsingSense |  | Scp049SenseAbility.ServerProcessCmd |
| UsingDoctorsCall | yes | per-action | → UsedDoctorsCall |  | Scp049CallAbility.ServerProcessCmd |
| UsingSense | yes | per-action | → UsedSense |  | Scp049SenseAbility.ServerProcessCmd |

### Scp079Events

| Event | Cancel | Frequency | Pair | Ignored setters | Raised from |
|---|---|---|---|---|---|
| BlackedOutRoom |  | per-action | ← BlackingOutRoom |  | Scp079BlackoutRoomAbility.ServerProcessCmd |
| BlackedOutZone |  | per-action | ← BlackingOutZone |  | Scp079BlackoutZoneAbility.ServerProcessCmd |
| BlackingOutRoom | yes | per-action | → BlackedOutRoom |  | Scp079BlackoutRoomAbility.ServerProcessCmd |
| BlackingOutZone | yes | per-action | → BlackedOutZone |  | Scp079BlackoutZoneAbility.ServerProcessCmd |
| CancelledRoomLockdown |  | per-lifecycle | ← CancellingRoomLockdown |  | Scp079LockdownRoomAbility.ServerCancelLockdown |
| CancellingRoomLockdown | yes | per-lifecycle | → CancelledRoomLockdown |  | Scp079LockdownRoomAbility.ServerCancelLockdown (callers Update, ResetObject) |
| ChangedCamera |  | per-action | ← ChangingCamera | `Camera` | Scp079CurrentCameraSync.ServerProcessCmd |
| ChangingCamera | yes | per-action | → ChangedCamera |  | Scp079CurrentCameraSync.ServerProcessCmd |
| GainedExperience |  | per-action | ← GainingExperience |  | Scp079RewardManager.GrantExp |
| GainingExperience | yes | per-action | → GainedExperience |  | Scp079RewardManager.GrantExp (callers: TerminationRewards, HumanBlockingRewards, HidStoppedReward, TeammateProtectionRewards) |
| LeveledUp |  | per-action | ← LevelingUp |  | Scp079TierManager.AccessTierIndex setter |
| LevelingUp | yes | per-action | → LeveledUp | `IsAllowed`* | Scp079TierManager.AccessTierIndex setter (reached from TotalExp setter) |
| LockedDoor |  | per-action | ← LockingDoor |  | Scp079DoorLockChanger.ServerProcessCmd |
| LockedDownRoom |  | per-action | ← LockingDownRoom |  | Scp079LockdownRoomAbility.ServerProcessCmd |
| LockingDoor | yes | per-action | → LockedDoor |  | Scp079DoorLockChanger.ServerProcessCmd |
| LockingDownRoom | yes | per-action | → LockedDownRoom |  | Scp079LockdownRoomAbility.ServerProcessCmd |
| Pinged |  | per-action | ← Pinging |  | Scp079PingAbility.ServerProcessCmd |
| Pinging | yes | per-action | → Pinged |  | Scp079PingAbility.ServerProcessCmd |
| Recontained |  | per-round | ← Recontaining |  | Scp079Recontainer.TryKill079 |
| Recontaining | yes | per-round | → Recontained |  | Scp079Recontainer.TryKill079 (from BeginOvercharge) |
| UnlockedDoor |  | per-action | ← UnlockingDoor |  | Scp079DoorLockChanger.ServerUnlock |
| UnlockingDoor | yes | per-action | → UnlockedDoor |  | Scp079DoorLockChanger.ServerUnlock (callers: Update, signal-lost handler, ServerProcessCmd, OnDestroy, ResetObject, Scp079DoorLockReleaser, Scp079LockdownRoomAbility) |
| UsedTesla |  | per-action | ← UsingTesla |  | Scp079TeslaAbility.ServerProcessCmd |
| UsingTesla | yes | per-action | → UsedTesla |  | Scp079TeslaAbility.ServerProcessCmd |

### Scp096Events

| Event | Cancel | Frequency | Pair | Ignored setters | Raised from |
|---|---|---|---|---|---|
| AddedTarget |  | per-action | ← AddingTarget |  | Scp096TargetsTracker.AddTarget |
| AddingTarget | yes | per-action | → AddedTarget |  | Scp096TargetsTracker.AddTarget (callers: the per-frame target update, damage-based target adding, and the client RPC path) |
| ChangedState |  | per-action | ← ChangingState |  | Scp096StateController.SetRageState |
| ChangingState | yes | per-action | → ChangedState |  | Scp096StateController.SetRageState (callers: Scp096RageManager Distressed and Calming; Scp096RageCycleAbility Enraged and Docile) |
| Charged |  | per-action | ← Charging |  | Scp096ChargeAbility.ServerProcessCmd |
| Charging | yes | per-action | → Charged |  | Scp096ChargeAbility.ServerProcessCmd |
| Enraged |  | per-action | ← Enraging |  | Scp096RageManager.ServerEnrage |
| Enraging | yes | per-action | → Enraged |  | Scp096RageManager.ServerEnrage (caller Scp096RageCycleAbility ServerProcessCmd) |
| PriedGate |  | per-action | ← PryingGate |  | PryableDoor.TryPryGate |
| PryingGate | yes | per-action | → PriedGate |  | PryableDoor.TryPryGate (caller Scp096PrygateAbility ServerProcessCmd) |
| StartCrying | yes | per-action | → StartedCrying |  | Scp096TryNotToCryAbility IsActive setter (setting it to false; callers: Update, ServerProcessCmd and the cancel path) |
| StartedCrying |  | per-action | ← StartCrying |  | Scp096TryNotToCryAbility IsActive setter (setting it to false) |
| TriedNotToCry |  | per-action | ← TryingNotToCry |  | Scp096TryNotToCryAbility IsActive setter (setting it to true) |
| TryingNotToCry | yes | per-action | → TriedNotToCry |  | Scp096TryNotToCryAbility IsActive setter (setting it to true; caller ServerProcessCmd, which passes the validation result) |

### Scp106Events

| Event | Cancel | Frequency | Pair | Ignored setters | Raised from |
|---|---|---|---|---|---|
| ChangedStalkMode |  | per-action | ← ChangingStalkMode |  | Scp106StalkAbility.ServerSetStalk |
| ChangedSubmersionStatus |  | per-action | ← ChangingSubmersionStatus |  | Scp106SinkholeController.ServerSetSubmerged |
| ChangedVigor |  | per-frame **HOT** | ← ChangingVigor |  | Scp106VigorAbilityBase.VigorAmount setter (same per-frame callers as ChangingVigor) |
| ChangingStalkMode | yes | per-action | → ChangedStalkMode |  | Scp106StalkAbility.ServerSetStalk (callers: ServerProcessCmd; UpdateServerside when vigor hits 0) |
| ChangingSubmersionStatus | yes | per-action | → ChangedSubmersionStatus |  | Scp106SinkholeController.ServerSetSubmerged (called every frame from Update, once the target submerged state differs from the desired one) |
| ChangingVigor | yes | per-frame **HOT** | → ChangedVigor |  | Scp106VigorAbilityBase.VigorAmount setter (callers: Scp106StalkAbility per-frame drain while stalking; per-frame regeneration while moving; Scp106HuntersAtlasAbility; Scp106Attack) |
| TeleportedPlayer |  | per-action | ← TeleportingPlayer |  | Scp106Attack.ServerShoot |
| TeleportingPlayer | yes | per-action | → TeleportedPlayer |  | Scp106Attack.ServerShoot |
| UsedHunterAtlas |  | per-action | ← UsingHunterAtlas |  | Scp106HuntersAtlasAbility.UpdateServerside |
| UsingHunterAtlas | yes | per-action | → UsedHunterAtlas |  | Scp106HuntersAtlasAbility.ServerProcessCmd |

### Scp127Events

| Event | Cancel | Frequency | Pair | Ignored setters | Raised from |
|---|---|---|---|---|---|
| GainExperience |  | per-action | ← GainingExperience |  | Scp127TierManagerModule.ServerIncreaseExp |
| GainingExperience | yes | per-action | → GainExperience |  | Scp127TierManagerModule.ServerIncreaseExp |
| LevelUp |  | per-action | ← LevellingUp |  | Scp127TierManagerModule.ServerIncreaseExp |
| LevellingUp | yes | per-action | → LevelUp |  | Scp127TierManagerModule.ServerIncreaseExp |
| Talked |  | per-action | ← Talking |  | Scp127VoiceLineManagerModule.ServerSendVoiceLine |
| Talking | yes | per-action | → Talked | `Priority`* | Scp127VoiceLineManagerModule.ServerSendVoiceLine |

### Scp173Events

| Event | Cancel | Frequency | Pair | Ignored setters | Raised from |
|---|---|---|---|---|---|
| AddedObserver |  | per-action | ← AddingObserver |  | Scp173ObserversTracker.UpdateObserver |
| AddingObserver | yes | per-frame-per-pair **HOT** | → AddedObserver |  | Scp173ObserversTracker.UpdateObserver |
| BreakneckSpeedChanged |  | per-action | ← BreakneckSpeedChanging |  | Scp173BreakneckSpeedsAbility.IsActive (setter) |
| BreakneckSpeedChanging | yes | per-action | → BreakneckSpeedChanged |  | Scp173BreakneckSpeedsAbility.IsActive (setter) |
| CreatedTantrum |  | per-action | ← CreatingTantrum |  | Scp173TantrumAbility.ServerProcessCmd |
| CreatingTantrum | yes | per-action | → CreatedTantrum |  | Scp173TantrumAbility.ServerProcessCmd |
| PlayedSound |  | per-action | ← PlayingSound |  | Scp173AudioPlayer.ServerSendSound |
| PlayingSound | yes | per-action | → PlayedSound |  | Scp173AudioPlayer.ServerSendSound |
| RemovedObserver |  | per-action | ← RemovingObserver |  | Scp173ObserversTracker.UpdateObserver |
| RemovingObserver | yes | per-frame-per-pair **HOT** | → RemovedObserver |  | Scp173ObserversTracker.UpdateObserver |
| Snapped |  | per-action | ← Snapping |  | Scp173SnapAbility.ServerProcessCmd |
| Snapping |  | per-action | → Snapped | `Target` | Scp173SnapAbility.ServerProcessCmd |
| Teleported |  | per-action | ← Teleporting | `IsAllowed` | Scp173TeleportAbility.TryBlink |
| Teleporting | yes | per-action | → Teleported |  | Scp173TeleportAbility.TryBlink |

### Scp3114Events

| Event | Cancel | Frequency | Pair | Ignored setters | Raised from |
|---|---|---|---|---|---|
| Dance |  | per-action | ← StartDancing |  | Scp3114Dance.ServerProcessCmd |
| Disguised |  | per-action | ← Disguising |  | Scp3114Disguise.ServerComplete |
| Disguising | yes | per-action | → Disguised |  | Scp3114Disguise.ServerValidateBegin |
| Revealed |  | per-action | ← Revealing |  | Scp3114Identity.Update (forced reveal); Scp3114Reveal.ServerProcessCmd (voluntary reveal) |
| Revealing | yes | per-action | → Revealed |  | Scp3114Identity.Update (forced reveal, disguise timer expired); Scp3114Reveal.ServerProcessCmd (voluntary reveal) |
| StartDancing | yes | per-action | → Dance |  | Scp3114Dance.ServerProcessCmd |
| StrangleAborted |  | per-action | ← StrangleAborting |  | Scp3114Strangle.SyncTarget (setter) |
| StrangleAborting | yes | per-action | → StrangleAborted |  | Scp3114Strangle.SyncTarget (setter) |
| StrangleStarted |  | per-action | ← StrangleStarting |  | Scp3114Strangle.SyncTarget (setter) |
| StrangleStarting | yes | per-action | → StrangleStarted |  | Scp3114Strangle.SyncTarget (setter) |

### Scp914Events

| Event | Cancel | Frequency | Pair | Ignored setters | Raised from |
|---|---|---|---|---|---|
| Activated |  | on-request | ← Activating |  | Scp914Controller.ServerInteract |
| Activating | yes | on-request | → Activated |  | Scp914Controller.ServerInteract (Activate case) |
| KnobChanged |  | on-request | ← KnobChanging |  | Scp914Controller.ServerInteract |
| KnobChanging | yes | on-request | → KnobChanged |  | Scp914Controller.ServerInteract (ChangeMode case) |
| ProcessedInventoryItem |  | per-action | ← ProcessingInventoryItem |  | Scp914Upgrader.ProcessPlayer |
| ProcessedPickup |  | per-action | ← ProcessingPickup |  | Scp914Upgrader.ProcessPickup |
| ProcessedPlayer |  | per-action | ← ProcessingPlayer |  | Scp914Upgrader.ProcessPlayer |
| ProcessingInventoryItem | yes | per-action | → ProcessedInventoryItem |  | Scp914Upgrader.ProcessPlayer |
| ProcessingPickup | yes | per-action | → ProcessedPickup |  | Scp914Upgrader.ProcessPickup |
| ProcessingPlayer | yes | per-action | → ProcessedPlayer |  | Scp914Upgrader.ProcessPlayer |

### Scp939Events

| Event | Cancel | Frequency | Pair | Ignored setters | Raised from |
|---|---|---|---|---|---|
| Attacked |  | per-action | ← Attacking | `Damage` | Scp939ClawAbility.DamagePlayer; Scp939LungeAbility.ServerProcessCmd (primary, only if DealDamage returned true); Scp939LungeAbility.ServerProcessCmd (secondary, only if DealDamage returned true) |
| Attacking | yes | per-action | → Attacked | `Target`* | Scp939ClawAbility.DamagePlayer; Scp939LungeAbility.ServerProcessCmd (primary lunge target, damage 120); Scp939LungeAbility.ServerProcessCmd (secondary lunge targets, damage 30) |
| CreatedAmnesticCloud |  | per-action | ← CreatingAmnesticCloud |  | Scp939AmnesticCloudAbility.OnStateEnabled |
| CreatingAmnesticCloud | yes | per-action | → CreatedAmnesticCloud |  | Scp939AmnesticCloudAbility.OnStateEnabled |
| Focused |  | per-action |  |  | Scp939FocusAbility.TargetState (setter) |
| Lunged |  | per-action | ← Lunging |  | Scp939LungeAbility.State (setter) |
| Lunging | yes | per-action | → Lunged |  | Scp939LungeAbility.State (setter) |
| MimickedEnvironment |  | per-action | ← MimickingEnvironment |  | EnvironmentalMimicry.ServerProcessCmd |
| MimickingEnvironment | yes | per-action | → MimickedEnvironment |  | EnvironmentalMimicry.ServerProcessCmd |

### ScpEvents

| Event | Cancel | Frequency | Pair | Ignored setters | Raised from |
|---|---|---|---|---|---|
| HumeShieldBroken |  | per-action |  |  | DynamicHumeShieldController.OnHsValueChanged |

### ServerEvents

| Event | Cancel | Frequency | Pair | Ignored setters | Raised from |
|---|---|---|---|---|---|
| AchievedMilestone |  | per-action | ← AchievingMilestone |  | RespawnTokensManager.TryAchieveMilestone |
| AchievingMilestone | yes | per-action | → AchievedMilestone |  | RespawnTokensManager.TryAchieveMilestone (called in a loop from OnPointsModified, subscribed to FactionInfluenceManager.InfluenceModified) |
| BanIssued |  | per-action | ← BanIssuing |  | BanHandler.IssueBan |
| BanIssuing | yes | per-action | → BanIssued |  | BanHandler.IssueBan |
| BanRevoked |  | per-action | ← BanRevoking |  | BanHandler.RemoveBan |
| BanRevoking | yes | per-action | → BanRevoked |  | BanHandler.RemoveBan |
| BanUpdated |  | per-action | ← BanUpdating |  | BanHandler.IssueBan |
| BanUpdating | yes | per-action | → BanUpdated |  | BanHandler.IssueBan (branch where a ban with the same Id exists) |
| BlastDoorChanged |  | per-action | ← BlastDoorChanging |  | BlastDoor.SetDoorState |
| BlastDoorChanging | yes | per-action | → BlastDoorChanged | `NewState` | BlastDoor.SetDoorState (SyncVar hook for the open state; also called directly by BlastDoor.ServerSetTargetState, which the warhead detonation-time update in AlphaWarheadController and AlphaWarheadNukesitePanel invoke) |
| CassieAnnounced |  | never-fired | ← CassieAnnouncing |  |  |
| CassieAnnouncing | yes | never-fired | → CassieAnnounced |  |  |
| CassieQueuedScpTermination |  | per-lifecycle | ← CassieQueuingScpTermination |  | CassieScpTerminationAnnouncement.AnnounceScpTermination |
| CassieQueuingScpTermination | yes | per-lifecycle | → CassieQueuedScpTermination |  | CassieScpTerminationAnnouncement.AnnounceScpTermination (subscribed to PlayerStats.OnAnyPlayerDied) |
| CheckpointDoorSequenceChanged |  | per-action | ← CheckpointDoorSequenceChanging |  | CheckpointDoor.CurSequence setter (server only) |
| CheckpointDoorSequenceChanging | yes | per-action | → CheckpointDoorSequenceChanged |  | CheckpointDoor.CurSequence setter (driven every frame by CheckpointDoor.Update from the sequence controller while the server is active) |
| CommandExecuted |  | on-request | ← CommandExecuting | `ExecutedSuccessfully`*, `Response`* | CommandProcessor.ProcessQuery (CommandType.RemoteAdmin, on success and on exception); GameCore.Console.TypeCommand (CommandType.Console, on success and on exception); QueryProcessor.ProcessGameConsoleQuery (finally block, CommandType.Client) |
| CommandExecuting | yes | on-request | → CommandExecuted | `Command`* | CommandProcessor.ProcessQuery (CommandType.RemoteAdmin); GameCore.Console.TypeCommand (CommandType.Console, server console and RA path); QueryProcessor.ProcessGameConsoleQuery (CommandType.Client, "." commands from the player game console) |
| DeadmanSequenceActivated |  | once | ← DeadmanSequenceActivating |  | DeadmanSwitch.InitiateProtocol |
| DeadmanSequenceActivating | yes | once | → DeadmanSequenceActivated |  | DeadmanSwitch.InitiateProtocol |
| DoorDamaged |  | per-action | ← DoorDamaging |  | BreakableDoor.ServerDamage |
| DoorDamaging | yes | per-action | → DoorDamaged |  | BreakableDoor.ServerDamage (callers: ExplosionGrenade, Scp096HitHandler, Scp1507AttackAbility, DisruptorHitregModule, Scp018Projectile, the MicroHID charge fire mode, DestroyDoorCommand, CheckpointDoor.ServerDamage per sub-door) |
| DoorLockChanged |  | per-action |  |  | DoorVariant.Update (per-frame poll comparing the active locks with the previous value) |
| DoorRepaired |  | per-action | ← DoorRepairing |  | BreakableDoor.ServerRepair |
| DoorRepairing | yes | per-action | → DoorRepaired |  | BreakableDoor.ServerRepair (callers: the RA RepairDoorCommand, CheckpointDoor.ServerRepair per sub-door) |
| ElevatorSequenceChanged |  | per-action |  |  | ElevatorChamber.ServerSetDestination (first placement branch); ElevatorChamber.Update (compares the sequence before and after the destination and sequence updates) |
| ExplosionSpawned |  | per-action | ← ExplosionSpawning |  | ExplosionGrenade.Explode |
| ExplosionSpawning | yes | per-action | → ExplosionSpawned |  | ExplosionGrenade.Explode (static; callers: ExplosionGrenade.ServerFuseEnd, ExplosionUtils.ServerExplode (SCP-018 Scp018Projectile, PinkCandy/Cola/Jailbird effects, HubertMoon), DisruptorHitregModule) |
| GeneratorActivated |  | per-action | ← GeneratorActivating |  | Scp079Generator.ServerUpdate |
| GeneratorActivating | yes | per-action | → GeneratorActivated |  | Scp079Generator.ServerUpdate (called from Update every frame) |
| ItemSpawned |  | per-round | ← ItemSpawning |  | ItemDistributor.ServerRegisterPickup |
| ItemSpawning | yes | per-round | → ItemSpawned | `ItemType` | ItemDistributor.ServerRegisterPickup (callers: ItemDistributor and RandomItemGroupSpawnpoint) |
| LczDecontaminationAnnounced |  | per-round |  |  | DecontaminationController.UpdateTime (phase advance, called from Update) |
| LczDecontaminationStarted |  | per-round | ← LczDecontaminationStarting |  | DecontaminationController.FinishDecontamination |
| LczDecontaminationStarting | yes | per-round | → LczDecontaminationStarted |  | DecontaminationController.FinishDecontamination (called from the DecontaminationOverride setter when the value is Forced) |
| MapGenerated |  | per-round | ← MapGenerating |  | SeedSynchronizer.GenerateLevel |
| MapGenerating | yes | per-round | → MapGenerated |  | SeedSynchronizer.Start (server branch) |
| ModifiedFactionInfluence |  | per-action | ← ModifyingFactionInfluence |  | FactionInfluenceManager.Set (inside NetworkServer.active) |
| ModifyingFactionInfluence | yes | per-action | → ModifiedFactionInfluence |  | FactionInfluenceManager.Set (via Add/Remove; callers: FactionObjectiveBase.GrantInfluence, WaveSetCommand) |
| PickupCreated |  | per-action |  |  | Pickup.AddPickup (subscribed to ItemPickupBase.OnPickupAdded, which the game raises from ItemPickupBase.Start) |
| PickupDestroyed |  | per-action |  |  | Pickup.RemovePickup (subscribed to ItemPickupBase.OnPickupDestroyed, which the game raises from ItemPickupBase.OnDestroy) |
| PluginsEnabled |  | once |  |  | PluginLoader.Initialize |
| ProjectileExploded |  | per-action | ← ProjectileExploding |  | ExplosionGrenade; FlashbangGrenade; Scp018Projectile; Scp2176Projectile |
| ProjectileExploding | yes | per-action | → ProjectileExploded |  | TimeGrenade.ServerFuseEnd (called once from TimeGrenade.Update; base of ExplosionGrenade, FlashbangGrenade, Scp018Projectile, Scp2176Projectile overrides) |
| RoomColorChanged |  | per-action |  |  | RoomLightController.OverrideColorHook (SyncVar hook of OverrideColor) |
| RoomLightChanged |  | per-action |  |  | RoomLightController.LightsEnabledHook (SyncVar hook of LightsEnabled) |
| RoundEnded |  | per-round | ← RoundEnding |  | RoundSummary (the server-side round-summary loop) |
| RoundEnding | yes | per-round | → RoundEnded |  | RoundSummary (the server-side round-summary loop) |
| RoundEndingConditionsCheck |  | per-action |  |  | RoundSummary (the server-side round-summary loop, a MEC coroutine on Segment FixedUpdate) |
| RoundRestarted |  | per-round |  |  | RoundRestart.InitiateRoundRestart |
| RoundStarted |  | per-round | ← RoundStarting |  | CharacterClassManager.Init (coroutine) |
| RoundStarting | yes | per-round | → RoundStarted |  | CharacterClassManager.ForceRoundStart (callers: Init with round autostart, the lobby countdown reaching zero, and CommandSystem.Commands.Shared ForceStartCommand) |
| SendingAdminChat | yes | on-request | → SentAdminChat |  | CommandProcessor.ProcessAdminChat |
| SentAdminChat |  | on-request | ← SendingAdminChat |  | CommandProcessor.ProcessAdminChat |
| Shutdown |  | once |  |  | Shutdown.Quit |
| WaitingForPlayers |  | per-round |  |  | CharacterClassManager.Start (host/local hub only) |
| WaveRespawned |  | per-round | ← WaveRespawning |  | WaveSpawner.SpawnWave |
| WaveRespawning | yes | per-round | → WaveRespawned |  | WaveSpawner.SpawnWave (called from WaveManager.Spawn) |
| WaveTeamSelected |  | per-round | ← WaveTeamSelecting |  | WaveManager.InitiateRespawn |
| WaveTeamSelecting | yes | per-round | → WaveTeamSelected |  | WaveManager.InitiateRespawn (called from WaveManager.Update for a ready TimeBasedWave, and from RA/wave commands) |

### WarheadEvents

| Event | Cancel | Frequency | Pair | Ignored setters | Raised from |
|---|---|---|---|---|---|
| Detonated |  | once | ← Detonating |  | AlphaWarheadController.Detonate |
| Detonating | yes | per-action | → Detonated |  | AlphaWarheadController.Detonate (called from ServerUpdateDetonationTime, which runs from Update every frame) |
| Started |  | per-action | ← Starting |  | AlphaWarheadController.StartDetonation |
| Starting | yes | per-action | → Started |  | AlphaWarheadController.StartDetonation (callers: the button/RA path, which can suppress the subtitles, and the auto-nuke check every frame) |
| Stopped |  | per-action | ← Stopping |  | AlphaWarheadController.CancelDetonation (inside NetworkServer.active) |
| Stopping | yes | per-action | → Stopped |  | AlphaWarheadController.CancelDetonation(ReferenceHub) |

## Re-verifying on a new build

This file and `tools/catalog/labapi-events.json` are verified against LabAPI 1.1.7 / SCP:SL 14.2.7 only. After a game or
LabAPI update, treat every row as stale until the steps below say otherwise. The full procedure is
`researching-scpsl-internals/reverifying-a-build.md` (section 3 for the catalog); the short form:

1. Run `researching-scpsl-internals/scripts/event-sites.py` in catalog-drift mode against the new game and LabAPI trees.
   It lists events that are new, removed, duplicated, or whose raise-site count changed, and events marked `never-fired`
   that now have a call site (or the reverse). Add, delete or re-read exactly those rows.
2. For a row whose raise site moved, read the enclosing method and its caller again; do not trust the old frequency class.
3. Re-derive `hot` from the frequency class (per-frame, per-frame-per-pair, high-rate-network). `burst` and
   `conditionallyHot` follow the same way: a changed rate or a newly found per-frame caller moves a row between classes.
4. Check `ignoredSetters` and `conditionallyIgnoredSetters` on every cancellable row whose raise file changed: a setter that became
   honoured (or that lost its condition) flips a trap. Keep `pair` symmetric: the generator refuses an asymmetric pair.
5. Regenerate the embedded hot list in SlKit (`HotEventAudit.HotList.cs`) and run the `scpsl-lint` tests; the linter reads
   the catalog at runtime, so a reclassified event changes its findings.
6. Update the version stamp only after the checks above were actually re-run. Mark what you could not re-check UNVERIFIED.
