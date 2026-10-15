# HUD recipe: coexistence, limits and the client test

Verified against LabAPI 1.1.7 / SCP:SL 14.2.7 (2026-10).

The example compiles and lints clean. The example plugin itself has **not** been run against a game client. The client behaviour it relies on (one hint slot per player, a quiet identical resend at 0.5 s, the count-up parameter, the tags and the layout numbers) is verified on a real client (SCP:SL 14.2.7; details and the scaling rule for other window sizes in scpsl-hints-and-hud). Every point below marked unverified needs the test at the end.

## Coexisting with other hint writers

| Situation | What you can know | What to do |
|---|---|---|
| Another plugin calls `SendHint` for the same players | The server has no arbitration: both messages go out in call order (verified). The client has one slot and the newer hint replaces the older (verified, client). | Ask the owner. Prefer one owner: the other plugin submits text to yours, or yours to its. |
| A plugin refreshes every player on spawn, role change or death | Its cost is N x N hints in one frame. It does cover your HUD until your next send (one slot, verified client); how long and how often is unverified. | Do not copy it. Suggest a coalesced refresh; the fix belongs to that plugin. Re-assert once after the burst through the visits, and choose a compositor when its text matters: hud-coexistence.md. |
| A HUD library is installed | Its API is not covered by this library. | Make it the owner and feed it segments. Do not run two owners. |
| Vanilla item warnings (full inventory, pickups) | They use the same hint message (verified). | The keep-alive is the repair. Do not lengthen the duration instead. |
| A hint sent while a player's pickups are re-created (escape, role change) | The game suppresses hints for that connection for a moment (verified). | The next keep-alive covers it. |
| EXILED installed too | Its `CurrentHint` patch adds a coroutine per hint (estimated 0.5-1.5 us). | Hint volume matters more there; keep the change check. |

Never intercept another plugin's hints: no LabAPI event exists for them.

## Positioning and size

- Placement (verified, client, 1920x1080, 16:9): the default hint is centred, its block about 146 px below the screen centre. `<align>`, `<pos>`, `<size>` and `<voffset>` render in hints and act inside a centred 1200-unit container (x 360..1560). `align=left` starts at x 360, `align=right` ends at x 1560, and the right screen edge cannot be reached without wrapping, so a "right corner" is the container edge. A negative `pos` (`pos=-360`) reaches the left screen edge. Table: scpsl-hints-and-hud (hint-limits-and-broadcasts.md). Everything scales with the window height; at other aspect ratios only the area left of the container moves (same file, "Other window sizes"), and the right "corner" is the container edge on every screen. The example sets no `voffset`, so its HUD keeps the default vertical position near the screen centre and `Align` only moves it sideways; a top or bottom position needs the anchor-line recipe (hud-compositor.md).
- Close every tag you open inside the method that opens it. `Misc.CloseAllRichTextTags` is a safety net for text you did not write.
- Config strings that end up in rich text go through a whitelist (see `AlignTag` in `HudService.cs`).
- Size: a hint string must stay under 65534 UTF-8 bytes or `SendHint` throws. Aim for a few hundred bytes. That is prudence: a 15-line hint rendered without clipping, and the client's maximum length is unverified.
- Player-controlled text (nicknames) is not brace-safe and may hold a split surrogate: replace `{`, `}` and truncate on a character boundary (`Safe` in hud-compositor.md). The example shows none.

## Cost at 60 players (estimates, unmeasured)

Visit period 0.5 s means 120 text builds/s. A send happens only when the text changed or after 2 s, so a stable HUD is about 30 hints/s, about 0.06-0.12 ms/s of main-thread time. Measure with `SlClock.Stats` (the job is named `adding-player-hud.visit`) before quoting a number.

## Client test (ask the owner first)

Local tests are optional. Ask whether they want one and whether to use one of their ports or a separate instance on an unused port (running-a-test-server). Then on a real client:

1. The HUD appears, the clock ticks without resends (the count-up from one send is verified), 00:00 does not reappear on keep-alives (a negative offset starting at the right age is derived, not yet seen), and the HUD does not blink on the 2 s keep-alive (only a 0.5 s identical resend has been observed).
2. Pick up items until the inventory-full warning shows: does the HUD return within 2-3 s? (The warning replaces it; the keep-alive is the repair.)
3. Flip the toggle: does the HUD vanish within 3 s, and stay hidden after a round restart?
4. Change `Align` and `TextSizePercent`: does it sit where the measured layout predicts (left edge x 360, right edge x 1560 at 1080p, 16:9), does a tag leak into other text? Repeat on any other resolution or aspect ratio you care about.
5. Run with the other hint plugins enabled: the replace rule is settled, so look for how long the HUD is covered and whether the two writers alternate. Spawn and change roles with and without `ReassertAfterSpawn`; the full steps are in hud-coexistence.md.

Write down what was settled and what was not.

## Measuring hint placement from screenshots or a recording

- **A screen recording beats per-step screenshots.** Steps can then be a few seconds long; cut frames afterwards (`ffmpeg -ss <s> -i rec.mp4 -frames:v 1 f.png`, contact sheets with `-vf "fps=15,crop=...,scale=480:-1,tile=5x6"`). A recording's file name gives its start only roughly, so put a label on screen for every step (a broadcast or a hint) and match frames by label, not by clock. Screenshots need 20 to 40 s per step with an on-screen cue to take them.
- **The crosshair dot is the screen centre.** A windowed shot carries a title bar and an arbitrary crop, and a recording may scale the window. The 2x2 crosshair dot sits at (959.5, 539.5) at 1080p, so it gives the offset and, with the picture edges, the scale. Search for it only close to the expected centre, or HUD text near the centre is taken for the dot.
- **Measure by colour, with tolerance.** Give each measured element its own colour. Thin strokes such as `|` are antialiased and fail an exact-colour match; test hue or greyness instead. A `|` at size 30 has about 3 px of side bearing, so a bar at `pos=P` shows its stroke at `P + 3`.
- **Cap top is the first dense row.** Accents above capitals are sparse rows: take the first row holding at least about a third of the element's fullest row, not the first coloured pixel.
- **A ruler gives the scale.** Size-24 labels at known `voffset`s (for example every 50 units from 300 to -300) give the vertical scale in every shot; their middles sit at `688 - voffset` at 1080p.
- **Compare a layout preview in the same units.** Convert the preview's element boxes and the measured game positions to 1920x1080 units and judge the vertical and horizontal difference per element against a stated tolerance.

## Re-verifying on a new build

- Re-check that `HintDisplay.Show` is stateless, still throws for the host, and still skips connections in `HintDisplay.SuppressedReceivers` (the visit's guard relies on it).
- Re-check `TimespanHintParameter.FromOffset` and that it stays the only count-up path.
- Compile the example against the new game assemblies: `SSTwoButtonsSetting`, `ServerOnSettingValueReceived`, `SendToPlayer`.
- Re-run the client test and update every "unverified" mark it settles, and re-measure the layout numbers on a new build.
