# HUD coexistence: another plugin also writes hints

Verified against LabAPI 1.1.7 / SCP:SL 14.2.7 (2026-10).

Case: the server runs a plugin that re-sends hints to everyone on every spawn and role change. The client has one hint slot per player and the newest hint replaces the current one (verified on a real client), so your HUD disappears after each of those events and only the keep-alive brings it back. The example (`HudService.cs`, `HudConfig.cs`) implements the pattern below.

## What is known

| Fact | Status |
|---|---|
| The server arbitrates nothing: two hints to one player are two messages, sent in call order | verified |
| No LabAPI event exists for hints, so another plugin's hint writes cannot be observed or intercepted without Harmony | verified |
| The game suppresses hints for one connection while it re-creates pickups after an escape or role change; a hint sent in that window is silently dropped. `HintDisplay.SuppressedReceivers` is a public static set owned by the game, main thread only | verified |
| `ChangedRole` is the last signal of a role change (after `Spawned` and the loadout). It also fires for spawn waves, and for the Spectator step of a death, before `Death` | verified (static trace) |
| At `RoundStarted` roles are still `None`; they are assigned later in the same frame, one `ChangedRole` per player | derived |
| A late joiner's `ChangedRole` can come before `Joined` | verified (static trace) |
| All LabAPI plugins are loaded before any is enabled, and the loader exposes the list | verified (source) |
| A refresh-all-on-event plugin costs N x N hints in one frame (about 20 000) | verified in a trace, cost estimated |
| The client has one hint slot per player: a new hint replaces the current one at once, no stack, no queue; an empty hint clears it | verified (real client, 14.2.7) |
| An identical resend every 0.5 s (3 s duration) does not flicker or pulse, with or without `AlphaEffect(1, 0, 1)`; a 2 s keep-alive period was not observed | verified (real client) for 0.5 s |
| How long the suppression window lasts; when the other plugin sends relative to the event; whether it repeats | **unverified** (client test, other plugin's code) |
| How two hint writers interact beyond the replace rule | **unverified** |

## The pattern

1. **Assume, do not detect.** Nothing reports who else writes hints. Ask the owner (Ask First in SKILL.md). The example also logs the names of the other loaded plugins at enable time, so the owner can say which of them send hints. That list holds names only, and plugins from other frameworks may be missing. Config flag `ReassertAfterSpawn` (default on) records the assumption; turn it off only when the owner confirms there is no other writer.
2. **Events arm one deadline, the existing visits do the sending.** `RoundStarted` and `ChangedRole` only set `_reassertAt` (first event of a burst; later events do not move it, so latency stays bounded). The first visit after the deadline bumps a counter `_epoch`; each player's own staggered visit then sees `hud.Epoch != _epoch` and sends once. No coroutine per event or per player, no send from a handler, and 60 `ChangedRole` events in one frame cost 60 comparisons.
3. **Re-assert for everyone, not only for the player who spawned.** A refresh-all plugin overwrites all players when anyone spawns, so a dirty flag on the spawned player alone would miss the rest.
4. **Skip suppressed connections without recording the send.** In the visit: `if (HintDisplay.SuppressedReceivers.Contains(p.Connection)) return;` placed after the decision to send and before `Sent`/`SentAt`/`Epoch` are stored. The send would be dropped anyway; not recording it means the next visit tries again instead of waiting for the keep-alive.
5. **Keep-alive stays the backstop.** It repairs overwrites that no event predicted (vanilla pickup warnings, a plugin on a timer).

```csharp
bool reassert = hud.Epoch != _epoch;                 // armed by events, cleared per player at his visit
if (!changed && !keepAlive && !reassert) return;
if (HintDisplay.SuppressedReceivers.Contains(p.Connection)) return;   // retry at the next visit
hud.Sent = text; hud.SentAt = now; hud.Epoch = _epoch;
```

## Numbers (defaults)

| Setting | Default | Reason |
|---|---|---|
| `HintDurationSeconds` | 3 | client lifetime; the server keeps no timer |
| `KeepAliveSeconds` | 2 | clamped to duration - visit period - 0.25 s, so the HUD does not lapse between sends |
| `RefreshSeconds` (visit period) | 0.5 | one visit per player per period |
| `ReassertDelaySeconds` | 0.5 | policy value: later than the other plugin's own refresh and the suppression window (both lengths unverified; the replace rule itself is verified). Clamped to the keep-alive, since a later re-assert adds nothing |

- Gap after an overwrite caused by an event: delay + up to one visit period, about 1 s with the defaults. After an overwrite no event predicted: keep-alive + visit period, about 2.5 s.
- Cost of the re-assert: one extra hint per player per burst, spread over one visit period by the stagger. At 60 players that is 60 hints, about 0.12-0.24 ms in total (estimate from an unmeasured 2-4 us per hint). Compare the other plugin's N x N.
- A shorter keep-alive is not a substitute: the steady rate is players / keep-alive (60 hints/s at 1 s) all round long, to fix a gap that only exists after events.
- Tune the delay on a client: the right value is "just after the other plugin's hint arrives". If the other plugin sends later than ours, the keep-alive still repairs it, only later.

## The price of winning

The client replaces (one slot, verified), so every re-assert erases the other plugin's hint until it sends again, and every send of theirs erases ours. Two writers that both re-assert alternate and flicker. So the re-assert is a stopgap for a writer whose hints are brief or less important than the HUD. When their content matters to players, use a shared compositor.

## When to prefer a shared compositor

| Situation | Choose |
|---|---|
| The other plugin's text matters to players (hints replace each other, verified) | Compositor: one string per player, both contribute segments |
| Both texts must be visible at once | Compositor (segments in one string). Re-asserting can only alternate them |
| Three or more writers, or the owner keeps adding plugins | Compositor |
| The owner can change the other plugin, or it already has a segment or hook API | Compositor: it submits text and stops calling `SendHint` for the same players |
| The other plugin is closed, unchangeable, or its hints are short notices | Re-assert (this pattern), and ask its author for a coalesced refresh |
| Nothing else writes hints | Neither: leave `ReassertAfterSpawn` off |

A compositor does not fix the other plugin's N x N storm by itself; that remains its author's fix (one coalesced refresh with a per-player dirty flag). Compositor sketch: scpsl-hints-and-hud (hud-compositor.md). Third-party HUD libraries are not covered here: read their documentation, make the library the owner and feed it segments, and never run two owners.

## Client test (extends hud-verification.md)

1. With the other plugin enabled, spawn a few players and change roles. Does our HUD vanish? For how long? Does it return about 1 s later (re-assert) or only after about 2.5 s (keep-alive)?
2. How long is the other plugin's hint on screen before the re-assert replaces it, and how often does it send? (The replace rule is settled; this measures the timing for this pair.)
3. With `ReassertAfterSpawn` off, repeat step 1: this is the baseline.
4. Try `ReassertDelaySeconds` 0 and 1. Pick the smallest value that lands after the other plugin's hint.
5. Pick up items until the inventory is full right after a role change: is a send inside the suppression window dropped, and does the next visit recover it?

Record what was settled and update the "unverified" rows above.

## Re-verifying on a new build

- Re-check that `HintDisplay.SuppressedReceivers` still exists as a public static set, is still read on the main thread only, and still drops a hint sent to a suppressed connection (it is the basis of step 4 of the pattern).
- Re-check that `ChangedRole` is still the last signal of a role change (after `Spawned` and the loadout), still fires for spawn waves and for the Spectator step of a death, and still comes before `Joined` for a late joiner. Check that `RoundStarted` still sees roles as `None` (scpsl-game-lifecycle).
- Re-check that LabAPI still has no hint event and that the plugin list the loader exposes is still filled before any plugin is enabled.
- The rows marked unverified stay unverified until a client test settles them: the length of the suppression window and the other plugin's send timing. The replace rule and the quiet identical resend at 0.5 s are verified on client build 14.2.7; re-check them on a new build. When a test settles a row, change it above and the "Numbers" defaults that depend on it (`ReassertDelaySeconds`).
- Re-run the client test above after any change to hint handling in the game or in another plugin on the server.
