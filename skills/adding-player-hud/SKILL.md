---
name: adding-player-hud
description: "Use when adding a persistent on-screen element to players of an SCP:SL LabAPI server: stats, round clock, cooldown, objective, status line or corner overlay, with or without a show/hide toggle, especially when other plugins already write hints or the HUD must stay cheap at 60 players."
---

# Adding a Player HUD

Verified against LabAPI 1.1.7 / SCP:SL 14.2.7 (2026-10).

## Overview

One owner calls `SendHint`; events only change data. A staggered visit sends on change or when a keep-alive is due; the client ticks clocks. Compiled, linted example: `examples/adding-player-hud` (`HudService.cs` core, `HudToggle.cs` SSS switch, `HudConfig.cs` kill switch, clamps and re-assert settings). Facts: scpsl-hints-and-hud.

## When to Use

- A HUD that stays on screen: stats, timer, cooldown, objective.
- Not: one hint after one event (path A, a guarded `SendHint`), an announcement (broadcast), settings-tab text (scpsl-server-specific-settings), stat storage (adding-per-player-tracking, adding-external-integrations).
- Path C: brainstorming-scpsl-features first.

## Ask First

LabAPI, EXILED or both? What else writes hints (refresh-all-on-spawn plugins, activity trackers, a HUD library)? Peak players, server count? What shows, how often does it change? Custom roles (team kills use `Faction`)? Toggle? Local test: ask, never push (running-a-test-server). Hints share one slot per player (verified on a real client), so there is exactly one owner; a HUD library is an owner like any other, not a requirement.

## Decisions

| Decision | Default | Change when |
|---|---|---|
| Slot owner | this plugin | another HUD plugin or library exists: ask, submit segments to it |
| Ticking value | `TimespanHintParameter.FromOffset` | value pauses: quantise, send on change |
| Cadence | visit 0.5 s, keep-alive 2 s, duration 3 s | duration must cover keep-alive + visit + margin |
| Recipients | `ReadyList`, skip `IsDummy`, staggered | never `Player.List` |
| Toggle | SSS two-buttons, default shown | hold-to-peek: keybind (adding-player-abilities) |
| Hidden player | send nothing, let it expire | an instant hide needs one empty hint, which also wipes other writers' hints |
| Other hint writers (refresh-on-spawn plugins) | assume yes: one re-assert after spawn bursts via the visits (`ReassertAfterSpawn`) | owner confirms none: off. Their text matters or two writers re-assert: shared compositor (hud-coexistence.md) |
| Placement, size | size % and optional align in config | numbers scale with the window height; at other aspect ratios only the area left of the container moves (layout table in scpsl-hints-and-hud) |
| Kill switch | `IsEnabled`, read at use time | always |

## Pattern

1. Config first: kill switch, cadence, clamps in `Normalize()`.
2. State by `UserId` in one owner class; never keep `Player`.
3. Events (`Death`, `Left`, `RoundStarted`) change data only. Check frequency in labapi-events.
4. One `SlClock.EveryStaggered` job: refill from `ReadyList`, one visit per player, try/catch each.
5. Visit: build text, then `changed || keepAlive`, then send. The keep-alive is never folded into the change check.
5b. Other hint writers: `RoundStarted` and `ChangedRole` only arm one deadline; the first visit after it bumps an epoch and every player is re-sent once at his own visit. Skip connections in `HintDisplay.SuppressedReceivers` without recording the send. No coroutine, no send from the handler (hud-coexistence.md).
6. Ticking numbers go in parameters; recompute age from your own start tick on each resend.
7. Toggle: append to `DefinedSettings`, namespaced id, handler in try/catch.
8. Lifecycle: constant metadata, idempotent `Enable`, reset at `WaitingForPlayers`, clean up at `Shutdown`.
9. Verify on a real client (testing-scpsl-plugins; checklist in hud-verification.md); report what could not be checked.

## Key Parts of the Example

```csharp
bool changed   = !string.Equals(text, hud.Sent, StringComparison.Ordinal);
bool keepAlive = now - hud.SentAt >= (long)(cfg.KeepAliveSeconds * Stopwatch.Frequency);
if (!changed && !keepAlive) return;   // text holds "{0}", so it stays equal while the clock ticks
double age = (now - _roundStart) / (double)Stopwatch.Frequency;
var clock = TimespanHintParameter.FromOffset(-age, @"h\:mm\:ss", negate: true);
p.SendHint(text, new HintParameter[] { clock }, null, cfg.HintDurationSeconds);
```

`_hidden` survives `WaitingForPlayers`: it is a preference, not round state. `HudToggle.Install` merges into `DefinedSettings` and never replaces it.

## Common Mistakes

| Wrong | Right |
|---|---|
| HUD library chosen because "hints share one slot" | The slot is shared (verified), but a library is not the fix: one owner composing one string plus a keep-alive is. |
| Ignored the refresh-all plugin and 60 players | Ask; send on change, staggered; no refresh from events. |
| Refresh-all plugin only listed as a "test item" | Design the re-assert in: deadline + epoch via the visits, keep-alive as backstop. Replace is verified: their hint covers the HUD until its next send. |
| Re-asserting from a `ChangedRole` handler or a coroutine per spawn | Handlers arm a deadline only; 60 role events in a frame must cost 60 comparisons. |
| Keep-alives "naturally spread" by join time | A restart starts every session together: `EveryStaggered`. |
| Session time resent as text every 2 s | `TimespanHintParameter`, age from your start tick. |
| `<align=right>` corner, tags left open | Right alignment ends at the 1200-unit container edge, 360 px short of the screen edge (1080p, 16:9); close every tag; whitelist config strings. |
| One try/catch around the whole player loop | One per player, rate-limited log. |
| Empty hint to hide the HUD | Send nothing. An empty hint clears the slot, so it also wipes other writers' hints. |
| Toggle assumed persisted | Client saves it; server learns on the first message (unverified). Keep a server-side set. |
| `DefinedSettings = mine` | Append, or use the shared registry. |

## Red Flags

- `SendHint` in an event handler or a "refresh everyone" helper.
- No keep-alive, or a change check that swallows it.
- Pixel placement promised for a setup other than 1920x1080, 16:9.

## Links

hud-coexistence.md (foreign writers, compositor choice), scpsl-hints-and-hud (hud-compositor.md, hint-parameters.md), scpsl-server-specific-settings, adding-player-abilities, scpsl-main-thread-and-timing, scpsl-game-lifecycle, labapi-events, testing-scpsl-plugins, verifying-scpsl-work.
