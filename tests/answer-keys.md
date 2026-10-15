# Answer keys for the scenarios

Public grading keys for the nine scenarios in [scenarios.md](scenarios.md): five code tasks (s1 to
s5) and four process tasks (p1 to p4). They exist so that a reader can re-grade a run without the
private research notes the original grading used (see Limitation 3 in [RESULTS.md](RESULTS.md)).

Each row is one thing a correct answer shows, and the public file that states it. Nothing here
points at private notes or at decompiled code.

## How to use a key

1. Grade one transcript at a time. Count a row as met only if the answer shows the behaviour in
   code or in its design, not if it only mentions the topic.
2. Rows marked **critical** break the server or the feature if missed. Everything else is a mistake
   but not a break.
3. Score 0 to 10 as in scenarios.md ("correct and cheap on a 60-player server"), list the missed
   rows, list mistakes that are not in the key, and note which arm produced the transcript only
   after grading.
4. The last column says how far you can check the row yourself:
   - **L**: the claim is visible in LabAPI's public source (LGPL-3.0, northwood-studios/LabAPI), in
     the signatures of the packages you compile against, or in the .NET documentation.
   - **G**: a game or engine behaviour. LabAPI's source does not show it. The cited file states it
     together with how it was established, and says UNVERIFIED where it was only read from code.
     To check it yourself you need a decompiler or a test server (`researching-scpsl-internals`,
     `running-a-test-server`).
   - **D**: a design rule or a judgement (what is proportionate, what to ask). Check it against the
     cited file's reasoning, not against code.
5. The skills and these keys come from the same research, so they are not independent. A **G** row
   that you cannot confirm on your own build is a reason to distrust the skill, not the grader.

Paths are relative to `skills/`.

## Test 1: code tasks

### s1: activity tracker with a live HUD and HTTP export

| # | A correct answer | Source | Check |
|---|---|---|---|
| s1.1 | Measures time on the wall clock (absolute deadlines or `Stopwatch`), not on `Time.deltaTime` or coroutine waits. An empty server runs about 1 frame per second at a time scale of 0.01, so scaled waits stretch about 100 times. **Critical** if playtime is credited from scaled time. | `adding-per-player-tracking/accounting-and-flush.md`, `scpsl-main-thread-and-timing/SKILL.md` | G |
| s1.2 | Treats a round restart as a scene reload: `Left` fires for every non-host hub and `Joined` again after re-authentication, so a session opened on `Joined` and closed on `Left` is per round, not per visit. The answer either accumulates into `UserId` totals or parks and resumes the session, and says which scope the numbers have. | `scpsl-game-lifecycle/players.md`, `scpsl-game-lifecycle/restart-and-state.md`, `adding-per-player-tracking/SKILL.md` | G |
| s1.3 | Keys state by `UserId` and handles ids that are not identities: dummies share one id, unauthenticated hubs have none, offline mode gives a per-connection id. A `string.IsNullOrEmpty` guard alone is not enough for offline ids. Does not key by `PlayerId` or hold a `Player` across rounds. | `scpsl-game-lifecycle/restart-and-state.md`, `scpsl-game-lifecycle/players.md`, `labapi-wrappers/SKILL.md` | G |
| s1.4 | Flushes in `ServerEvents.Shutdown` synchronously and on a timer, because `Disable()` is never called. Records spooled at shutdown are terminal (open sessions closed with a final record), because nothing in the next process can close them. | `scpsl-game-lifecycle/plugin-boot.md`, `scpsl-game-lifecycle/SKILL.md`, `designing-scpsl-plugins/SKILL.md` | G |
| s1.5 | HTTP runs off the main thread on plain snapshots built on the main thread, with a timeout, a bound on retries and the backlog, and no `.Result` or `.Wait()`. State changes on the result, not before the answer. | `adding-external-integrations/SKILL.md`, `adding-external-integrations/delivery-policy.md` | D |
| s1.6 | One HUD owner. Sends on change plus a keep-alive that the change check does not suppress (duration 3 s, refresh 2 s). Ticking values such as session time go in `TimespanHintParameter`, not in resent text. Does not assume keep-alives are spread by join time: after a restart every session starts together, so the visit is staggered on purpose. | `scpsl-hints-and-hud/SKILL.md`, `adding-player-hud/SKILL.md` | G |
| s1.7 | One clock for all periodic work with a try/catch per job. No coroutine per player, no per-frame event. | `using-scpsl-skills/SKILL.md` (rules 3 and 7), `scpsl-main-thread-and-timing/SKILL.md` | D |
| s1.8 | Voice activity: if it subscribes to `SendingVoiceMessage`, it states the real rate (100 raises per second per speaker) and that each raise allocates; better, it polls `ServerIsSending` and says that a sub-second utterance can be missed. It does not call the event "50 per second, allocation-free". | `adding-per-player-tracking/afk-and-activity.md`, `scpsl-voice-and-audio/SKILL.md`, `labapi-events/SKILL.md` | G |
| s1.9 | HUD placement is stated with its conditions (measured at 1920x1080, 16:9; a right "corner" is the container edge; other setups unverified) and the replace rule as one hint slot per player, rich-text tags are closed, and player-controlled text that reaches a hint has `{` and `}` replaced (cheap insurance; what the client does with a stray brace is itself unverified). | `scpsl-hints-and-hud/SKILL.md`, `scpsl-hints-and-hud/hint-limits-and-broadcasts.md` | G |
| s1.10 | A custom `TimeSpan` format field is a component, not a total: `h` wraps at 24 hours. A timer that can pass a day needs a `d` field. | `scpsl-hints-and-hud/hint-parameters.md` | L |

### s2: TPS alert to a Discord webhook

| # | A correct answer | Source | Check |
|---|---|---|---|
| s2.1 | Does not decide on `Server.Tps` (smoothed, clamped by the maximum delta, probably scaled, never below about 3). Measures frame period or busy time instead (`FrameProfiler`, a player-loop probe). **Critical** if the alert fires on `Server.Tps` alone. | `measuring-scpsl-performance/SKILL.md`, `measuring-scpsl-performance/signals.md` | G |
| s2.2 | Treats idle mode as no data (`Server.IdleModeActive`), including idle that starts while a round is still in progress. Discards the window after any excluded period (restart, lobby, map generation). | `measuring-scpsl-performance/signals.md`, `scpsl-main-thread-and-timing/SKILL.md` | G |
| s2.3 | Does not time a wall-clock window with an Update-segment coroutine wait. | `scpsl-main-thread-and-timing/SKILL.md` | G |
| s2.4 | The threshold is relative to `Application.targetFrameRate`, not a fixed 60, and the answer knows that host start overwrites the configured tick rate. | `measuring-scpsl-performance/SKILL.md`, `measuring-scpsl-performance/signals.md` | G |
| s2.5 | The webhook goes out through a bounded client off the main thread, with a timeout and no `.Result` or `.Wait()`. The cooldown and any "sent" state are charged on the result, so a failed send neither burns the cooldown nor looks like success. Honours `retry_after`. | `adding-external-integrations/SKILL.md`, `adding-external-integrations/delivery-policy.md` | D |
| s2.6 | The plugin builds. `using UnityEngine;` together with `using LabApi.Features.Console;` makes a bare `Logger` ambiguous; the answer aliases or qualifies it. | `reviewing-scpsl-plugins/SKILL.md` | L |
| s2.7 | `RequiredApiVersion` is a literal (`new Version(1, 1, 7)`), not `LabApiProperties.CompiledVersion`, which is computed from the running DLL and so never fails. | `deploying-scpsl-plugins/loader-and-config.md`, `designing-scpsl-plugins/SKILL.md` | L |
| s2.8 | Reads `Config` at use time or overrides `LoadConfigs()`, because a config reload replaces the object. Guards a null `Config` (empty `config.yml`). `Enable()` is idempotent, does fallible work first and subscribes last; nothing depends on `Disable()`. | `deploying-scpsl-plugins/loader-and-config.md`, `scpsl-game-lifecycle/plugin-boot.md` | L |
| s2.9 | Handles `RoundRestarted` (it can fire twice) and parks the detector until the next lobby. | `scpsl-game-lifecycle/SKILL.md`, `scpsl-game-lifecycle/timeline.md` | G |
| s2.10 | Install notes put libraries in `dependencies/`, never `plugins/`, and end with a check of the boot log for the enable line and for load errors. | `deploying-scpsl-plugins/SKILL.md`, `deploying-scpsl-plugins/verify-and-brand.md` | G |

### s3: a "master keycard" tracked by serial

| # | A correct answer | Source | Check |
|---|---|---|---|
| s3.1 | Identity is the item serial in a plugin-owned set, tagged at the call site that creates or gives the item. Not a held `Item` or `Pickup` wrapper, which are different objects with the same serial. | `labapi-wrappers/items-and-pickups.md`, `adding-custom-items/serial-lifecycle.md` | G |
| s3.2 | The permission check compiles: game flags go through `CheckPermission(PlayerPermissions.X, out response)`. `HasPermission(s)` takes strings. **Critical** if it calls `HasPermissions` with a `PlayerPermissions` value. | `scpsl-commands-and-permissions/SKILL.md` | L |
| s3.3 | Lets the holder through a door with `ev.CanOpen = true` on `InteractingDoor`, which covers locked and unlocked doors. Does not cancel and then toggle `Door.IsOpened`: a cancel on the lock path still shows other plugins and the player a denial, and `IsOpened` ignores locks. | `labapi-events/event-mechanics.md`, `labapi-wrappers/facility.md`, `adding-map-automation/SKILL.md` | G |
| s3.4 | The serial counter is not reset per round. It runs across rounds and wraps after about 65,000, so the set is cleared at round boundaries and a lookup validates the item type. | `labapi-wrappers/items-and-pickups.md`, `adding-custom-items/serial-lifecycle.md` | G |
| s3.5 | Does not prune the set by sweeping on a timer, and never drops a serial on `PickupDestroyed` (picking up destroys the pickup). If it prunes at all, it proves the object is gone twice, a frame apart. | `labapi-wrappers/items-and-pickups.md`, `adding-custom-items/serial-lifecycle.md` | G |
| s3.6 | A disconnecting holder's items are removed with no pickup, so a "master" item does not survive its owner. A cap on the number of marks drops the serial in `Left`. | `labapi-wrappers/items-and-pickups.md` | G |
| s3.7 | `Pickup.Create` already spawns by default, so no second `Spawn()`. A full inventory is handled (`AddItem` can return null). | `labapi-wrappers/items-and-pickups.md` | L |
| s3.8 | Only subscribes to events whose frequency was looked up in the catalog (`InteractingDoor` is per-action, not hot). Cost is argued from structure (one early-out, one lookup), not from an unmeasured microsecond figure. | `labapi-events/events-catalog.md`, `measuring-scpsl-performance/SKILL.md` | D |
| s3.9 | Literal `RequiredApiVersion`, null-guarded `Config`, idempotent `Enable()` (as s2.7 and s2.8). | see s2 | L |

### s4: anti-ESP

| # | A correct answer | Source | Check |
|---|---|---|---|
| s4.1 | Omitting a target parks the model far away on the client at once (no grace period, no freeze) and the client snaps back on return. Footsteps, proximity voice and gunshot audio are positioned on that transform, so they go silent. The answer does not say "sound is not affected". **Critical** if the filter hides everything beyond a short radius without an audibility set. | `scpsl-networking-for-plugins/hiding-players.md`, `scpsl-networking-for-plugins/SKILL.md` | G |
| s4.2 | The send set is the visibility set plus the audibility set (footstep, voice, shot and item-sound radii), with a margin that covers the observer's own movement, round-trip time, update interval and the age of the decision when a periodic sweep is used. | `scpsl-networking-for-plugins/hiding-players.md`, `designing-scpsl-plugins/design-checklist.md` | G |
| s4.3 | Hook choice is real: the LabAPI `ValidatedVisibility` event (L: `IsVisible` is settable) or a Harmony patch on a method and parameter that exist. An invented parameter is **critical**. Any patch checks the pristine code before patching. | `scpsl-networking-for-plugins/hiding-players.md`, `scpsl-harmony-patching/SKILL.md` | L |
| s4.4 | The per-pair event is hot: arguments are allocated per pair per frame even with no subscriber. The handler answers from a cache with no allocation, no dictionary lookup and no geometry per pair. | `labapi-events/SKILL.md`, `scpsl-engine-cost-model/SKILL.md`, `scpsl-networking-for-plugins/hiding-players.md` | G |
| s4.5 | Room-graph filtering uses open portals, not a hop count: rooms joined by an open doorway or hallway can see each other well beyond one hop. Portal state is `CanSeeThrough or TargetState or GetExactState() > 0` and a door counts as closed only when the animation ends. Surface, pocket dimension and rooms that fail lookup fail open to vanilla. | `scpsl-networking-for-plugins/hiding-players.md` | G |
| s4.6 | The first shot of a hidden shooter is silent unless the shot always carries the shooter position; a reactive reveal after the shot event is too late. Reveal is immediate and event-driven for voice and shots; hiding waits about a second of "not needed". | `scpsl-networking-for-plugins/hiding-players.md` | G |
| s4.7 | Exempts what other mechanics need: non-human roles (173, 096, 049 and zombie mechanics are server-side visibility), noclip targets, moving elevators, targets without a room, and receivers that have their own perception. Applies the vanilla distance cap to SCP-1344 wearers instead of exempting them completely. | `scpsl-networking-for-plugins/hiding-players.md`, `scpsl-roles-damage-effects/roles-and-spawning.md` | G |
| s4.8 | States that omission from the position stream does not remove the position from role-sync data (`RoleSyncInfo`, join-time packs), and says whether it patches that or accepts the leak. | `scpsl-networking-for-plugins/hiding-players.md` | G |
| s4.9 | Starts in shadow mode: computes, does not apply. The false-negative audit is live in shadow mode (about 20 sampled to-hide pairs per tick, head, chest and feet, both directions, the vision layer mask) and a pass is defined as zero clear rays. | `scpsl-networking-for-plugins/hiding-players.md` | D |
| s4.10 | Fails open: an exception, a bad graph or a skipped door switches the layer off for the round. Per-player caches are reset on join, leave and round boundaries (player ids are recycled). Does not ship its own `0Harmony.dll`. | `scpsl-networking-for-plugins/hiding-players.md`, `deploying-scpsl-plugins/SKILL.md`, `scpsl-harmony-patching/SKILL.md` | D |
| s4.11 | Cost is not stated from an unmeasured ray count. Per-pair raycasting is a second stage or an audit sample, not the primary mechanism. | `scpsl-networking-for-plugins/hiding-players.md`, `measuring-scpsl-performance/SKILL.md` | D |

### s5: port an EXILED fragment

| # | A correct answer | Source | Check |
|---|---|---|---|
| s5.1 | Every LabAPI name exists (grep the LabAPI source). The mapping uses `SendBroadcast(text, duration)` (argument order differs from EXILED), `SendHint(text, duration)`, `Team` rather than `Faction` where it means team, and `ReadyList` rather than `List`. | `porting-exiled-to-labapi/mapping.md`, `porting-exiled-to-labapi/SKILL.md` | L |
| s5.2 | Cancels with the LabAPI event's own `IsAllowed = false`, never reassigns `ev.DamageHandler` in `Hurting` (the game ignores it), and null-checks `Attacker`. States that LabAPI `Hurting` runs after spawn protection and god mode, so those victims never reach the handler. | `porting-exiled-to-labapi/mapping.md`, `labapi-events/SKILL.md` | G |
| s5.3 | The delayed call and the welcome do not capture a `Player` across time. State is keyed by `UserId`; if a wrapper is held, it is tested with `IsDestroyed`, not `ReferenceHub == null`. | `labapi-wrappers/SKILL.md` | L |
| s5.4 | Items given at round start are given from `ChangedRole` (or `ReceivedLoadout`), because roles are `None` inside `RoundStarted`. No guessed one-second delay. | `scpsl-game-lifecycle/SKILL.md`, `porting-exiled-to-labapi/mapping.md` | G |
| s5.5 | `Joined` fires at the end of authentication, so `IsReady` is already true. No welcome delay. It is skipped on a badge mismatch and fires again every round. A late joiner's role events come before `Joined`. | `porting-exiled-to-labapi/SKILL.md`, `scpsl-game-lifecycle/players.md` | G |
| s5.6 | Does not port a MEC loop as a coroutine. One staggered job on a wall clock, with a try/catch. If it keeps a coroutine, it knows coroutines survive restarts and that an exception ends one for good. | `porting-exiled-to-labapi/SKILL.md`, `scpsl-main-thread-and-timing/SKILL.md` | G |
| s5.7 | The HP hint is sent on change plus a keep-alive (3 s duration, 2 s refresh), not resent every second. It does not claim "zero allocation", and `ReadyList` is not described as a snapshot (it is lazy LINQ over the live collection). | `scpsl-hints-and-hud/SKILL.md`, `labapi-wrappers/SKILL.md` | G |
| s5.8 | A catch in a frequent handler does not log an unbounded exception per raise (`Hurting` is hot). Rate-limit it. | `labapi-events/SKILL.md` | D |
| s5.9 | Literal `RequiredApiVersion`, `Config` read at use time, idempotent `Enable()`, no reliance on `Disable()`. No `MEC.dll` reference (MEC has no DLL of its own). Shared libraries go in `dependencies/`. | `deploying-scpsl-plugins/SKILL.md`, `scpsl-game-lifecycle/plugin-boot.md` | L |
| s5.10 | Names the timing differences between EXILED and LabAPI events instead of hiding them. | `porting-exiled-to-labapi/mapping.md` | D |

## Test 2: process tasks

The owner's hidden facts are the F-numbers in the fact sheet in scenarios.md. Which facts bear on a
scenario is a choice of this key, made from the run notes. Both files below are in `skills/`.

### What every process run is graded on

| # | A correct run | Source | Check |
|---|---|---|---|
| pr.1 | Reads what it can read first (csproj, handlers, existing HUD code) and never asks what the files answer. | `brainstorming-scpsl-features/SKILL.md` | D |
| pr.2 | Asks about the environment before the design: peak players, EXILED and other plugins, number of servers and shared data, an existing panel API, whether the server sits empty at night. One question per message, with a recommendation. The budget is about 5 to 7 questions for a real feature, counted per question, not per message. | `brainstorming-scpsl-features/SKILL.md`, `brainstorming-scpsl-features/question-bank.md` | D |
| pr.3 | For anything that rewards, ranks, counts, measures or punishes players, "who counts" is asked or listed as a numbered assumption with a default. Staff, spectators, AFK players and dummies are excluded by default and the owner confirms. | `brainstorming-scpsl-features/SKILL.md`, `brainstorming-scpsl-features/question-bank.md` | D |
| pr.4 | Ends discovery with numbered assumptions, each with a default, then shows a design with per-second cost and waits for a yes before writing code. | `brainstorming-scpsl-features/SKILL.md`, `brainstorming-scpsl-features/process-details.md` | D |
| pr.5 | After approval, an explicit "write the code" from the owner wins. The agent says what it skips. | `brainstorming-scpsl-features/SKILL.md` | D |
| pr.6 | Asks whether the owner wants local tests at all. Never uses the owner's ports (7777 and 7778 here); offers its own instance on a free port. A "no" is final. | `brainstorming-scpsl-features/SKILL.md`, `running-a-test-server/SKILL.md` | D |
| pr.7 | Cost lines are labelled estimate or measured. No microsecond figure without a benchmark. | `measuring-scpsl-performance/SKILL.md` | D |

### p1: "I want a plugin that shows players their kill stats in a corner of the screen."

Facts that bear: F1 to F8. A run is credited with a fact if it asked about it or handled it in the
design with a stated assumption.

| # | A correct run | Source | Check |
|---|---|---|---|
| p1.1 | F1, F2: sizes the plan for about 60 players and two servers; every record carries a server id. | `brainstorming-scpsl-features/question-bank.md` | D |
| p1.2 | F3: asks whether EXILED and custom role or item plugins are installed, and defines a teamkill by the rule the owner gives, because custom roles change team and faction. | `brainstorming-scpsl-features/question-bank.md` | D |
| p1.3 | F4, F5: asks which plugins write to the screen. Designs one HUD owner and a re-assert after spawn and role change, or a compositor. States the replace rule and the placement with their measured conditions (one hint slot per player; layout measured at 1920x1080, 16:9 only) and does not promise a pixel position elsewhere. | `adding-player-hud/SKILL.md`, `scpsl-hints-and-hud/SKILL.md`, `scpsl-hints-and-hud/hud-compositor.md` | G |
| p1.4 | F6: asks for the existing panel API before designing endpoints. Batches writes, with an id that makes a retry idempotent. | `brainstorming-scpsl-features/question-bank.md`, `adding-external-integrations/SKILL.md` | D |
| p1.5 | F7: staff do not enter anything that ranks. | `brainstorming-scpsl-features/SKILL.md` | D |
| p1.6 | The engine rules of s1 apply to the code or spec it ends with (one clock, snapshots off-thread, `UserId` keys, flush on `Shutdown`). | see s1 | G |

### p2: "I want to reward players for being active."

Facts that bear: F1, F2, F6, F7, F10. The original owner also had an explicit rule that only active
time counts.

| # | A correct run | Source | Check |
|---|---|---|---|
| p2.1 | Asks what "rank" and "active" mean. Treats AFK, spectating and dead time as not active, and says that a movement-based AFK rule can be beaten by a macro. | `brainstorming-scpsl-features/question-bank.md`, `adding-per-player-tracking/afk-and-activity.md` | D |
| p2.2 | F7: asks who counts. Staff are excluded and the check is evaluated on every sample, because groups can change mid-session. | `brainstorming-scpsl-features/question-bank.md`, `brainstorming-scpsl-features/SKILL.md` | D |
| p2.3 | F2: totals from two servers add up; the plugin sends totals or ids that make a retry safe; the panel decides thresholds. | `adding-per-player-tracking/accounting-and-flush.md`, `adding-external-integrations/delivery-policy.md` | D |
| p2.4 | F10: asks about round length and empty nights, then handles both. The idle clock is per `UserId` and is not reset by a restart or a respawn. Accounting runs on the wall clock, so idle mode (time scale 0.01, one frame per second) neither stalls it nor credits time that did not pass. | `adding-per-player-tracking/SKILL.md`, `adding-per-player-tracking/accounting-and-flush.md`, `scpsl-game-lifecycle/idle-config-dummies.md` | G |
| p2.5 | F6: asks which existing panel API grants the rank instead of designing a new pipeline. | `brainstorming-scpsl-features/question-bank.md` | D |
| p2.6 | Progress survives disconnects and restarts: a journal or outbox, a synchronous flush on `Shutdown`, no network call there. | `adding-per-player-tracking/SKILL.md`, `scpsl-game-lifecycle/plugin-boot.md` | G |

### p3: "I need something that detects speedhackers."

Facts that bear: F1, F3, F8, F9. The original owner also had speed multipliers on custom roles and
speed effects, and elevators and the pocket dimension moving players. Add them to the sheet to make
p3 harder.

| # | A correct run | Source | Check |
|---|---|---|---|
| p3.1 | F9: report-only, with evidence, to staff. Shadow mode first. No automatic ban or kick. | `brainstorming-scpsl-features/question-bank.md` | D |
| p3.2 | F3: asks what legitimately changes speed (custom role plugins, status effects such as a movement boost or slowness) and does not hard-code one legal speed. Asking the owner how the rule is defined is the point. | `brainstorming-scpsl-features/question-bank.md`, `scpsl-roles-damage-effects/stats-and-effects.md` | G |
| p3.3 | F8: instant moves (map teleporters, elevators, the pocket dimension, spawn teleports) are not speed. Whether a map plugin's teleport raises any event depends on the plugin, so the run checks or asks instead of assuming; a jump the detector did not see is not counted as movement. | `brainstorming-scpsl-features/question-bank.md`, `adding-per-player-tracking/afk-and-activity.md`, `scpsl-roles-damage-effects/roles-and-spawning.md` | G |
| p3.4 | F1: samples on one staggered clock for about 60 players, not per frame and not per pair. Cost is expressed as a ratio, not an invented figure. Does not use `Server.Tps`. | `scpsl-main-thread-and-timing/SKILL.md`, `measuring-scpsl-performance/SKILL.md` | G |
| p3.5 | Staff and spectators are not checked unless the owner says so. A report is sent off the main thread from a snapshot. | `brainstorming-scpsl-features/question-bank.md`, `adding-external-integrations/SKILL.md` | D |
| p3.6 | After approval and "write the code", delivers code, or says plainly what it skips. A spec only, after an explicit order to write code, costs points. | `brainstorming-scpsl-features/SKILL.md` | D |

### p4: "Log a line when a player joins." (a small change; the owner's plugin can be read)

No hidden fact bears here. The grade is proportionality.

| # | A correct run | Source | Check |
|---|---|---|---|
| p4.1 | Names path A and does not run brainstorming or write a plan. | `using-scpsl-skills/SKILL.md` | D |
| p4.2 | Matches the plugin's own subscription style and logger. At most one question, and only if the plugin cannot be read. | `using-scpsl-skills/SKILL.md` | D |
| p4.3 | Delivers one change. No second variant, no host or dummy filter, no config, no file output, no list of caveats nobody asked for. | `using-scpsl-skills/SKILL.md` | D |
| p4.4 | Uses the real `CustomEventsHandler` signature (`On{Category}{Event}` taking the event args type). A method with another signature is silently ignored, not an error, so the run says it. | `labapi-events/event-mechanics.md` | L |
| p4.5 | `Joined` is raised after identity is set, so nickname and user id are readable there. Earlier events for a late joiner see a null user id and a null nickname. `Joined` is not a hot event. | `scpsl-game-lifecycle/players.md`, `labapi-events/events-catalog.md` | G |
| p4.6 | Notes that LabAPI's console logger is `LabApi.Features.Console.Logger` if the owner's code uses another name. Says what it could not build or run. | `adding-player-abilities/SKILL.md`, `reviewing-scpsl-plugins/SKILL.md` | L |

## Traps seen in the original runs

These are the mistakes that closed-book agents made most often. A transcript that contains one has
missed the row named next to it.

- `Server.Tps` as an alert signal (s2.1), and no idle-mode guard (s2.2).
- A permission check that mixes string permissions with the game flag enum (s3.2).
- Cancel plus `IsOpened` to open a locked door (s3.3).
- "Serials restart each round" (s3.4).
- A Harmony patch on a parameter that does not exist, and "sound is not touched" (s4.1, s4.3).
- Session objects opened on `Joined` and closed on `Left` (s1.2).
- `RequiredApiVersion` from `CompiledVersion` (s2.7), a cached `Config` (s2.8), a flush in `Disable()`
  (s1.4).
- Medkits or items given from `RoundStarted` after a guessed delay (s5.4).
- Counting staff and spectators by default in a reward (pr.3).
- Two variants and seven caveats for a one-line change (p4.3).

## Limits of these keys

- They were written after the original grading, from the grade files, keeping only what a public
  skill states. A fact that only the private notes supported is not here, so a transcript can lose
  a point in the original grading for something this key does not list. Re-grading with this key
  will not reproduce the original scores.
- **G** rows are as strong as the cited skill's verification status. Several are code reading only
  and say so in the file.
- The keys describe LabAPI 1.1.7 and SCP:SL 14.2.7. After a game update, re-check them with the
  skills (see `docs/maintenance.md`).
