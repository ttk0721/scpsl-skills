---
name: using-scpsl-skills
description: "Use when starting any work on an SCP: Secret Laboratory (SCP:SL) server plugin or server (write, change, review, debug, port, test, deploy, measure; LabAPI, EXILED, NWAPI, LocalAdmin), or when a server shows low TPS, lag spikes, broken hints or plugins that silently fail to load. Short index: picks the path and the skills to load."
---

# Using scpsl-skills

Verified against LabAPI 1.1.7 / SCP:SL 14.2.7 (2026-10).

<SUBAGENT-STOP>
Subagent on one defined task? Skip path selection; still load the domain skills it touches.
</SUBAGENT-STOP>

<EXTREMELY-IMPORTANT>
Before you write code, touch a server or state an API name, an event's cost or a default, load the skill that holds the fact. Even a 1% chance a skill covers the step means load it first. Remembered SCP:SL APIs are guesses.

This binds your own shortcuts ("I know this API", "too small for skills"), not the owner's choices: see Priority.

Reading this index is cheap; loading the library is not. Load only what the path needs. A clarifying question that needs no SCP:SL fact can come first; one that does waits for the skill.
</EXTREMELY-IMPORTANT>

## Priority

**Owner's explicit instructions, then these skills, then your defaults.** "Skip the spec", "no tests", "use our logger": follow it, say in one line which rule it bypasses and the risk. The owner can waive a rule; you cannot. Between skills: process, `adding-*` recipes, domain.

## Pick the path

State the path in one line, then follow it.

- **A. Small change** (a few lines: one handler, command, log line or config field; no new state, timers, IO, HUD, hot events, patches): no brainstorming, no plan. Load the domain skill, then:
  1. Read the existing plugin; match its subscription style, logger and naming. Can't read it? Ask one question ("which style?"), don't write variants.
  2. Look up every API name in the skills or LabAPI source, and the event's frequency in `events-catalog.md`. No "try X if it doesn't compile".
  3. Deliver ONE change with only the behaviour asked for: no filters, guards or extras nobody requested.
  4. Build; report the change, assumptions, and what you could not run.
- **B. Bounded feature**: short brainstorming-scpsl-features, then the matching `adding-*` recipe, test-first, then verifying-scpsl-work.
- **C. New plugin, or timers, IO, HUD, persistent state, hot events, patches, several servers**: brainstorming-scpsl-features, writing-scpsl-plans, executing-scpsl-plans, verifying-scpsl-work.

**One-way ratchet.** Complexity found mid-task moves the path up. Never down.

## Routing

- Tests: testing-scpsl-plugins; running-a-test-server (ask first; never the owner's ports)
- Bug, slow server: debugging-scpsl-plugins, measuring-scpsl-performance, scpsl-engine-cost-model
- Recipes: `adding-` + per-player-tracking, player-hud, custom-items, custom-roles, gameplay-rules, external-integrations, round-mechanics, map-automation, player-abilities
- Design: designing-scpsl-plugins. Events: labapi-events. Player, Item, Door, Room: labapi-wrappers
- Timers, threads: scpsl-main-thread-and-timing. Join, round, restart: scpsl-game-lifecycle
- Hints: scpsl-hints-and-hud, scpsl-server-specific-settings. Commands: scpsl-commands-and-permissions. Roles, damage: scpsl-roles-damage-effects
- Voice, network, Harmony: scpsl-voice-and-audio, scpsl-networking-for-plugins, scpsl-harmony-patching
- Audit, EXILED, deploy: reviewing-scpsl-plugins, porting-exiled-to-labapi, deploying-scpsl-plugins
- Not answered, or a game update: researching-scpsl-internals

## Non-negotiable for you (the owner may waive them, you may not)

1. Check an event's frequency in the catalog before subscribing. Hot events need a gate.
2. Off the main thread, touch only plain snapshots, never game/LabAPI/Unity objects.
3. One clock for periodic work. No coroutine per player or per event.
4. A round restart reloads the scene. Reset at `WaitingForPlayers`; never keep `Player` across rounds.
5. `Server.Tps` hides hitches. Measure frame time.
6. Every `Logger` call costs a stack walk plus regexes. Don't log on hot paths.
7. `Disable()` is never called. Flush synchronously in `ServerEvents.Shutdown` and on a timer; a kill or crash fires nothing.

## Red flags

- "I know what they want; here's the code": you don't know player count, other plugins, staff exclusions, shared servers. B or C: design first.
- "Mark the API name 'verify'": look it up.
- "Two variants and caveats, to be safe": over-hedging. One change in the plugin's style; unasked extras are scope creep.
- "Path A, so no skills": A skips process, not knowledge.
- "Brainstorm this log line": path A.
- "Load every skill, to be safe": load what the path needs.
- "Test it on their server": ask whether they want local tests, and on whose port. Never assume theirs.
