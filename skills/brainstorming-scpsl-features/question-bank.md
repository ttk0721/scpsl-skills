# SCP:SL question bank (brainstorming-scpsl-features)

Verified against LabAPI 1.1.7 / SCP:SL 14.2.7 (2026-10).

Ordered by how much the answer changes the design. Work top-down through the interactive question
tool, multiple choice with your recommended answer first; independent questions may share one call
(up to 4). Before asking, check the "Read first" column;
skip anything the repo, config or an earlier answer settles. Stop when no remaining answer would
change a decision: write the rest down as assumptions in the design instead.

## 1. Where it runs

| Question | Typical choices | What the answer changes | Read first |
|---|---|---|---|
| Peak players? | under 20 / 20-40 / 40-60 / more | Every cost line multiplies by it. Per-player periodic work must be staggered; anything per pair is N². 30 dummies alone cost about 4 ms of main-thread time per frame, so plugins spend from a shared, finite frame budget | `max_players` in the server's gameplay config |
| **Who counts?** (any feature that rewards, ranks, counts, measures or punishes players: rewards, stats, playtime, leaderboards, anti-cheat, moderation) | (a) regular players only: staff, spectators, AFK and dummies excluded (recommended) / (b) staff count too / (c) per group: tell me which | Whose time, kills or behaviour enter the numbers, who can be rewarded or punished, and what the detector must ignore. Never decide it silently: see "Who counts" below | Permission groups in the server's config, `Exiled.*` usings (staff checks differ by framework) |
| How many servers or ports, and is data shared between them? | one / several, separate data / several, shared totals | Folder: `plugins/<port>` or `plugins/global` (a global plugin needs its dependencies in the global dependencies folder too, or it fails on every other port). Shared data needs a server id in every record, and the same player can be online on two servers at once | Per-port folders on the server |
| LabAPI only, or EXILED too (which version)? | LabAPI only / EXILED 9.x / not sure | Plugin type and event layer. EXILED raises its events from its own patches at other points in the same game method, so timing and cancel effects differ. EXILED's reload calls `Enable()` again, without `Disable()`, on LabAPI plugins that sit in EXILED's folder. EXILED-only APIs (custom roles, permissions) are on the table only if EXILED is (porting-exiled-to-labapi) | csproj references, `Exiled.*` usings |
| Which other plugins touch the same things? | see the list below | The whole shape of the feature | Server plugin folders, if given |
| Server tickrate? | default / lower / unknown | Frame budget is 1000 / real frame rate. A `server_tickrate` below 60 stays inert until a config reload or the first idle exit | Gameplay config |
| Does it sit empty (idle mode), e.g. at night? Round length? | yes / no; minutes per round | Idle mode drops to about 1 frame/s with time scaled by 0.01: MEC `Update` waits stretch about 100x, `Server.Tps` reads nonsense (296 in idle mode). Playtime, timers and flush schedules must use wall clock. Short rounds mean frequent scene reloads | Gameplay config `idle_mode_*` |

Other plugins to ask about by name, with what each one breaks:

- **Custom role or item plugins** (UncomplicatedCustomRoles, UncomplicatedCustomItems, EXILED CustomItems): change speed, team, faction, health and item identity. Any rule about "teamkill", "legal speed" or "which item" must know them. Several of them call an unscoped `UnpatchAll` on disable, which also removes your Harmony patches (scpsl-harmony-patching, who-else-patches.md).
- **Map plugins with schematics and teleporters** (ProjectMER and similar): move players instantly. Whether that raises any event you can subscribe to depends on the plugin: check, do not assume. Movement, position and anti-cheat logic must allow for it.
- **HUD plugins and plugins that send hints** (a corner HUD, an info plugin that refreshes everyone's hints on spawn or role change): the server keeps no record of what a player sees and does not arbitrate between senders. Design needs one HUD owner, a shared compositor, or an on-demand alternative (scpsl-hints-and-hud).
- **Plugins that already patch or cancel the same events** (anti-cheat, round-flow, respawn plugins): cancels do not short-circuit, the last writer wins.
- **Plugins that use Server-Specific Settings**: SSS is one global last-writer-wins array; ids collide (scpsl-server-specific-settings).

### Who counts

Ask it once, early, as one multiple-choice question (row above), for every feature that rewards,
ranks, counts, measures or punishes players. A request like "reward activity" or "top killers"
never says it, and the usual wrong default (counting everyone, staff included) is only caught if
the owner happens to read the proposal. If the budget is spent, do not drop it: list the six
rows below as numbered assumptions, each with its default, for the owner to confirm in one reply
("A5: staff groups excluded; A6: spectators and Overwatch excluded; ..."). **Staff default to
excluded**, but the default is still shown and confirmed. "Excluded" means two things to settle:
not counted, and not reported or rewarded at all (no send, no read of their stored data).

| Group | Default to propose | What to know (verified) |
|---|---|---|
| Staff / admin groups | Excluded; ask which groups | LabAPI `Player` offers `RemoteAdminAccess`, `PermissionsGroupName` (permissions group, null if none), `UserGroup`, `IsNorthwoodStaff`, `IsGlobalModerator`. `GroupName` is only the displayed badge text, not the permission group. Which groups the owner means is a server fact: ask by name. Staff can also hold a normal gameplay role at the moment, so a role check alone does not find them. Re-evaluate on every sample, not only at join: groups can change mid-session (EXILED's own staff APIs: UNVERIFIED here) |
| Spectators and Overwatch | Excluded | Dead players are Spectators (`Team.Dead`); lobby players hold role `None`, also `Team.Dead`; with `late_join_time: 0` every late joiner is a Spectator. So "spectating" is a large share of connected players, and a counter that skips it is an owner decision, not a detail. `Player.IsOverwatchEnabled` exists; whether Overwatch also appears as its own role is UNVERIFIED here |
| AFK / idle players | Excluded; ask what "active" means | LabAPI wrappers expose no AFK flag (nothing found in the source), so any AFK rule is the plugin's own heuristic (movement, events, input) and can be beaten by a macro: say so and ask whether that is acceptable. Activity checks: scpsl-roles-damage-effects |
| Dummies / NPCs | Excluded from everything | `IsDummy`/`IsNpc` on the wrapper; no `Joined`, all share `UserId` `ID_Dummy`, and `Player.ReadyList` includes dummies. A shared `UserId` merges every dummy into one record |
| Tutorial and special roles | Excluded unless the owner wants them | `Player.IsTutorial` is a role check; custom roles from other plugins change team and faction (ask which plugins) |
| Offline-mode or unauthenticated players | Excluded from stored data | Offline mode gives `ID_Offline_<netId>_<ts>`, a new id per connection, so nothing keyed on it accumulates; unauthenticated hubs have a null `UserId` |

## 2. Exact behaviour and edge cases

Ask only the rows where the default below is not obviously what the owner wants.

| Edge case | Why it matters | Common default to propose |
|---|---|---|
| Round restart | A scene reload: every `Player`, room, door and pickup is destroyed; `Left` fires for every player and `Joined` again after re-auth (derived from code order, not observed live). "Per round" vs "per session" must be named | Per-round state reset at `WaitingForPlayers`; per-player data keyed by `UserId` |
| Disconnect mid-action | A `Left` during the restart window is usually teardown, not a quit | Keep pending data by `UserId`; no penalty for teardown leaves |
| Spectators and Overwatch | Do they count, see the HUD, earn time? | Excluded from counting, HUD optional (covered by "Who counts" in section 1) |
| Dummies / NPCs | No `Joined`, all share `UserId` `ID_Dummy`, counted as players | Excluded from everything |
| Staff and admins | Exclusion from rewards, rankings, detection; immunity is a business rule | Excluded, shown as a numbered assumption the owner confirms; never decided silently (section 1, "Who counts") |
| Late joiners | Once the round has started they get a role before `Joined`, with no nickname yet. `Joined` is also skipped for a connected player whose badge UserID or nickname mismatches | Start tracking at `Joined`, reading nickname there |
| Custom roles, teams | Teamkill, kill credit, speed limits under custom roles | Ask how the owner defines the rule |
| Cheating the metric | AFK spinning, kill farming with friends | Ask what is acceptable |

## 3. Data

| Question | What the answer changes |
|---|---|
| Must it survive a round restart? A server restart? | Round only: memory. Server restart: external store plus a synchronous flush on `ServerEvents.Shutdown` (LabAPI never calls `Disable()`; a crash fires nothing, so also flush on a timer) |
| Where should it live? | Existing panel API / database / file per server. **Ask for the owner's existing API before designing endpoints or tables**; get its contract, auth and rate limits |
| Shared across servers? | Server id field, concurrent sessions, which server is the writer |
| Sum or maximum, and what if a send is retried? | Idempotency key reused across retries of the same batch; "best streak" is a max, not an increment |
| Who reads it, how fresh must it be? | Batch interval vs live sends; off-thread IO with a main-thread snapshot (scpsl-main-thread-and-timing) |

## 4. Admin and player UX

| Question | What the answer changes |
|---|---|
| Commands: remote admin, server console, or player console (`.cmd`)? | Handler type and permission model (scpsl-commands-and-permissions) |
| Which permission, which groups? | LabAPI and EXILED permission wildcards differ |
| What must be configurable, and live-reloadable? | Read `Config.X` at use; reload replaces the object |
| Kill switch? Per feature? | Runtime flags; hot-event features subscribe only while on |
| Player toggle? Command or Settings-tab toggle (SSS)? Should the choice persist? | SSS registry and id scheme vs a simple command; where the preference lives |
| Status command for admins? | Counters for sends, failures, deferred items |

## 5. Failure policy

| Question | What the answer changes |
|---|---|
| External service down or slow: what may players notice? | Transient (timeout, 5xx, 429) retried with a bound; other 4xx stop; deferred vs dropped named in state |
| False positives: acceptable? Automatic punishment? | Report-only with evidence to staff, shadow mode first, fail open |
| Abuse: who can trigger it, how often? | Rate limits, authorisation inside handlers, client values treated as untrusted |
| What happens on the first round after deploy? | Migration, empty-store behaviour, defaults |

## 6. Testing

Never assume. Ask in this order, each as its own question:

1. Do you have a dedicated server you can run locally, and do you want me to run tests on it?
   (a) yes, default install path, (b) yes, another path: which? (c) no, or I test myself: give me
   a checklist. Default install, config and log paths per OS are in running-a-test-server; ask for
   the path only if it differs.
2. If yes: is that server public? A public server has real players: never test there. Do not rely
   on the answer, check: `contact_email` in the port's gameplay config must be unset, and after boot
   the game logs that the server will not be visible on the public list. `check-private.sh` in
   running-a-test-server does both.
3. Which ports are yours (for example 7777, 7778)? Test on one of them, or should we create our
   own instance on an unused port (for example 9999)? Recommend our own instance; touch the
   owner's ports only on an explicit request.

A "no" is final: the spec's test plan becomes a manual checklist and a measurement plan the
owner can run, and nothing later pushes tests on them. running-a-test-server does not repeat
questions 1 and 3: pass the answers on. Windows default paths there are marked unverified; if the
owner's differ, ask.

## Example of the stop rule

For a playtime reward, "who counts" flips the whole design (staff, spectators and AFK time in or out), so it is asked, or listed as confirmed assumptions: never defaulted in silence. For a kill-stats HUD, font size and colours do not change the design: make them config with a
default and do not ask. "Is another plugin sending hints?" flips the HUD architecture: ask it
first.

## Re-verifying on a new build

- Check that a restart still raises `Left` for every player and `Joined` after re-auth, and that
  `WaitingForPlayers` still fires every round (scpsl-game-lifecycle).
- Re-check idle-mode frame rate and time scale and the `server_tickrate` override at boot
  (scpsl-game-lifecycle, idle-config-dummies.md).
- Re-check whether `plugins/global` still requires global dependencies and the public-list log
  line wording (running-a-test-server).
- Re-check which installed plugins call unscoped `UnpatchAll` (scpsl-harmony-patching).
- Re-check that `Disable()` still has no caller and `ServerEvents.Shutdown` still fires on `exit`.
