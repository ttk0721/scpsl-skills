---
name: scpsl-hints-and-hud
description: "Use when a LabAPI or EXILED SCP:SL plugin shows hints, broadcasts or a HUD (SendHint, SendBroadcast, HintParameter, rich text), when hints flicker, vanish, overwrite each other, spike lag at round start or spawn, throw \"Cannot display a hint to the local player\", or when one HUD must serve 60 players cheaply."
---

# SCP:SL Hints, Broadcasts and HUD

Verified against LabAPI 1.1.7 / SCP:SL 14.2.7 (2026-10).

## Overview

A hint is one reliable message per recipient, built the moment you call it. The server has no queue, dedupe or memory of what a player sees. A cheap HUD has one owner, sends on change plus a keep-alive, and lets the client tick numbers.

## When to Use

- Writing or reviewing any `SendHint`, `Server.SendBroadcast`, HUD, timer or counter.
- HUD flickers, vanishes after pickups, is overwritten, or round start spikes.
- Not for settings-tab UI (scpsl-server-specific-settings) or scheduling (scpsl-main-thread-and-timing).

## Quick Reference

| Fact | Status |
|---|---|
| One reliable-ordered message per recipient, about 20 + n bytes | verified (derived) |
| `Show` throws for the host; `ReadyList` excludes host, includes dummies | verified |
| Text over 65534 UTF-8 bytes or a lone surrogate throws inside `SendHint` | verified |
| Only `TimespanHintParameter` and `AnimationCurveHintParameter` stay live | server side verified; client verified for the Timespan count-up (one send), **unverified** for the curve |
| `AnimationCurve.Linear` as a ramp | **refuted**: eases to 37.5 % at midpoint |
| One hint slot per player: a new hint replaces the old, no stack, no queue; an empty hint clears | verified (real client) |
| Identical resend every 0.5 s (3 s duration): no flicker, with or without `AlphaEffect`; other periods untested | verified (real client) |
| Tags `b i u color size align pos voffset`, `line-height=0`, positive `margin-left`/`margin-right` and `noparse` render; lowercase is drawn as small caps | verified (real client) |
| Layout: centred 1200-unit container, 1 unit = window height / 1080; `voffset` absolute only with anchor lines; cap top = `697 - voffset - 0.70 x size` at 1080p | verified (real client) at 1920x1080; the scaling rule also at 1280x720, 4:3, 16:10 and 21:9 |
| Other aspect ratios change only the left margin: left screen edge at `pos = -(1080 x aspect - 1200) / 2`; the server knows each player's ratio (`ReferenceHub.aspectRatioSync.AspectRatio`) | verified (real client; the ratio field server side) |
| `ClearBroadcasts` wipes vanilla and other plugins' broadcasts | verified |

## Rules

1. One HUD owner per server composes everything into one string; others submit segments. There is one slot per player, so a second writer, vanilla included, replaces the HUD until your next send.
2. Send on change, at most 1-2 Hz per player (policy, not a measured limit), plus a **keep-alive resend** before expiry that the change check must not suppress. Use duration 3 s, refresh 2 s. An identical resend every 0.5 s did not flicker, so the keep-alive should be quiet (the 2 s period itself is untested); it bounds the gap after a foreign hint.
3. Ticking values go in `TimespanHintParameter.FromOffset(...)`, never in resent text. Never use its `DateTimeOffset` constructor.
4. Ramps: `new Keyframe(t, v, slope, slope)`, 2..257 keys, curve at least as long as the hint, keep the original offset on resend. Details: hint-parameters.md.
5. Stagger explicitly (`SlClock.EveryStaggered`): a restart or mass join starts every session in one frame.
6. Events update data and never send. No "RefreshAll" per spawn, role change or death.
7. Iterate `Player.ReadyList`, skip `IsDummy`, try/catch each send, main thread only; pass `""`, not `null`.
8. Close every rich-text tag you open. Replace `{` and `}` in player-controlled text.
9. Prefer letting a short hint expire to sending an empty one. An empty hint clears the slot, other plugins' hints included; send one only to hide your own HUD at once.
10. Broadcasts: rare announcements only.

```csharp
// Ticks on the client; recompute age from YOUR start time on each keep-alive.
var t = TimespanHintParameter.FromOffset(-ageSeconds, @"h\:mm\:ss", negate: true);
p.SendHint("Session {0}", new HintParameter[] { t }, null, 3f);
```

Compositor and anchor-line recipe: hud-compositor.md. Parameters, curve traps: hint-parameters.md. Limits, rich text, layout table, broadcasts: hint-limits-and-broadcasts.md.

## Common Mistakes

| Wrong | Right |
|---|---|
| Assumed bottom-centre placement, a `<align=right>` corner at the screen edge, or a flicker on identical resends; left `<align>`/`<size>` open | Default hint is centred, about 146 px below screen centre. `align` and `pos` act inside a 1200-unit centred container (right alignment ends 360 px short of the screen edge at 1080p, 16:9). Identical resends do not flicker. Close tags (`Misc.CloseAllRichTextTags` is a net); keep layout optional, and remember the area left of the container moves with the aspect ratio. |
| Positioned lines with `voffset` alone, then the layout moved when another line changed | Wrap the composed string in the two anchor lines (hud-compositor.md); then y = 688 - voffset at 1080p. |
| Assumed keep-alives are staggered by join time; resent text every 2 s for a session counter | After restart all 60 fire together (about 0.2 ms burst, est.); stagger on purpose. Count-up from join is a `TimespanHintParameter`; active time minus AFK is not (use minute resolution). |
| Resent HP to everyone every second, believing skipping needs a longer duration | Send on change plus 2 s keep-alive under a 3 s duration. 60 hints/s is waste, not a defect. |
| `foreach (var p in Player.List) p.SendHint(...)` | Throws on the host. Use `ReadyList`, skip dummies. |
| Used `AnimationCurve.Linear` for a bar | Explicit equal in/out tangents. |
| `ClearBroadcasts` to remove "my" message | It wipes warhead and other plugins' too; no per-message removal exists. |

## Red Flags

- `SendHint` in an event handler, `Update` or a per-frame loop (scpsl-lint SL012).
- A hint loop with no change check, or one whose check suppresses the keep-alive.
- Two plugins hinting the same players.
- An empty hint used as "clear" for a HUD that other plugins share.
- Unsanitised nicknames in hint text.
- Elements placed left of the container (negative `pos`) as if every screen were 16:9: at 4:3 they can leave the screen, at 21:9 they stand far from the edge. Adapt them per player or state the limit plainly.
