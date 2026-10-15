# Spec template (brainstorming-scpsl-features)

Verified against LabAPI 1.1.7 / SCP:SL 14.2.7 (2026-10).

Save as `docs/specs/YYYY-MM-DD-<topic>-design.md` in the plugin's repository (the owner's
preferred location wins). Every section is required; "not applicable, because ..." is an answer,
an empty section is not. Tag every fact: **owner** (said it), **repo** (read it), **assumed**.

```markdown
# <Feature> design

Status: draft for review. Path: C. Date: YYYY-MM-DD.

## Context (server facts)
- Players: peak N; slots; dummies used?
- Servers: ports; folder (plugins/<port> or global); data shared? server id
- Framework: LabAPI <ver>; EXILED <ver or none>; plugin type chosen and why
- Neighbours: plugins touching the same events, hints, patches, roles, SSS; what each breaks
- Tickrate and idle: configured tickrate; idle mode on? empty hours; round length
- Existing systems reused: panel API, database, Discord, rank system (contract or link)

## Behaviour
What players and staff see and what happens, per trigger. Then the edge-case table:
| Case | Behaviour |
round restart / disconnect mid-action / spectators and Overwatch / dummies / staff /
late joiners / custom roles / abuse of the metric

## Lifecycle (what resets when)
| Event | What is kept | What is reset or flushed |
round restart (scene reload) / server restart / reconnect / config reload / Shutdown / crash
State owner: <class>. Reset at `WaitingForPlayers`. Flush on `ServerEvents.Shutdown` and on a timer.

## Data
What is stored, keyed by what (`UserId`; dummies and offline ids excluded), where, the contract
(owner's existing API first), sum vs max semantics, idempotency key and its reuse on retry,
cross-server concurrency.

## Cost budget
One cost line per component: events x frequency class (catalog) x players, per-frame work,
clock jobs and periods, allocations per run, network (messages and bytes per second), IO per
minute. Steady-state budget in ms per frame. Then the design note from designing-scpsl-plugins.

## Failure policy
External service: transient classes and bound, permanent classes and action, deferred vs dropped,
what players notice. False positives: report-only, shadow mode, fail open. Abuse: limits and
authorisation.

## Admin UX
Commands (type, name, permission), config keys and which are live-reloadable, kill switches,
status command, player toggles and where the preference lives.

## Test plan
Owner's answer to "local tests?": yes / no / they test themselves.
- yes: which instance (own private instance on port <p>, or the owner's port <p> on explicit
  request), scenarios, metrics and pass criteria (measuring-scpsl-performance)
- no: manual checklist the owner runs, with what to look for in the log
Scenarios: join, late join, round restart, disconnect, mass death, idle, external service down.

## Unverified assumptions
Each with how and when it gets verified.
```

## Self-review before showing the owner

1. Placeholders: no "TBD", no empty section, no "[VERIFY]" API name (look it up instead).
2. Every Context fact is used somewhere or marked irrelevant; no section contradicts another.
3. Every cost line has numbers or a named frequency class, estimates marked.
4. Every edge-case row has one behaviour, not two readings.
5. Scope fits one implementation plan; otherwise split it.

Then ask the owner to review the file. Only their yes permits writing-scpsl-plans.

## Re-verifying on a new build

- Restart, `WaitingForPlayers`, `Shutdown` and `Disable()` semantics: scpsl-game-lifecycle.
- Frequency classes in cost lines: regenerate the labapi-events catalog and diff.
- The design-note fields: designing-scpsl-plugins, design-checklist.md.
