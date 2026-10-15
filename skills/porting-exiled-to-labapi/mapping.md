# EXILED to LabAPI mapping tables

Verified against LabAPI 1.1.7 / SCP:SL 14.2.7 (2026-10). EXILED side checked against 9.15.x.

Companion to `SKILL.md`. Features with no counterpart (CustomItems, CustomRoles, permissions,
translations) and the cost of running EXILED are in `replacements.md`.

Legend: **C** = the LabAPI event is cancellable (`IsAllowed`). **TIMING** = same name or same game
method, but a different moment inside it: re-test, do not just rename. Statements marked
UNVERIFIED come from code reading and were not observed on a live server.

## 0. How the two stacks relate (why renaming is not porting)

- EXILED is itself a LabAPI plugin (it enables last among LabAPI plugins). It raises almost all of
  its events from its **own Harmony patches**, applied lazily at the first `+=` to an event and never
  removed on `-=`. LabAPI events are plain inline calls inside the game.
- Only a handful of EXILED events are fed from LabAPI events (`ReloadingWeapon`, `UnloadingWeapon`,
  `SendingHitmarker`, the SCP-127 events, `ProjectileExploding`).
- About two dozen EXILED event patches anchor on the call that builds the LabAPI event args. Some run
  **before** it, so an EXILED cancel hides the occurrence from LabAPI handlers (pickups, NoClip, disarm
  and cuff, radio toggle and preset, locker, generator, voice send and receive, SCP-914 knob and
  activate, SCP-079 first camera patch, tesla). Others run **after** it (`Escaping`, SCP-914
  `UpgradingPlayer`, SCP-079 second camera patch): LabAPI handlers already ran, and an EXILED cancel
  only aborts the later action. With both stacks loaded the same occurrence can reach an EXILED and a
  LabAPI handler in either order.
- Delivery: LabAPI is a plain multicast delegate (subscription order = order of plugin `Enable()`, no
  priority, no async, per-handler try/catch). EXILED `Event<T>` has per-handler priority and async
  handlers. Ordering and `async` are lost in a port. Plugin `Priority` is a byte and **lower enables
  earlier**, so the `Lowest` plugin gets the last word on `IsAllowed`.
- Two unrelated `Player` classes (EXILED's and LabAPI's), two wrapper graphs for Item, Pickup, Door
  and Room. Implicit conversions exist only for `Player`, and only while EXILED is loaded. A LabAPI-only
  plugin must not reference `Exiled.API`.

## 1. Timelines (authoritative order)

**Join, online mode.**

1. `PreAuthenticating` / `PreAuthenticated` on the single connection-request thread. No Unity, no `Player`
   (see labapi-events).
2. Hub starts. LabAPI `Player` wrapper is created here (also lazily by `Player.Get(hub)`).
   EXILED `Joined` fires here. `UserId` is null, `IsReady` false, `Nickname` is the string `"(null)"`.
3. Auth response arrives. The `UserId` setter runs; this makes the instance a ready client and, **when
   the round has already started**, the game assigns a late-join role right here (reason `LateJoin`).
   LabAPI `ChangingRole` ... `ChangedRole` for that player therefore fire while `IsReady` is still false and
   `Nickname` is still `"(null)"`. In the lobby no role is assigned yet.
4. Nickname is set, `IsReady` becomes true, permissions refresh. EXILED `Verified` fires right after
   this step (and calls the late-join assignment itself, because it unhooks the game's own).
5. Badge and group processing, then LabAPI **`Joined`**. Skipped on: badge serial mismatch (player is
   disconnected); badge UserID or nickname mismatch (player **stays connected and `IsReady`, never gets
   `Joined`**). A badge token that only fails signature parsing still reaches `Joined`.
6. Offline mode: LabAPI `Joined` fires at the end of the nickname command; EXILED has a separate
   offline-mode path for `Verified`.

Consequences: `Joined` is the correct port of `Verified` and is *later* than it (badge and group state
are settled). The wrapper and hub exist long before `Joined`. A late joiner already has a role.

**Leave.** LabAPI `Left` is the hub's destroy callback: it fires for every non-host hub, including
never-authenticated connections, dummies and NPCs. The wrapper is alive during `Left`; `UserId` and
`Nickname` are readable. `IpAddress` may throw there for a real client (peer entry already removed;
derived from callback order, untested). With the default `disconnect_drop`, a living leaver is killed
first, so `Hurting/Hurt/Dying/Death` fire for leaving players.

**Role change (`ServerSetRole`).**

1. LabAPI `ChangingRole` (C), then the game initialises the role.
2. Spawnpoint: LabAPI `Spawning` (C), then `Spawned`. Not raised for roles without a spawnpoint handler
   (079, spectator, overwatch).
3. Loadout granted: `ReceivingLoadout` (C) then `ReceivedLoadout`. **After** `Spawned`.
4. Role info sent to every hub, then LabAPI `ChangedRole`.

**Damage and death (`PlayerStats.DealDamage`).**

1. God mode and spawn protection return early.
2. EXILED `Hurting` is inserted right after the god-mode return: **before** spawn protection, before the
   role's damage processing, before attacker resolution.
3. LabAPI `Hurting` (C): after all of those. Then damage is applied, then LabAPI `Hurt`.
4. Death branch: LabAPI `Dying` (C), then the died-callbacks, then `KillPlayer`, then LabAPI `Death`.
   EXILED `Dying` sits at the head of `KillPlayer`, so it runs **after** LabAPI `Dying` and after those
   callbacks; `Died` is at the end of `KillPlayer`.
5. Cancelling LabAPI `Dying` leaves the victim alive at 0 HP: damage was already applied. Restore `Health`.
6. `ev.DamageHandler` has a setter the game ignores (it keeps its local handler). Mutate the existing
   handler (`StandardDamageHandler.Damage`) instead.

## 2. Event mapping, most used first

"Same" = same game method, same moment, rename only.

### Connection and identity

| EXILED | LabAPI | Differences |
|---|---|---|
| `Verified` | `PlayerEvents.Joined` | TIMING: later (after badges and group). Not cancellable. Skipped on badge UserID/nickname mismatch (player stays connected). |
| `Joined` (hub start, before auth) | none as event | Use `ReferenceHub.OnPlayerAdded`, or just do the work in `Joined`. `UserId` is null at that moment. |
| `Destroying` | `PlayerEvents.Left` | LabAPI also fires for unauthenticated hubs, NPCs, dummies. Guard with a set filled in `Joined`. |
| `Left` (network disconnect) | none | Real-connection disconnects only. Use `Left` plus your own guard, or a Harmony patch on the disconnect handler. |
| `PreAuthenticating` | `PreAuthenticating` (C), `PreAuthenticated` | Both on a background thread. LabAPI args: `CanJoin`, `CustomReject`, `Reject*` helpers. |
| `Kicking` / `Kicked` | `Kicking` (C) / `Kicked` | EXILED `Kicked` hooks the generic disconnect, so it also fires for non-kick disconnects (auth rejections). LabAPI only for `KickUser`. |
| `Banning` / `Banned` | `Banning` (C) / `Banned` | Duration 0 is routed to a kick first, so `Banning` is not raised for kicks. EXILED `Banned` is per `IssueBan` call and may fire twice with IP bans (UNVERIFIED). |
| `Unbanning` | `ServerEvents.BanRevoking` (C) / `BanRevoked` | Rename. Also `BanIssuing` / `BanIssued`. |
| `ReservedSlot` | none | LabAPI has the `ReservedSlots` wrapper and `PreAuthenticating.CanJoin`. |
| `ChangingNickname` | `ChangingNickname` (C) / `ChangedNickname` | Same. |
| `ChangingGroup` | `GroupChanging` (C) / `GroupChanged` | Name reversal. |

### Roles, spawn, death

| EXILED | LabAPI | Differences |
|---|---|---|
| `ChangingRole` | `ChangingRole` (C) / `ChangedRole` | LabAPI `OldRole` is a `PlayerRoleBase`; reason is the game `RoleChangeReason` (EXILED `SpawnReason` has the same numeric order). `ev.Items` / `ev.Ammo` / `ShouldPreserveInventory` have **no twin**: use `ReceivingLoadout` and `RoleSpawnFlags`. Unguarded `SetRole` inside `ChangingRole` recurses; change `ev.NewRole` instead. |
| `Spawning` | `Spawning` (C) | LabAPI fires for every FPC role with a spawnpoint handler; EXILED only when a position will really be set, and is not cancellable. |
| `Spawned` | `ChangedRole` or `ReceivedLoadout` | TIMING: LabAPI `Spawned` is position-only and **precedes the loadout**. Items added there are lost when the change carries `RoleSpawnFlags.AssignInventory`. |
| `Dying` | `Dying` (C) | TIMING: see timeline 5 (0 HP after cancel). |
| `Died` | `Death` | Args carry old role, position, velocity, camera rotation. No ragdoll; use `SpawnedRagdoll`. |
| `Hurting` | `Hurting` (C) | TIMING: LabAPI runs after spawn-protect and role damage processing. No `DamageType` enum anywhere: read the handler type (`FirearmDamageHandler`, ...). |
| `Hurt` | `Hurt` | EXILED's attempt to rewrite the handler output is most likely a no-op on 14.2.7 (UNVERIFIED at runtime); LabAPI cannot rewrite it. Also fires when the output was `Nothing`. |
| `Escaping` / `Escaped` | `Escaping` (C) / `Escaped` | `NewRole` and `EscapeScenario` mutable. EXILED runs after LabAPI here and sees its edits. HOT: LabAPI raises it every frame for each player standing in an escape zone, even when the scenario is `None`: return early on `EscapeScenario == None`. Escape with kept items returns them one frame after `ChangedRole`. |
| `SpawningRagdoll` | `SpawningRagdoll` (C) / `SpawnedRagdoll` | Same. |

### Inventory and items

| EXILED | LabAPI | Differences |
|---|---|---|
| `ChangingItem` / `ChangedItem` | same names | Same. |
| `DroppingItem` / `DroppedItem` | `DroppingItem` (C) / `DroppedItem`; `ThrowingItem` (C) / `ThrewItem` | TIMING: EXILED fires before the `AllowDropping` check and raises `DroppingNothing` for unknown serials; LabAPI only after validation, and only for the client drop command (not for server-side drops such as death). Throw physics is a separate event. |
| `DroppingNothing` | none | Patch the drop command if needed. |
| `DroppingAmmo` / `DroppedAmmo` | same names | Same. |
| `PickingUpItem` | `PickingUpItem` (C) / `PickedUpItem` | EXILED runs first and an EXILED cancel hides the event. SCP-244 deployables raise it too; ammo, armor and SCP-330 have their own LabAPI events. |
| `SearchingPickup` | `SearchingPickup` (C) / `SearchedPickup` | `SearchedAmmo` and `SearchedArmor` are **never raised**; use `SearchedPickup`. |
| `ItemAdded` / `ItemRemoved` | none | Subscribe the game's static `InventoryExtensions.OnItemAdded` / `OnItemRemoved` (no try/catch, see section 3). |
| `UsingItem` | `UsingItem` (C) | EXILED documents firing on cooldown; on 14.2.7 its anchor looks to sit after the cooldown branch too (UNVERIFIED). LabAPI fires only after cooldown and speed checks. |
| `UsingItemCompleted` | `ItemUsageEffectsApplying` (C) | Adds `ContinueProcess`. |
| `UsedItem` | `UsedItem` | Same moment. |
| `CancellingItemUse` / `CancelledItemUse` | `CancellingUsingItem` (C) / `CancelledUsingItem` | Rename. |
| `Handcuffing` / `RemovingHandcuffs` | `Cuffing` (C) / `Uncuffing` (C), `Cuffed` / `Uncuffed` | EXILED runs first; a cancel hides it from LabAPI. |
| `ThrowingRequest` / `ThrownProjectile` | `ThrowingProjectile` (C) / `ThrewProjectile` | Request level vs throw level. |
| `FlippingCoin` | `FlippingCoin` (C) / `FlippedCoin` | Same. |
| `TogglingFlashlight` / weapon | `TogglingFlashlight` (C), `TogglingWeaponFlashlight` (C) | Same. |
| `TogglingRadio`, `ChangingRadioPreset`, `UsingRadioBattery` | `TogglingRadio` (C), `ChangingRadioRange` (C), `UsingRadio` (C) | `UsingRadio` is per frame per radio (hot). Semantics vs `UsingRadioBattery` UNVERIFIED. |

### Firearms

| EXILED | LabAPI | Differences |
|---|---|---|
| `Shooting` | `ShootingWeapon` (C) | LabAPI args: only `Player` and `Firearm` (no direction, no claimed target); fires before shot processing. |
| `Shot` (per destructible hit) | `ShotWeapon` | Granularity differs: once per trigger pull after processing. Port per-hit consumers to `Hurting` + `FirearmDamageHandler` (narrower: no dead targets, god-mode or spawn-protected targets, non-player destructibles) or your own patch. |
| `ReloadingWeapon`, `UnloadingWeapon`, `ReloadedWeapon`, `UnloadedWeapon` | same names (C on `-ing`) | EXILED already sources the `-ing` events from LabAPI. |
| `AimingDownSight` | `AimedWeapon` | No pre-event: the `AimingWeapon` declaration is commented out in 1.1.7. |
| `DryfiringWeapon` | `DryFiringWeapon` (C) / `DryFiredWeapon` | Rename. |
| `ChangingAttachments`, `ReceivingPreference` | `ChangingAttachments` (C) / `ChangedAttachments`, `SendingAttachmentsPrefs` (C) | Same. |
| `RotatingRevolver` | `SpinningRevolver` (C) | Name. |

### Interactions

| EXILED | LabAPI | Differences |
|---|---|---|
| `InteractingDoor` | `InteractingDoor` (C) / `InteractedDoor` | Two fire sites (unlocked, locked). On the locked path LabAPI raises with `CanOpen=false` and honours `CanOpen=true`, so one `ev.CanOpen = true` opens both. Cancelling on the locked path still plays denial feedback and raises `InteractedDoor(canOpen:false)`. `Door.IsOpened` ignores locks and permissions. |
| `InteractingLocker`, `InteractingElevator` | same names (C) | Same. |
| `UnlockingGenerator`, `OpeningGenerator`, `ClosingGenerator`, `ActivatingGenerator`, `StoppingGenerator` | `UnlockingGenerator`, `OpeningGenerator`, `ClosingGenerator`, `ActivatingGenerator`, `DeactivatingGenerator`, `InteractingGenerator` (all C) | "Stopping" is `Deactivating`. |
| `TriggeringTesla` | `TriggeringTesla` (C) / `TriggeredTesla`, `IdlingTesla` (C) | Both are per-frame class events (hot). |
| `ActivatingWarheadPanel` | `UnlockingWarheadButton` (C) / `UnlockedWarheadButton` | Name. |
| `InteractingShootingTarget`, `DamagingShootingTarget` | same names (C) | Same. |
| `DamagingWindow`, `DamagingDoor` | `DamagingWindow` (C), `ServerEvents.DoorDamaging` (C) | Same. |

### Voice, moderation, movement, environment

| EXILED | LabAPI | Differences |
|---|---|---|
| `VoiceChatting`, `ReceivingVoiceMessage` | `SendingVoiceMessage` (C), `ReceivingVoiceMessage` (C) | HOT: per packet (about 100 packets/s per speaker) and per listener; args are built even with no subscriber. See labapi-events before subscribing. |
| `IntercomSpeaking` | `UsingIntercom` (C) / `UsedIntercom` | Per frame while someone transmits. |
| `IssuingMute` / `RevokingMute` | `Muting` (C) / `Unmuting` (C) | LabAPI fires **only** from the four RA commands. `Player.Mute()` raises nothing. To observe every mute use the game's `VoiceChatMutes.OnFlagsSet` (post-hoc, includes temporary mutes). |
| `TogglingNoClip` | `TogglingNoclip` (C) | Same layer; EXILED runs first. |
| `RoomChanged` / `ZoneChanged` | same names | Same. |
| `ChangingMoveState` | `MovementStateChanged` | LabAPI only when applying a client sync packet; EXILED on every setter call. |
| `Jumping` / `Landing` | `Jumped` (post only) | No landing event. |
| `EnteringPocketDimension` | `EnteringPocketDimension` (C) / `EnteredPocketDimension` | LabAPI hooks the corroding effect, EXILED the SCP-106 attack. |
| `EscapingPocketDimension`, `FailingEscapePocketDimension` | `LeavingPocketDimension` (C, mutable `IsSuccessful`) / `LeftPocketDimension` | One event for both outcomes; flipping `IsSuccessful` flips the outcome. |
| `ReceivingEffect` | `UpdatingEffect` (C) / `UpdatedEffect` | Rename. |
| `Entering/Staying/ExitingEnvironmentalHazard` | `EnteringHazard` (C), `StayingInHazard`, `LeavingHazard` (C) | `StayingInHazard` is per frame; its `AffectedPlayers` is a pooled list, copy it inside the handler. |
| `SendingValidCommand` / `SentValidCommand` | `ServerEvents.CommandExecuting` (C) / `CommandExecuted` | EXILED hooks RA and game console separately. |
| `ReportingCheater`, `LocalReporting` | `ReportingCheater` (C), `ReportingPlayer` (C) | Different handler classes. |
| `EarningAchievement` | `ReceivedAchievement` (post only) | No pre-event, no cancel. |
| `SendingAdminChatMessage` | `ServerEvents.SendingAdminChat` (C) / `SentAdminChat` | Rename. |

### Round, server, map, warhead, SCP-914

| EXILED | LabAPI | Differences |
|---|---|---|
| `WaitingForPlayers` | `ServerEvents.WaitingForPlayers` | Same. Reset per-round state here. |
| `RoundStarting`, `RoundStarted` | `RoundStarting` (C), `RoundStarted` | Same. |
| `EndingRound` | `RoundEndingConditionsCheck` (`CanEnd`) + `RoundEnding` (C) | LabAPI splits end condition from cancel. EXILED `IsAllowed` maps to `CanEnd`. |
| `RoundEnded` | `RoundEnded` (`ShowSummary`) | Same. |
| `RestartingRound` | `RoundRestarted` | LabAPI fires before the already-restarting guard, so the handler can run twice: make it idempotent. A soft restart does not raise it (use `RoundRestart.OnRestartTriggered`, see scpsl-game-lifecycle). |
| `RespawningTeam` / `RespawnedTeam` | `WaveRespawning` (C) / `WaveRespawned` | `WaveRespawning.Roles` is pooled; copy. |
| `SelectingRespawnTeam` | `WaveTeamSelecting` (C) / `WaveTeamSelected` | Rename. |
| `Decontaminating` | `LczDecontaminationStarting` (C) / `Started` / `Announced` | Rename. |
| `ExplodingGrenade` | `ProjectileExploding` (C) / `ProjectileExploded`, `ExplosionSpawning` (C) | LabAPI is projectile-generic. |
| `SpawningItem` | `ItemSpawning` (C) / `ItemSpawned` | Rename. |
| `Generating` / `Generated` | `MapGenerating` (C) / `MapGenerated` | Rename. |
| `PlacingBulletHole` | `PlacingBulletHole` (C) / `PlacedBulletHole` | Per hit, double on glass. |
| `GeneratorActivating` | `ServerEvents.GeneratorActivating` (C) / `Activated` | Same. |
| `PickupAdded` / `PickupDestroyed` | `ServerEvents.PickupCreated` / `PickupDestroyed` | LabAPI `PickupCreated` fires only when no wrapper existed yet, so it misses most pickups; tag at the call site that creates or drops them. |
| Warhead `Starting`, `Stopping`, `Detonating`, `Detonated` | `WarheadEvents.Starting` (C)/`Started`, `Stopping` (C)/`Stopped`, `Detonating` (C)/`Detonated` | Same methods. |
| `ChangingLeverStatus` | `InteractingWarheadLever` (C) / `Interacted` | Name. |
| SCP-914 `Upgrading*` | `Scp914Events.ProcessingPlayer` / `ProcessingPickup` / `ProcessingInventoryItem` (C) + `Processed*` | Renames. `Activating` / `ChangingKnobSetting` map to `Scp914Events.Activating` (C), `KnobChanging` (C). |
| `SendingCassieMessage` | `ServerEvents.CassieAnnouncing` | **Dead**: never raised in 14.2.7. Only `CassieQueuingScpTermination` works, for termination lines. |

SCP-role events map by name only (args and timing **not diffed**, UNVERIFIED): SCP-096 (`AddingTarget`,
`Enraging`, `ChangingState`, `PryingGate`, `Charging`), SCP-173 (`Teleporting`, `Snapping`,
`CreatingTantrum`, `BreakneckSpeedChanging`, `AddingObserver`), SCP-079 (`ChangingCamera`,
`GainingExperience`, `LevelingUp`, `Pinging`, `BlackingOutRoom`, `BlackingOutZone`, `LockingDownRoom`,
`Recontaining`), SCP-106 (`TeleportingPlayer`, `UsingHunterAtlas`, `ChangingStalkMode`), SCP-049
(`Attacking`, `StartingResurrection`, `ResurrectingBody`, `UsingSense`, `UsingDoctorsCall`), SCP-939
(`Lunging`, `CreatingAmnesticCloud`, `Focused`, `MimickingEnvironment`), SCP-3114 (`Disguising`,
`Revealing`, `StrangleStarting`, `Dance`). EXILED-only SCP events (no LabAPI event found by name):
939 `ValidatingVisibility`/`PlayingFootstep`/`PlayingVoice`, 173 `BeingObserved`, 079 `TriggeringDoor`/
`ElevatorTeleporting`/`ChangingSpeakerStatus`/`LosingSignal`, zombie `TriggeringBloodlust`, 330, 244,
559, 2536, 1344, 1507.

## 3. EXILED events with no LabAPI event

Order of preference: (a) a LabAPI event from section 2, after re-reading its timing; (b) a game-native
C# event; (c) your own Harmony patch on the same game method EXILED patches (see scpsl-harmony-patching:
unique Harmony id, test with EXILED loaded and unloaded).

**Game-native events** (public static C# events, no Harmony): `ReferenceHub.OnPlayerAdded`,
`InventoryExtensions.OnItemAdded` / `OnItemRemoved`, `ItemPickupBase.OnPickupAdded` / `OnPickupDestroyed`,
`RoleAssigner.OnPlayersSpawned`, `WaveManager.OnWaveSpawned`, `UsableItemsController.ServerOnUsingCompleted`,
`PlayerRoleManager.OnRoleChanged`, `FpcServerPositionDistributor.RoleSyncEvent`, `VoiceChatMutes.OnFlagsSet`.
They are raw delegates invoked inline by the game: **no per-handler try/catch**, so an exception in your
handler propagates into the game method (for example `ServerAddItem`) and skips later subscribers. Wrap
every body in try/catch and unsubscribe at shutdown.

| EXILED event | Game method to hook (or native event) |
|---|---|
| `Joined` (hub start) | `ReferenceHub.OnPlayerAdded`, or port to LabAPI `Joined` |
| `Left` (disconnect) | `CustomNetworkManager.OnServerDisconnect` |
| `ReservedSlot` | `CustomLiteNetLib4MirrorTransport.HasReservedSlot` |
| `Hit` (zombie attack) | `ScpAttackAbilityBase<ZombieRole>.ServerPerformAttack` |
| `Healing` / `Healed` | `HealthStat.ServerHeal` |
| `DroppingNothing` | `Inventory` drop command handler |
| `Interacted` | `InteractionCoordinator.ClientInteract` |
| `ChangedRatio` | `AspectRatioSync` set-aspect-ratio command |
| `Activating/DeactivatingWorkstation` | `WorkstationController.ServerInteract`, `NetworkStatus` setter |
| `PlayingAudioLog` | `AudioLog.ServerInteract` |
| `SendingGunSound` / `ReceivingGunSound` | `AudioModule.ServerSendToNearbyPlayers` |
| `EnteringKillerCollision` | `PitKiller.OnTriggerEnter` |
| `MakingNoise` | `AnimatedCharacterModel.PlayFootstep` |
| `Jumping` (pre) / `Landing` | `FpcMotor.UpdateGrounded`, `FirstPersonMovementModule.IsGrounded` setter |
| MicroHID (`ChangingMicroHIDState`, `UsingMicroHIDEnergy`, `ExplodingMicroHID`) | `CycleController.Phase` setter, `EnergyManagerModule.EquipUpdate` |
| `ChangingDisruptorMode` | LabAPI has post-only `ToggledDisruptorFiringMode` |
| `ChangingEmotion` | `EmotionSync.ServerSetEmotionPreset` |
| `TogglingOverwatch` | `OverwatchCommand.SetOverwatchStatus` |
| `ChangingWearables` | `WearableSync.OverrideWearables` |
| `InteractingEmergencyButton` | `EmergencyDoorRelease.ServerInteract` |
| `DrinkingCoffee` | `Coffee.ServerInteract` |
| `ChangingSpectatedPlayer` | `SpectatorRole.SyncedSpectatedNetId` setter (LabAPI has post-only `ChangedSpectator`) |
| `ItemAdded` / `ItemRemoved` | native `InventoryExtensions.OnItemAdded` / `OnItemRemoved` |
| `AllPlayersSpawned` | native `RoleAssigner.OnPlayersSpawned` |
| `AddingUnitName`, `ChoosingStartTeamQueue` | `NamingRulesManager.ServerGenerateName`, `RoleAssigner.OnRoundStarted` |
| `FillingLocker`, `SpawningRoomConnector`, `SpawningTeamVehicle`, `TurningOffLights` | per-patch; LabAPI has post-only `RoomLightChanged` |
| `AnnouncingNtfEntrance`, `AnnouncingScpTermination` | announcement dispatch; LabAPI `CassieQueuingScpTermination` covers termination only |
| `SendingCassieMessage` | `CassieAnnouncementDispatcher.PlayNewAnnouncement` (LabAPI `CassieAnnouncing` is dead) |
| `Swinging`, `ChargingJailbird`, `Punching`, `Cackling`, `KeycardInteracting` | Jailbird and keycard modules; LabAPI has `ProcessingJailbirdMessage`, `InspectingItem`, `InspectingKeycard` |
| `ReloadedConfigs`, `ReloadedPlugins`, ... | EXILED-internal; no game counterpart |

**Declared but never raised in 14.2.7** (a handler compiles and silently never runs):
`ServerEvents.CassieAnnouncing`, `CassieAnnounced`, `PlayerEvents.SearchedAmmo`, `SearchedArmor`.
`AimingWeapon` does not exist (commented out). Check the event catalog in labapi-events before relying
on any event not listed above.

## 4. API member mapping

### Player

| EXILED | LabAPI | Trap |
|---|---|---|
| `Player.Get(ReferenceHub / GameObject / uint / int)` | `Player.Get(ReferenceHub / GameObject / NetworkIdentity / uint / int)` | LabAPI creates a wrapper on a miss. `Get(int)` is O(N); for an O(1) path use `ReferenceHub.TryGetHub(id, out hub)` then `Player.Get(hub)`. |
| `Player.Get(string)` (id, `@steam` id, or fuzzy name) | `Player.Get(string userId)` exact id; `GetByNickname`, `GetByDisplayName`, `TryGetPlayersByName` | **A direct rename silently loses name lookup** (returns null). `GetByNickname` is prefix match, alphabetical-first, **not** EXILED's `Contains` + shortest name. Write your own search over `Player.ReadyList` if you need the old behaviour. `TryGetPlayersByName` can throw while someone is connecting. |
| `Player.List` (copies per call) | `Player.List` (live view), `Player.ReadyList`, `GetAll(PlayerSearchFlags)` | LabAPI `List` includes host, NPCs, unauthenticated hubs. `ReadyList` = authenticated players plus dummies. Do not enumerate across a `yield`. |
| `Player.Count`, `Server.PlayerCount` | `Player.Count`, `Server.PlayerCount` | LabAPI re-runs a LINQ filter per call and counts dummies. Compute once per tick. |
| `Id` | `PlayerId` | Recycled FIFO; key long-lived data by `UserId` (not for dummies: all share `"ID_Dummy"`). |
| `UserId`, `Nickname`, `DisplayNickname` | `UserId`, `Nickname`, `DisplayName` | `DisplayName` carries a coloured `*` suffix when custom. Compare `Nickname`. |
| `IsVerified` | `IsReady` | True earlier than `Joined`. |
| `IsConnected` | `!IsDestroyed` | Wrapper outlives the hub; never keep it across leave or round restart. |
| `IsNPC` | `IsNpc` (incl. dummies), `IsDummy`, `IsPlayer`, `IsHost` | A dummy is a regular NPC until the next frame. |
| `Role` (object), `Role.Type`, `Role.Set(...)` | `Role` (`RoleTypeId`), `RoleBase`, `SetRole(role, reason, flags)` | `Role = X` always means reason `RemoteAdmin` + `RoleSpawnFlags.All` (teleports, rebuilds inventory, O(N) role-sync messages). Use `SetRole` with explicit flags. EXILED's `Scp079Role`/`FpcRole` have no wrappers: pattern-match the game's role types. |
| `IsNTF`, `IsHuman` | `IsNTF`, `IsHuman`, `IsChaos`, `IsSCP`, `Team`, `Faction` | EXILED `IsNTF` excludes guards; LabAPI `IsNTF` = Foundation forces team. LabAPI `IsHuman` = alive and not SCP (includes Flamingos). Test `Team` or `RoleTypeId` explicitly. |
| `Position` get/set, `Teleport(Vector3)` | `Position` get/set | Getter is zero for non-FPC roles. Each set sends a message. `Rotation` setter has a pitch mismatch: use `LookRotation`. |
| `Health`, `MaxHealth`, `ArtificialHealth`, `HumeShield` | same names | `Health = 0` does not kill. Use `Kill()` / `Damage()` and check the bool (false under god mode or spawn protection). |
| `Hurt(...)`, `Kill(...)`, `Heal(amount, overrideMaxHealth)` | `Damage(...)`, `Kill()`, `Kill(reason)`, `Heal(float)` | `DamageType` overloads: build a game `DamageHandlerBase` / `DeathTranslations` yourself. No `overrideMaxHealth`. |
| `ShowHint(string, float)` | `SendHint(string, float)` | Throws for the host. Never send to the host or unauthenticated hubs. See scpsl-hints-and-hud. |
| `Broadcast(ushort duration, string message, ...)` | `SendBroadcast(string message, ushort duration, ...)` | **Argument order swapped.** Rewrite every call; positional calls usually fail to compile, but a call through variables or defaults can slip past review. |
| `SendConsoleMessage(msg, color)` | `SendConsoleMessage(msg, color = "green")` | One encrypted message per call. |
| `Kick(reason, issuer)` | `Kick(reason)` / `Kick(issuer, reason)` | Asynchronous: player stays in `ReadyList` until the client confirms (about one round trip, fallback about 3 s). A dummy is never removed this way. |
| `Ban(int duration, reason, issuer)` | `Ban(reason, long duration)` / `Ban(issuer, reason, duration)` | Parameter order changed. Duration 0 is a kick. With a positive duration, a bypass-flagged target returns false (check the result). |
| `Mute(bool isIntercom = false)` | `Mute(bool isTemporary = true)` | **Default inverted and the bool means something else.** EXILED `Mute(true)` = intercom-only persistent mute; LabAPI `Mute(true)` = temporary mute until the player disconnects (not until restart). Persistent: `Mute(isTemporary: false)`. No intercom parameter. |
| `IsCuffed`, `Handcuff(cuffer)` | `IsDisarmed` get/set, `DisarmedBy` | LabAPI setter forces the state and sends the whole list to every authenticated hub. |
| `Items`, `CurrentItem`, `AddItem`, `RemoveItem`, `ClearInventory`, `DropItem` | `Items`, `CurrentItem`, `AddItem(ItemType, ItemAddReason)`, `RemoveItem`, `ClearInventory`, `DropItem` | `Items` allocates wrappers per enumeration: cache. `AddItem` returns null at 8 non-ammo items: null-check. |
| `Ammo`, `SetAmmo(AmmoType, ...)`, `GetAmmo`, `AddAmmo` | `Ammo`, `SetAmmo(ItemType, ...)`, `GetAmmo`, `AddAmmo` | `AmmoType` enum -> `ItemType`. Writing into `Player.Ammo` directly never reaches the client. Per-player limits need a patch (LabAPI limits are server-wide). |
| `EnableEffect<T>(duration, intensity, ...)` and `EffectType` overloads | `EnableEffect<T>(byte intensity = 1, float duration = 0, bool addDuration = false)`, `TryGetEffect<T>`, `HasEffect<T>`, `DisableEffect<T>` | **Overload order differs** (duration first vs intensity first). No `EffectType` enum. |
| `CurrentRoom`, `Zone` | `Room` (point query per call), `CachedRoom`, `Zone` | Prefer `CachedRoom` in loops. |
| `Group`, `GroupName`, `RankName`, `SetRank` | `UserGroup`, `GroupName`, `PermissionsGroupName` | `UserGroup` setter fires events and sends several messages. Group name display is not permissions. |
| `CustomInfo`, `InfoArea` | same | LabAPI offers `ValidateCustomInfo`; clients validate. |
| `Scale`, `SetScale`, `SetFakeScale` | `Scale` get/set | Per-viewer fake scale: send the game's scale message yourself. |
| `SessionVariables` | `CustomDataStore<T>.Get(player)` | Typed store, removed after `Left`. See replacements.md. |
| `IsInvisible`, `IsInvisibleFor`, `SetAppearance`, `FakeRoles` | `PlayerEvents.ValidatedVisibility` (`IsVisible` settable), game `RoleSyncEvent`, `Player.GetRoleVisibilityFor` (read only) | GhostMode has no wrapper. Hiding is more than the position list: see scpsl-networking-for-plugins before porting it. |

### Server and Round

| EXILED | LabAPI | Trap |
|---|---|---|
| `Server.Host`, `Name`, `Port`, `MaxPlayerCount` | `Server.Host`, `ServerListName`, `Port`, `MaxPlayers` | `Server.Host` can be null early. |
| `Server.Tps` | `Server.Tps` | Both are Unity frame-time proxies. EXILED: `min(1/deltaTime, MaxTps)`. LabAPI: `round(1/smoothDeltaTime)`, smoothed, scaled by `timeScale`, meaningless in idle mode. See measuring-scpsl-performance. |
| `Server.ExecuteCommand` | `Server.RunCommand` | Runs on the calling thread; may return null. Main thread only. |
| `Server.Restart`, `Shutdown`, `RestartRedirect` | `Server.Restart`, `Shutdown(ushort redirectPort)` | Flush your state on `ServerEvents.Shutdown`. |
| `Round.IsStarted`, `InProgress`, `IsEnded`, `IsLobby` | `Round.IsRoundStarted`, `IsRoundInProgress`, `IsRoundEnded` | No `IsLobby`. `IsRoundEnded` throws before the first round object exists: read `IsRoundStarted` in `Enable()`. |
| `Round.Start()`, `EndRound(force)`, `Restart`, `IsLocked`, `LastClassList` | `Round.Start()`, `Round.End(force)`, `Restart`, `IsLocked`, `Duration` | `End(true)` is undone at the next 2.5 s tick unless a `RoundEndingConditionsCheck` handler sets `CanEnd` (code reading). `Start()` has no already-started guard. |
| `Warhead.Start/Stop/Detonate/Shake/IsInProgress/DetonationTimer/...` | `Warhead.Start/Stop/Detonate/Shake/IsDetonationInProgress/DetonationTime/...` | Renames. |
| `Cassie.Message`, `GlitchyMessage`, `ScpTermination`, `Clear` | `Announcer.Message`, `GlitchyMessage`, `ScpTermination`, `Clear` | LabAPI `Cassie` is `[Obsolete(error)]`: compile error. `DelayedMessage`, `MessageTranslated` are gone. |

### Map, Door, Item, Pickup

| EXILED | LabAPI | Trap |
|---|---|---|
| `Map.Rooms/Doors/Pickups/Ragdolls`, `Room.List`, `Door.List`, `Lift.List`, `TeslaGate.List` | `Map.Rooms/Doors/Pickups/Ragdolls/Elevators/Generators/Teslas/Cameras`, `Room.List`, `Door.List` | EXILED `Lift` = `Elevator`; `TeslaGate` = `Tesla`. |
| `Door.Lock(time, DoorLockType)`, `Door.IsOpen`, `Door.Type` | `Door.Lock(DoorLockReason, bool)`, `IsOpened`, `DoorName`, `NameTag`, `IsLocked`, `LockReason` | No timed lock, no `DoorType` enum. |
| `Item.Create`, `Item.Get(serial)` | `Item.Create`, `Item.Get(serial)` | Serial cache with linear fallback. |
| `Pickup.CreateAndSpawn(type, pos)` | `Pickup.Create(type, pos[, rot])` | `Create` already spawns (`networkSpawn` defaults to true); a second `Spawn()` only logs a warning. Call `Spawn()` only after `networkSpawn: false`. See labapi-wrappers. |
| `Pickup.Get(serial)` (linear) | `Pickup.Get(serial)` (dictionary) | Serials are unique per kind (item vs pickup), not globally. |
| `Map.Broadcast`, `Map.ShowHint`, `Map.TurnOffAllLights`, `Map.Explode` | `Server.SendBroadcast`, per-player `SendHint`, `Map.TurnOffLights`, `SetColorOfLights` | Map-wide hint: loop `Player.ReadyList`. |
| `Plugin<TConfig>.OnEnabled/OnDisabled/OnReloaded`, `Prefix`, `Priority` | `Plugin.Enable/Disable/LoadConfigs`, `Name`, `Priority` | LabAPI never calls `Disable()`. See designing-scpsl-plugins. |

### Permission checks in code

| EXILED | LabAPI |
|---|---|
| `player.CheckPermission("a.b")` (string) | `player.HasPermission("a.b")` (string, from `permissions.yml`) |
| game `PlayerPermissions` flags | `sender.CheckPermission(PlayerPermissions.X, out response)` on the command sender |

Mixing the two (a string where the flag overload is expected) does not compile. See
scpsl-commands-and-permissions.

## Re-verifying on a new build

- Re-run the event catalog (`labapi-events`) and diff event names against section 2. Renames and removed
  events show up as compile errors; **never-raised events do not**, so grep the game for a caller of each
  `On<Event>` invoker you depend on.
- Re-check the timing claims that are code readings, in this order: `Joined` early returns (badge
  mismatch), `Hurting` position relative to spawn protection, `Spawned` vs loadout, late-join role
  before `Joined`, `Dying` cancel leaving 0 HP, `InteractingDoor` locked path.
- EXILED updates move its anchors. Run a test server with EXILED loaded and compare which handler
  fires first for `Hurting`, `Spawning`, `Escaping`, `PickingUpItem`.
- Re-check the **SCP-role** event rows by args and timing; they were matched by name only.
- Re-check `Player.Get(string)`, `Mute` defaults, `Broadcast` and `Ban` parameter order, `Cassie`
  obsolescence: signature changes compile silently when types still fit.
- After a game update confirm each Harmony target in section 3 still has the same parameter list
  (`GetPatchInfo`), and that the native events still exist.
