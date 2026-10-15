# Assembly-CSharp namespace map

Verified against LabAPI 1.1.7 / SCP:SL 14.2.7 (2026-10). Part of researching-scpsl-internals.

Orientation only: public type names, no code. Built from the type listing of a local decompile of the server's `Assembly-CSharp.dll` (one directory per namespace; types in the root namespace sit loose at the top). Use it to pick the directory to grep or the one type to decompile, then read the code locally.

**Entry points.** A LabAPI wrapper exposes the game object it wraps as `.Base` (for example `Door.Base` is a `DoorVariant`, `Player` is keyed by `ReferenceHub`). `grep -n "Base" <wrapper file>` in the LabAPI source tells you the game type, and the table tells you the namespace. Namespaces nest by dots in the name only: `PlayerRoles.PlayableScps.Scp079` is a flat directory, not a path.

**Not in this assembly.** Mirror (`NetworkServer`, `NetworkIdentity`, `NetworkBehaviour`, `SyncVar` attributes, `NetworkWriter`/`Reader`, `Transport`, `InterestManagement`) is `Mirror.dll`. LabAPI is `LabApi.dll` (public source on GitHub). MEC (`MEC.Timing`) and the Discord game SDK are in `Assembly-CSharp-firstpass.dll`. Pools (`NorthwoodLib.Pools.ListPool<T>`, `StringBuilderPool`, `HashSetPool<T>`, `ActionDispatcher`) are `NorthwoodLib.dll`; `GameObjectPools` is `Pooling.dll`; `ParentCommand` and `CommandSystem.CommandHandler` are `CommandSystem.Core.dll`. YAML is `YamlDotNet.dll`. `Logger` is LabAPI's (`LabApi.Features.Console`).

## Root namespace (types loose at the top of the tree)

| Type | What lives there | A plugin author asks |
|---|---|---|
| `ReferenceHub` | The per-player component hub: `roleManager`, `playerStats`, `inventory`, `nicknameSync`, `serverRoles`, `playerEffectsController`, `authManager`, `characterClassManager`, `hints`, `queryProcessor`, `searchCoordinator`, `interCoordinator`, `playerRateLimitHandler`. Statics `AllHubs`, `TryGetHub`, `GetPlayerCount`, `HostHub`; add/remove events | Is this the host or a dummy? Which component owns the field? Who is counted as a player? |
| `CharacterClassManager` | Host start and round start statics (`ServerWaitingForPlayers`, `OnRoundStarted`, `OnMaxPlayersChanged`), round-start trigger, `MaxPlayers` | When does `WaitingForPlayers` really fire? Who starts the round? |
| `ServerConsole` | Server log (`AddLog`), command entry (`EnterCommand`), `Disconnect`, server name refresh, console outputs, `Scs` sender | How do I log to the console, run a command, kick? |
| `ServerStatic` | Dedicated flag, `ServerTickrate` setter (writes `Application.targetFrameRate` when dedicated) | Why is `server_tickrate` not applied? |
| `IdleMode` | Idle enter/exit, pause flag, idle tickrate | Why is the server at 1 FPS? |
| `CustomNetworkManager` | The Mirror network manager subclass: host setup, connection and pre-auth handling, scene change | What runs at boot and on connect? |
| `RoundSummary` | Round end checks, win conditions, statistics, round-end coroutine | When does a round end, who can stop it? |
| `FastRoundRestartController`, `RoundPlayerHistory` | Fast restart switch; per-round player log | Why did the scene not reload? |
| `ServerRoles` | Badges, group assignment (`SetGroup`), permissions bitfield, remote-admin flag, hidden tags | How does a player get a group or a badge? |
| `PermissionsHandler`, `PlayerPermissions`, `UserGroup` | Permission flags, group definitions, config-file parsing | What does permission X check? |
| `NicknameSync` | Nickname, display name, custom info and `PlayerInfoArea` SyncVars | Which SyncVar holds the name? Who is told? |
| `PlayerEffectsController` | Owner of all status effects of one player | How are effects stored and updated? |
| `BanHandler`, `WhiteList`, `ReservedSlot`, `BanPlayer`, `CheaterReport`, `ReportServer` | Ban files, whitelist, reserved slots, kick/ban entry, report flow | Where is a ban stored? Who raises `Banned`? |
| `AlphaWarheadController`, `AlphaWarhead*Panel` | Warhead state machine, detonation, panels, lever | Can a plugin start, stop or lock it? |
| `TeslaGate`, `TeslaGateController` | Tesla trigger and idle logic | Is it per-frame? Who is "inside the range"? |
| `Broadcast`, `BroadcastMessage` | Broadcast RPCs | How are broadcasts sent and cleared? |
| `ServerLogs`, `ServerConfigSynchronizer`, `ServerTime` | Admin log; a networked object that syncs server name, item and ammo limits and RA ban templates to clients; server time | What is logged? Which config values reach clients? |
| `Escape`, `DeadmanSwitch`, `PitKiller`, `Scp559Cake`, `Scp956Pinata` | Escape rules, dead man's switch, the pit trigger that applies an effect, two SCP objects | Escape conditions; special objects |
| `FriendlyFireHandler`, `FriendlyFireConfig` + detectors | Friendly-fire checks and auto-punishment | Who may hurt whom, and what kicks them? |
| `MainThreadDispatcher`, `StaticUnityMethods`, `MemoryCleaner`, `CrashDetector`, `ServerShutdown`, `Headless*` | Main-thread queue (`Dispatch(Action, DispatchTime)`), static `OnUpdate`/`OnLateUpdate`/`OnFixedUpdate` events, memory clean-up helper, shutdown, headless runtime | How do I hop to the main thread? Is there a ready per-frame hook? |
| `RateLimiter`, `CustomInterestManagement` | Per-connection rate limiter; Mirror interest management subclass | Why was a client rate-limited? Who is an observer? |
| `CentralServer`, `DiscordWebhook`, `HttpQuery`, `UnityHttp`, `DotnetHttp`, `ManagedHttp` | Central-server contact, webhooks, HTTP helpers | Does the game phone home? Which HTTP stack? |
| `ItemType`, `ItemCategory`, `HitboxType`, `Hitmarker`, `ExplosionType`, `YamlConfig`, `Misc`, `TimeBehaviour` | Public enums and small utilities | Enum values, config parsing |

## Players, roles, stats

| Namespace | What lives there | A plugin author asks |
|---|---|---|
| `PlayerRoles` | `PlayerRoleBase`, `PlayerRoleManager`, `RoleTypeId`, `Team`, `Faction`, `RoleChangeReason`, `RoleSpawnFlags`, `HumanRole`, `NoneRole`, role interfaces (`IHiddenRole`, `IAFKRole`, `IArmoredRole`...) | How does `SetRole` flow? What is a role, a team, a faction? |
| `PlayerRoles.FirstPersonControl` | `FirstPersonMovementModule`, `FpcMotor`, jump, gravity, scale, collision, backtracker | Why does position or speed differ from what I set? |
| `PlayerRoles.FirstPersonControl.NetworkMessages` | `FpcServerPositionDistributor` and position messages | What is sent per player pair each tick? (the O(N^2) path) |
| `PlayerRoles.FirstPersonControl.Spawnpoints` | `RoleSpawnpointManager`, room and bounds spawn points | Where does a role spawn and who decides? |
| `PlayerRoles.RoleAssign` | `RoleAssigner`, `ScpSpawner`, `HumanSpawner`, tickets, SCP preferences | How are roles picked at round start? |
| `PlayerRoles.Spectating`, `PlayerRoles.Visibility` | Spectator and Overwatch roles; `VisibilityController`, `InvisibilityFlags`, `ICustomVisibilityRole` | Who sees whom? Where does `ValidatedVisibility` come from? |
| `PlayerRoles.Voice` | `Intercom`, `HumanVoiceModule`, `GroupMuteFlags`, voice role interfaces | Which role may speak on which channel? |
| `PlayerRoles.Subroutines`, `PlayerRoles.PlayableScps.Subroutines` | `SubroutineBase`, ability cooldowns, attack ability bases | How is an SCP ability structured? |
| `PlayerRoles.PlayableScps` + `.Scp049` `.Scp079` `.Scp096` `.Scp106` `.Scp173` `.Scp939` `.Scp3114` `.Scp1507` `.HumeShield` | One namespace per SCP: role class, abilities, attacks, targeting, movement; Scp079 also holds cameras, aux, map, overcons, pings, rewards; Scp049 holds zombies; Scp939 holds mimicry | Where is SCP X's rule implemented? Which event covers it? |
| `PlayerRoles.Ragdolls` | `RagdollManager`, `BasicRagdoll`, `DynamicRagdoll`, ragdoll data | How are ragdolls spawned and cleaned up? |
| `PlayerRoles.SpawnData` | Spawn-data writer/reader interfaces | How does a role carry spawn payload? |
| `PlayerStatsSystem` | `PlayerStats` (damage entry), `HealthStat`, `AhpStat`, `HumeShieldStat`, `StaminaStat`, `DamageHandlerBase` and the handler family, `DeathTranslations` | Where does damage go? Which handler type is it? |
| `CustomPlayerEffects` (+ `.Danger`) | About 110 types: status effects (`Bleeding`, `Asphyxiated`, `AntiScp207`...) and the danger stack | Which effect does what? Per-frame cost? |
| `Footprinting` | `Footprint`: a snapshot of a player identity kept after the player is gone | Who is the owner/attacker of a thrown item or dead player? |
| `Achievements` (+ `.Handlers`) | Achievement manager and one handler per achievement | Which gameplay actions are tracked? |
| `AFK` | `AFKManager` | How is AFK decided? |
| `CentralAuth`, `Authenticator`, `Cryptography` | `PlayerAuthenticationManager`, `CentralAuthManager`, tokens; key/hash helpers | What happens between connect and "ready"? What is verified? |

## Inventory and items

| Namespace | What lives there | A plugin author asks |
|---|---|---|
| `InventorySystem` | `Inventory`, `InventoryExtensions`, `InventoryItemLoader`, `InventoryInfo` | How is an item added, dropped, serial-tracked? |
| `InventorySystem.Items` | `ItemBase`, usable and thirdperson bases, interaction blocks | What does every item share? |
| `InventorySystem.Items.Pickups` | `ItemPickupBase`, `PickupSyncInfo`, `PickupStandardPhysics` | How does a pickup sync and move? |
| `InventorySystem.Items.Firearms` (+ `.Modules`, `.Attachments`, `.Ammo`, `.ShotEvents`, `.BasicMessages`, `.Modules.Scp127`) | `Firearm`, about 60 modules (hitscan, reload, recoil, audio), attachment system, ammo sync, shot events | Where is damage and ammo computed? Which module raises the shot? |
| `InventorySystem.Items.Usables` (+ `.Scp330`, `.Scp244`, `.Scp1576`, `.Scp1344`) | Medkit, painkillers, adrenaline, `UsableItemsController`, candy bag and outcomes, SCP-244/1576/1344 | How does item use progress and cancel? |
| `InventorySystem.Items.Keycards` | Keycard item, custom detail classes (name, label, tint, perms, rank, serial) | Where are keycard permissions and looks decided? |
| `InventorySystem.Items.ThrowableProjectiles` | Grenades, flashbang, SCP-018, SCP-2176, effect grenades | How do projectiles explode and who is credited? |
| `InventorySystem.Items.MicroHID`, `.Jailbird`, `.Radio`, `.Armor`, `.Coin`, `.Scp1509`, `.ToggleableLights`, `.Autosync` | One item family each; `Autosync` holds the item-state sync commands, RPCs and message handler | Per-frame cost of radio and Micro-HID? |
| `InventorySystem.Searching`, `InventorySystem.Disarming`, `InventorySystem.Configs` | Search sessions, cuffing, starting inventories and slot limits | How does searching and cuffing work? Where are loadouts? |

## Map and facility

| Namespace | What lives there | A plugin author asks |
|---|---|---|
| `MapGeneration` | `RoomIdentifier`, zone generators, `CurrentRoomPlayerCache`, atlas interpreter, `InitiallySpawnedItems` | When does the map exist? How is a room found? |
| `MapGeneration.Distributors` | `Locker`, `ItemDistributor`, hazards distributor, `Scp079Generator` | Where do items and generators come from? |
| `MapGeneration.RoomConnectors`, `.Rooms`, `.Decoration`, `.Clutter`, `.Holidays`, `.Scenarios`, `.StaticHelpers`, `.Spawnables` | Connectors, multi-level rooms, alarms, holiday variants | Seed and room variants |
| `Interactables`, `Interactables.Interobjects` | Interaction coordinator, doors (`BasicDoor`, `CheckpointDoor`, `BreakableDoor`), elevators, airlocks | Who may interact? How do elevators move? |
| `Interactables.Interobjects.DoorUtils` | `DoorVariant`, lock reasons, permission checks, door events | How are locks and permissions evaluated? |
| `Scp914`, `Scp914.Processors` | `Scp914Controller`, knob settings, per-item processors | What upgrades what? |
| `Hazards` | `EnvironmentalHazard`, sinkhole, tantrum, prismatic cloud | Who is "in" a hazard and how often is it polled? |
| `LightContainmentZoneDecontamination` | `DecontaminationController` | Decontamination timing |
| `Cassie` (+ `.Interpreters`) | Announcement dispatcher, line database, TTS, glitchifier | How is an announcement queued and spelled? |
| `AdminToys` | `AdminToyBase`, primitive, light, speaker, text, capybara, waypoint, SCP-079 camera toy, invisible interactable, shooting target | What does a toy cost per tick? |
| `RelativePositioning` | `RelativePosition`, waypoint types for moving surfaces | Why do positions inside elevators look odd? |

## Rounds, waves, objectives

| Namespace | What lives there | A plugin author asks |
|---|---|---|
| `Respawning` (+ `.Waves`, `.Config`, `.Announcements`) | `WaveManager`, wave classes (NTF/Chaos, mini waves), tokens, influence, wave config | How does a wave start, size, and cancel? |
| `Respawning.Objectives`, `Respawning.NamingRules` | Faction objectives and their footprints; unit naming | What grants influence? |
| `RoundRestarting` | `RoundRestart`, restart types and message | Full vs fast restart |
| `Rounds.Options`, `Metrics` | `RoundOptionManager` with lobby and round-start action lists (action, command, dummy, set-value options); metrics collectors and CSV builder | Which options run in the lobby or at round start? What does the game report? |
| `Waits` | `Wait` (a `MonoBehaviour` with an MEC `_Run` coroutine), `TimeWait`, animator, audio and particle waits, and/or/until managers | Wait steps the game composes into coroutines |

## Networking and voice

| Namespace | What lives there | A plugin author asks |
|---|---|---|
| `Mirror` (here), `Mirror.LiteNetLib4Mirror`, `LiteNetLib` (+ `.Utils`, `.Layers`) | Weaved network code only; the transport (`LiteNetLib4MirrorTransport`, server/client), and the UDP library | Which transport, which delivery methods? |
| `Utils.Networking` | Reader/writer helpers, `MessageStrategies`, hint serialisers | How do custom messages serialise? |
| `Security` | Rate limits (`PlayerRateLimitHandler`, `ServerRateLimit`) | What throttles a client command? |
| `VoiceChat` (+ `.Networking`, `.Playbacks`, `.Codec`, `.Filters`) | `VoiceTransceiver`, `VoiceMessage`, channels, mute and privacy flags, playbacks (radio, intercom, speaker toy), Opus codec | Where is voice received, validated, fanned out? |
| `Query`, `ServerOutput`, `Steam` | Server query protocol, console-output entries for LocalAdmin, Steam server info | What does LocalAdmin read? |

## Commands, admin, config, UI

| Namespace | What lives there | A plugin author asks |
|---|---|---|
| `CommandSystem` (+ `.Commands.RemoteAdmin` and its sub-namespaces, `.Commands.Console`, `.Commands.Shared`, `.Commands.Dot.Overwatch`) | The three command handlers; one class per built-in command | Is there already a command for this? How does its permission check work? |
| `RemoteAdmin` (+ `.Communication`, `.Interfaces`) | `CommandProcessor`, `QueryProcessor`, `PlayerCommandSender`, RA player list and data requests | How is an RA line parsed and answered? |
| `GameCore` | `ConfigFile` (server config and reload), `RoundStart`, `Version`, console types | Which config key is read where? What is the build number? |
| `Utils`, `Utils.ConfigHandler`, `Utils.NonAllocLINQ`, `Utils.CommandInterpolation` | Parsers, config entries, allocation-free LINQ-like helpers, name interpolation | Helpers worth reusing |
| `Hints` | `HintDisplay`, `TextHint`, parameter classes, effects, `*Functions` helper classes (the reader/writer serialisers are in `Utils.Networking`) | What can a hint carry? |
| `UserSettings.ServerSpecific` (+ `.Entries`, `.Examples`) | `ServerSpecificSettingsSync`, `SSButton`, dropdown, keybind, plaintext, slider-style entries | How are SSS entries defined, sent, answered? |
| `Serialization`, `Utf8Json` | YAML comment helpers, JSON library | Which serialiser does the game use? |
| `NetworkManagerUtils.Dummies` | `DummyUtils`, dummy actions, `DummyNetworkConnection` | How are dummies made and driven? |

## Client-only (skip for server questions)

`UserSettings.*` except ServerSpecific, `InventorySystem.GUI*`, `.Hotkeys`, `.Crosshairs`, `.Drawers`, `CameraShaking`, `CustomRendering`, `ProgressiveCulling`, `CustomCulling`, `RadialMenus`, `ToggleableMenus`, `Subtitles`, `FacilitySoundtrack*`, `AudioPooling`, `Audio`, `DeathAnimations`, `Decals`, `WaterPhysics`, `Christmas*`, `OperationalGuide`, `PlayerRoles.Blood`, `PlayerRoles.Filmmaker`, `PlayerRoles.RoleHelp`, `AnimatorLayerManagement`, `FastResources`, `Windows`, and the many loose UI types in the root (menus, loading bars, credits). Their classes still run on the server only when a server component references them, so check `grep` hits before assuming they are inert.

## Re-verifying on a new build

- Regenerate the listing: decompile with the project switch into a fresh directory and list the top-level entries (`ls -d */` inside the tree, never a plain `ls` of the root, which is thousands of lines). Compare the namespace names with this map; new or removed names are the first signal of a refactor.
- For each row you rely on, run `ilspycmd -l c` on the assembly (written to a file) and grep the namespace to confirm the named types still exist.
- Check the "Not in this assembly" paragraph against the `Managed` folder listing.
- A namespace that moved is the usual cause of a broken grep recipe, so fix this file first, then the recipes.
