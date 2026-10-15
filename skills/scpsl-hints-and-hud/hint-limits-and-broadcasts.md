# Hint mechanics, size and rich-text limits, broadcasts

Verified against LabAPI 1.1.7 / SCP:SL 14.2.7 (2026-10).

The dedicated-server build has the client-side hint renderer stripped, so client behaviour cannot be read from server code. What a client DOES with a hint was measured on a real client (SCP:SL 14.2.7; the layout at 1920x1080, the scaling rule at the window sizes listed under "Other window sizes") and is marked **verified (client)**. Anything not measured there stays **unverified**. What the server SENDS is verified from the server-side code.

## How a hint travels (verified server side)

1. `Player.SendHint(text, duration)` builds a `TextHint` and calls `ReferenceHub.hints.Show(hint)` (`HintDisplay.Show`). The simple overload allocates a dummy `StringHintParameter` plus a one-element array on every call. LabAPI always supplies at least one parameter; why is unverified, copy the behaviour. EXILED does the same.
2. `HintDisplay.Show` throws `ArgumentNullException` for a null hint and **`InvalidOperationException` for the host (local) player**. Then, if the connection is not in the static `HintDisplay.SuppressedReceivers` set, it sends one `HintMessage`. There is no readiness check, no rate limit, no dedupe, no queue, no priority and no per-player state.
3. The message is serialised at the moment you call it, on your thread. Wire size is about **20 + UTF-8 text bytes** for the common `SendHint("text", 3f)` shape (derived from the serialisers, not captured), plus 1-2 bytes of framing. Cost scales with (hints sent) x (recipients), not with frame rate.
4. Messages go on channel 0, `ReliableOrdered`. A hint flood on a lossy link queues behind retransmissions and can head-of-line-block every other reliable message to that player. The mechanism comes from the channel config; real impact is unverified.
5. Mirror batches per connection and flushes once per send interval, so five hints to one player in one frame are five full messages in the same datagram(s), not one.
6. Dummies: `Send` on a dummy connection is a no-op, but the message is serialised first. Hints to dummies cost CPU and deliver nothing. `Player.ReadyList` includes dummies and excludes the host.
7. Suppression: the game adds a connection to `SuppressedReceivers` briefly while it re-spawns pickups after an escape or role change, and hints sent in that window are silently discarded.
8. Clearing: there is no clear API. The idiom `SendHint("", 0.1f)` costs a full message and works: an empty hint clears the slot, even one holding a hint sent with a 99999 s duration, and the cleared hint does not return when the empty one expires (verified, client). It also wipes whatever another plugin showed (one slot, below). Pass `""`, not `null` (LabAPI does not guard null; writing a null string produces length 0 and does not throw on the server, but a null text on the client is untested).

## Replacement semantics with several plugins (verified on a real client)

- Server: nothing arbitrates. Both messages go out in call order.
- Client: **one hint slot per player.** A new hint replaces the current one at once, even when the current one has time left: this holds wherever the two hints are placed on screen, and an empty hint also replaces a hint sent with a 99999 s duration. There is no stacking and no queue, and a replaced hint does not return when the newer one expires. An empty hint clears the slot.
- Identical resend: resending the same text every 0.5 s (3 s duration) does not flicker or pulse, with or without an `AlphaEffect(1, 0, 1)` attached. A keep-alive resend should be just as quiet, but a longer period (for example 2 s, late in the hint's life) is unverified.
- Vanilla competes: item-search completors send translation hints for full inventory or category, and the SCP-330, armour, ammo and SCP-2536 gift code send hints too. Because there is one slot, any such hint, and any other plugin's one-shot hint, should cover a plugin HUD until the HUD's next send (follows from the slot; not tested with a vanilla or third-party sender).
- Therefore: one compositor that puts everything into one string, and keep-alive resends, which bound the gap after someone else's hint (hud-compositor.md).
- Not measured: how two plugins' hints interact beyond the replace rule (for example a plugin that re-sends on its own timer).

## Duration

The scalar defaults to 3 (`durationScalar`). LabAPI documents seconds and vanilla uses 3 s, 2 s and 1.5 s. A hint sent with 99999 stays on screen until the next hint replaces it (verified, client, over a few seconds); exact expiry timing for short values is unverified. The server keeps no timer; expiry is client-side.

## Size limits

- A string is written as UTF-8 with invalid-byte checking on. More than **65534 bytes** throws `IndexOutOfRangeException` from inside `SendHint`/`SendBroadcast`, and nothing catches it. A lone UTF-16 surrogate also throws.
- A hint of 15 lines is drawn in full without clipping (verified, client). The maximum length and larger line counts are **unverified**. Keep hints to a few hundred bytes. That is prudence, not a measured cap.
- Truncate on a character boundary (do not cut between the two halves of a surrogate pair). Even nicknames are not guaranteed encodable: both of the game's truncation paths cut at 48 UTF-16 units and can leave a lone surrogate (derived from the code, not reproduced).
- Wrap each send in try/catch so one bad string does not abort the loop over players.

## Rich text and layout

- The client UI is TextMeshPro. Hints render `<b>`, `<i>`, `<u>`, `<color>`, `<size=N>`, `<size=N%>`, `<align>`, `<pos>`, `<voffset>`, `<line-height=0>`, positive `<margin-left>`/`<margin-right>` and `<noparse>` (verified, client). Lowercase letters are drawn as small capitals. Server Specific Settings text areas also render `<rotate>`, `<link>` and `<mark>`; that hints do, and the rest of the tag list below, is **unverified**.
- `<noparse>` holds in hints (verified, client): `<noparse><b>X</b></noparse>` prints the tags as text, and a fullwidth less-than (U+FF1C) inside it cannot end it, so `<noparse>A＜/noparse><color=#ff0000>R</noparse>` prints as plain text with no red. The hint font has glyphs for the fullwidth braces (U+FF5B, U+FF5D), the fullwidth less-than (U+FF1C), U+FFFD and the single angle quotes (U+2039, U+203A), so they are safe stand-ins in escaped player text.
- The game's own tag list (used to auto-close tags) is: align, allcaps, alpha, b, color, cspace, font, font-weight, gradient, i, indent, line-height, line-indent, link, lowercase, margin, mark, mspace, nobr, noparse, page, pos, rotate, s, size, smallcaps, space, sprite, style, sub, sup, u, uppercase, voffset, width.
- `Misc.CloseAllRichTextTags(string)` appends missing closing tags. `Misc.SanitizeRichText(string)` strips `<` and `>`.
- Always close the tags you open, inside the same segment. An unclosed `<align>` or `<size>` leaks into the next segment or into text another plugin appends.
- Placement is measured, not assumed: the table below. It holds at 16:9 for any window size (everything scales with the window height). Other aspect ratios move the screen edges against the container, see "Other window sizes", so keep layout configurable and treat the area left of the container as aspect-dependent.

### Layout (verified, client: 1920x1080 window, 16:9)

| Property | Measured |
|---|---|
| Default hint, no tags | Centred horizontally. The text block is centred vertically about 146 px below the screen centre. |
| Container | 1200 units wide, centred. At 1080p one unit is one pixel, so it spans x 360..1560 (an 18.75 % inset on each side at 16:9). |
| `align=left`, `pos=0%` | Left edge of the container. |
| `align=right` | Right edge of the container. |
| `pos` | A percentage of the container width (`pos=25%` is 300 units; 50 % is the screen centre) or plain units from the container's left edge. `voffset` uses the same units. |
| Overflow | Text running past the container's right edge wraps to the start of the line. `<nobr>` does not stop it. |
| Left of the container | A negative `pos` goes left of the container: `pos=-360` is the left screen edge. Negative `margin`, `margin-left` and `margin-right` change nothing. |
| Right screen edge | Not reachable without wrapping. Right-aligned text ends at the container edge, 360 px short of the screen edge. |
| Centred at another x | `align=center` plus a positive margin moves the centre, to the pixel: for a centre at container unit `c`, `margin-left = 2c - 1200` when `c > 600`, `margin-right = 1200 - 2c` when `c < 600` (close with `</margin>`). |
| Right end before the container edge | `align=right` plus `margin-right = M` ends the text at container unit `1200 - M`, to the pixel. |
| Cap top and baseline (anchors, `line-height=0`) | The baseline sits 9 units below `688 - voffset`; capitals are 0.70 of the size tall, so cap top = `697 - voffset - 0.70 x size` px at 1080p. Exact for sizes 20 to 80; beyond that range it is an extrapolation. The middle of a size-24 line's digits sits at `688 - voffset`. |
| Glyph side bearings | A `|` at size 30 has about 3 px on each side, so a bar drawn at `pos=P` shows its stroke at `P + 3`. Account for it when lining up markers. |
| `voffset`, no anchors | The position depends on the rest of the hint: the same `voffset=0` text can move by about 60 px when the other lines of the hint differ. Do not rely on it. |
| `voffset`, with anchors | Absolute: y = 688 - voffset (px from the top edge at 1080p), so `voffset=0` sits about 148 px below the screen centre. Visible range about +688 (top) to -392 (bottom). Recipe: hud-compositor.md. |
| Other game UI | C.A.S.S.I.E. captions and the HP bar are drawn independently of the hint, in the same screen regions, so they can overlap it. The "your role" intro screen after a spawn or role change is drawn over the hint for about 4 s, across roughly 10..20 % of the screen height; the hint is intact underneath once it fades. |

### Other window sizes and aspect ratios (verified, client)

Verified at 1920x1080, 1280x720, 1440x1080 (4:3), 1680x1050 (16:10) and 2560x1080 (21:9), to 1..2 px:

- The hint unit is the window height / 1080, horizontally and vertically. The vertical layout (`voffset`, cap top, the 688 offset) does not depend on the aspect ratio.
- The container is always 1200 units and centred. The screen is `1080 x aspect` units wide, so the left screen edge is at `pos = -(1080 x aspect - 1200) / 2`: -360 at 16:9, -264 at 16:10, -120 at 4:3, -680 at 21:9.
- Consequence: text inside the container looks the same relative to the screen centre on every screen. Text left of the container depends on the aspect ratio: at 4:3 anything left of `pos=-120` is off screen (in 16:9 terms, left of x = 12.5 %), at 16:10 left of x = 5 %, and at 21:9 `pos=-360` stands 320 px in from the screen edge. The right margin outside the container is unusable at every ratio (text wraps at the container edge).
- 16:9 at another size (1280x720) is the 1080p layout scaled by 2/3.
- Not measured: windows narrower than 1200/1080 (about 10:9), where the container would be wider than the screen.

The server knows each player's aspect ratio without asking: the client reports `Screen.width / Screen.height` through `AspectRatioSync` (a command the game uses for SCP vision checks), and the server keeps it in `ReferenceHub.aspectRatioSync.AspectRatio`, publicly readable. Values below 1.0 are clamped to 1.0, and the field reads 1.0 until the client's first report, so treat 1.0 as "unknown" (verified server side; whether the value follows a live window resize is unverified). A HUD can therefore adapt the left margin per player, for example by mapping a position `x` in the 16:9 left margin (percent of a 16:9 screen, 0 to 18.75) proportionally onto the player's own margin: `pos = L(a) x (1 - x / 18.75)`, with `L(a)` the left-edge formula above. At 16:9 this equals the plain `pos = 19.2 x - 360`, and nothing from the margin lands inside the container. Per-player adaptation on a client is **unverified**.

## Braces in player-controlled text

Parameters are interpolated into the text on the client (`"Test {0} {1}"`). LabAPI always attaches at least one parameter, so that path is always in play. Nicknames keep braces: the game's nickname cleaner only alters `<` and `>` (to fullwidth look-alikes), `[`, `]` and the 48-unit length. What the client does with a stray `{`, `}` or `{5}` with no parameter (exception? literal?) is **unverified**.

Cheap insurance: replace `{` and `}` in anything player-controlled (nicknames, custom info, report text) with the fullwidth characters U+FF5B and U+FF5D. Doubling the braces is the usual `string.Format` convention but is not verified for the client. A hint with only your own static text and your own parameters needs no escaping.

## Broadcasts

- A broadcast is a Mirror RPC on the host's `Broadcast` component: the text, a `ushort` number of seconds and a flags byte. Flags: `Normal = 0`, `Truncated = 1` (assigned from a parameter that means monospaced text), `AdminChat = 2` (semantics unverified; LabAPI sends admin chat through an encrypted channel now).
- LabAPI: `Server.SendBroadcast(string, ushort duration, Broadcast.BroadcastFlags type = Normal, bool shouldClearPrevious = false)`, a per-player overload, `Server.ClearBroadcasts()` / `ClearBroadcasts(Player)`, and `Player.SendBroadcast` / `Player.ClearBroadcasts`.
- Cost: one RPC message to everyone is still **N serialisations** (the pre-packed writer is unused). The channel is reliable ordered. A global broadcast is one call but not one pack.
- Queueing, stacking, maximum lines and truncation are client-side and **unverified**. The RPC names (`AddElement`, `ClearElements`) imply a list on the client. The server keeps nothing and gives no handle to remove one broadcast.
- **`ClearBroadcasts` and `shouldClearPrevious: true` wipe everything**, including vanilla warhead, decontamination, friendly-fire, ban/kick and remote-admin broadcasts and other plugins' broadcasts. Clear only if you intend that.
- `Player.SendBroadcast` to the host logs a Mirror `LogError` (the host's connection is not a client connection). Skip `IsHost`. Whether it is null or merely the wrong type on a dedicated host is unverified.
- Use broadcasts for rare announcements. A HUD belongs in the hint compositor.

## Cost table (estimates, unmeasured)

| Operation | Main thread | Network |
|---|---|---|
| `SendHint`, one recipient | about 2-4 us, 3 small objects + an array, pooled writer | 20 + n bytes, reliable ordered |
| Same text to P players | P x the above (each call serialises separately) | P x |
| 60 hints/s (1 Hz, 60 players) | about 0.12-0.24 ms/s | about 60 x (20 + n) B/s |
| Per-frame hint at 60 TPS, 100 players | 6000 hints/s, about 12-24 ms/s | about 1 MB/s |
| `Server.SendBroadcast` global | one RPC message, N packs | N x (envelope + string + 3 B) |
| `ClearBroadcasts` | trivial | N x envelope |

Whether `NetworkServer.SendToReady(new HintMessage(...))` is a safe one-pack fast path for an identical global hint is **unverified** (it would bypass `SuppressedReceivers`). Do not use it without testing.

## Open questions (unverified, need a real client)

1. How two plugins' hints interact beyond the replace rule. Windows narrower than about 10:9. Whether the reported aspect ratio follows a live window resize. (Aspect ratios, unit scale and centring with an offset: measured, see the layout section.)
2. How `durationScalar` and effect scalars are interpreted for short values (only a 99999 s hint and a constant `AlphaEffect(1, 0, 1)` on identical resends are verified). Whether a fade effect restarts on an identical resend.
3. Hint renderer: tags outside the verified list, maximum length, more than 15 lines, stray braces.
4. Why LabAPI and EXILED always send a non-empty parameter array.
5. Broadcast stacking order, maximum lines, `Truncated` rendering, `AdminChat` meaning, whether clear removes admin-chat lines.
6. Whether a hint sent between connection ready and client scene readiness can be dropped. Send only to ready players.
7. The real lag between server `NetworkTime.time` and the client timeline, which limits `TimespanHintParameter` precision.
8. `AnimationCurve` construction off the main thread.

## Re-verifying on a new build

- Decompile the game assembly and re-read `HintDisplay.Show`: still stateless, still throws for the local player, still honours `SuppressedReceivers`?
- Re-compute the wire size from the hint, parameter and effect serialisers if the byte count matters to you.
- Re-check `Broadcast` (flags, RPC names, who calls `RpcClearElements`) and LabAPI's `Server.SendBroadcast` / `ClearBroadcasts` implementations (public on GitHub).
- Re-check the UTF-8 string writer's maximum length and invalid-byte behaviour in the Mirror copy the game ships.
- Re-grep for `hints.Show` and `RpcAddElement` in the game assembly to list vanilla senders that can compete with your HUD.
- Re-run the client measurements on a new build: the replace rule, the identical resend, the container width, the anchor formula (y = 688 - voffset), the cap-top fit, the margin forms and the aspect-ratio landmarks. They come from one client build, so any change in the game's UI scaling can move them. How to measure them from screenshots or a screen recording: `adding-player-hud/hud-verification.md`. Settle the open questions above and strike the "unverified" marks.
