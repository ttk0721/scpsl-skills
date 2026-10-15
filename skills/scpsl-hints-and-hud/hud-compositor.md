# HUD design for 60 players: one compositor, send on change, staggered keep-alive

Verified against LabAPI 1.1.7 / SCP:SL 14.2.7 (2026-10).

Costs below are **estimates** built on an unmeasured 2-4 us per `SendHint` (three small allocations in LabAPI plus the Mirror pack and batch copy) and about 20 + n bytes per hint on the wire (derived from the serialisers, not captured). Benchmark `Show` x N players on your own server before treating any figure as fact.

## Why one compositor

- The server has no hint arbitration, no queue, no priority and no memory of what a player currently sees. `HintDisplay.Show` just sends one reliable-ordered message per call.
- The client has **one hint slot per player**: a new hint replaces the current one at once, with no stacking and no queue (verified on a real client, SCP:SL 14.2.7). Two plugins sending independently therefore overwrite each other on every send, and whoever sent last owns the screen. Two texts can be visible together only inside one string.
- The game sends its own hints too (item-pickup warnings such as "inventory full"), and other plugins send one-shot hints. Each of them should cover the HUD until the HUD's next send (follows from the single slot; not tested with a vanilla or third-party sender). A HUD sent once with a long duration is not restored by itself. A keep-alive resend bounds that gap to one refresh period plus one visit period, and should cost nothing visually, since an identical resend every 0.5 s did not flicker.
- Conclusion: one HUD owner per server (or per plugin set) composes everything into one string. Other plugins submit text segments to it and never call `SendHint` for the same players. A foreign hint you cannot stop (vanilla, a closed plugin) still covers the HUD until your next send, and the keep-alive is the repair.
- No LabAPI event exists for hints or broadcasts. You cannot intercept another plugin's hints without Harmony.

## The rules, in numbers (60 players)

| Choice | Value | Why |
|---|---|---|
| Visit rate per player | 2 Hz (0.5 s period) at most, 1 Hz is fine | Policy, not a measured limit. Latency of a change is at most one period. |
| Hint `duration` | 3 s | Client-side lifetime. The server has no timer. |
| Keep-alive resend | every 2 s, **bypasses the change check** | `duration` must cover refresh + visit period + margin (3 s covers 2 s + 0.5 s visit + 0.5 s margin). It also repairs a HUD that vanilla or another plugin replaced, and it should be quiet: an identical resend every 0.5 s did not flicker (verified, client; the 2 s period is untested). |
| Send when | text changed OR keep-alive due | Never per frame, never "refresh everyone" from an event. |
| Ticking numbers | client-side `TimespanHintParameter` | Zero traffic between structural changes. See hint-parameters.md. |
| Discrete numbers (HP, ammo) | quantise, then send on change | Show HP in whole points or steps of 5: fewer changes, fewer messages. |
| Clearing | do not send an empty hint | Let a short-duration hint expire. An empty hint does clear the slot (verified, client), but it costs a full message and also wipes whatever another plugin showed. Send one only to hide your own HUD at once. |
| Recipients | `Player.ReadyList`, skip `IsDummy` | `ReadyList` already excludes the host (a hint to the host throws `InvalidOperationException`) but includes dummies, which serialise and discard. |

Steady-state estimate for 60 players with an unchanged HUD: 30 keep-alive hints/s (60 players every 2 s), about 0.06-0.12 ms/s of main-thread time and roughly 5 kB/s of payload at n = 150. If every HUD changed on every visit it would be 120 hints/s, about 0.24-0.5 ms/s. A per-frame hint at 60 TPS would be 3600 hints/s, about 7-14 ms/s plus a continuous garbage stream. At 60 players, 60 hints/s (for example one HP hint per second to everyone) is within policy: it is wasted work, not a defect. The change check removes most of it for stable players.

## Why stagger explicitly

Do not assume keep-alives are naturally spread because players joined at different times. After a round restart (all hubs are recreated) or a mass join, every session starts in the same frame, so all 60 keep-alives fire together every refresh period. The burst is still small (about 0.1-0.25 ms, est.), but it lands in one batch flush for no reason. `SlClock.EveryStaggered` spreads one visit per player over the period (with a 0.5 s period, 60 players and 60 fps that is about two players per frame). Because a send happens only at a player's visit, the keep-alives inherit that spread. The spread is approximate: the list order can change when players join or leave.

See scpsl-game-lifecycle for why timers realign after a round restart, and scpsl-main-thread-and-timing for why the job runs on one wall-clock scheduler instead of a coroutine per player.

## Reference compositor (original code, SlKit + LabAPI)

```csharp
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using LabApi.Features.Console;
using LabApi.Features.Wrappers;
using SlKit;

public static class Hud
{
    public delegate void Segment(Player p, StringBuilder sb);   // append balanced rich text, or nothing

    private sealed class State { public string Sent = ""; public long SentAt; }

    private const float DurationSeconds = 3f;                                   // client lifetime
    private static readonly long KeepAliveTicks = (long)(2.0 * Stopwatch.Frequency); // < duration
    private static readonly List<Segment> Segments = new List<Segment>();
    private static readonly Dictionary<string, State> States = new Dictionary<string, State>(64);
    private static readonly List<Player> Scratch = new List<Player>(64);
    private static readonly StringBuilder Sb = new StringBuilder(256);

    public static void Start()   // call from Enable(), main thread
    {
        SlClock.Install();
        SlClock.EveryStaggered<Player>(TimeSpan.FromSeconds(0.5), Refill, Visit, "hud");
    }

    public static void AddSegment(Segment s) => Segments.Add(s);
    public static void Forget(Player p) => States.Remove(p.UserId);   // on Left
    public static void ResetAll() => States.Clear();                  // on WaitingForPlayers

    private static IReadOnlyList<Player> Refill()
    {
        Scratch.Clear();
        foreach (Player p in Player.ReadyList)   // authenticated players + dummies; host excluded
            if (!p.IsDummy) Scratch.Add(p);
        return Scratch;
    }

    private static void Visit(Player p)
    {
        if (p.IsDestroyed) return;                       // the list is a snapshot
        if (!States.TryGetValue(p.UserId, out State st)) States[p.UserId] = st = new State();

        Sb.Clear();
        foreach (Segment seg in Segments) seg(p, Sb);
        if (Sb.Length == 0) return;                      // nothing to show: let the old hint expire
        string text = Sb.ToString();

        long now = Stopwatch.GetTimestamp();
        bool changed = !string.Equals(text, st.Sent, StringComparison.Ordinal);
        bool keepAlive = now - st.SentAt >= KeepAliveTicks;   // NOT suppressed by the change check
        if (!changed && !keepAlive) return;

        st.Sent = text;
        st.SentAt = now;
        try { p.SendHint(text, DurationSeconds); }
        catch (Exception e) { Logger.Warn("hud: send to " + p.UserId + " failed (" + e.Message + ")"); }
    }

    // For PLAYER-CONTROLLED text only (nicknames, custom info). See hint-limits-and-broadcasts.md.
    public static string Safe(string s, int maxChars = 32)
    {
        if (string.IsNullOrEmpty(s)) return "";
        if (s.Length > maxChars)
        {
            int n = maxChars;
            if (char.IsHighSurrogate(s[n - 1])) n--;      // never cut a surrogate pair
            s = s.Substring(0, n);
        }
        return s.Replace('{', '｛').Replace('}', '｝').Replace('<', '＜').Replace('>', '＞');
    }
}
```

How other code uses it (events only feed data, they never send):

```csharp
Hud.AddSegment((p, sb) =>
{
    if (!p.IsAlive) return;
    sb.Append("<size=70%>HP ").Append((int)p.Health).Append("</size>\n");   // tags closed in the same segment
});
ServerEvents.WaitingForPlayers += Hud.ResetAll;
PlayerEvents.Left += ev => Hud.Forget(ev.Player);   // use a named handler so you can unsubscribe
```

Notes on the sketch:

- **Segments close their own tags.** An unclosed `<align>` or `<size>` leaks into whatever follows it. The game has a helper, `Misc.CloseAllRichTextTags(string)`, that appends missing closing tags. Use it as a safety net over third-party segments, not as a substitute. Hints render `<b>`, `<i>`, `<u>`, `<color>`, `<size>`, `<align>`, `<pos>`, `<voffset>`, `<line-height=0>`, positive `<margin-left>`/`<margin-right>` and `<noparse>` (verified on a real client); the rest of the game's tag list is untested.
- **Placement.** The default hint is centred, its block about 146 px below the screen centre. `<align>`, `<pos>` and `<voffset>` act inside a centred 1200-unit container (x 360..1560 at 1080p, 16:9): left and right alignment stop at its edges, text past the right edge wraps to the start of the line, a negative `pos` reaches the left screen edge, and the right screen edge cannot be reached without wrapping (full table: hint-limits-and-broadcasts.md). Everything scales with the window height; at other aspect ratios only the area left of the container moves (the left screen edge is at `pos = -(1080 x aspect - 1200) / 2`), so keep layout tags optional and adapt or warn about that area. Centring at another x and ending right-aligned text before the container edge work with positive margins (same table).
- **Parameters.** The sketch sends text only. To add a `TimespanHintParameter`, let segments also append to a per-player `List<HintParameter>` and write `{0}`, `{1}` in the text. The text stays identical between visits, so the change check still works, and the keep-alive rebuilds the parameters with a freshly computed age. Only player-controlled text goes through `Safe`; your own `{n}` placeholders must stay intact.
- It rebuilds the string on each visit (120 builds/s for 60 players at 2 Hz, a few hundred bytes each, est.). If your segments are expensive, keep a per-player version counter that handlers bump, rebuild only when it changed, and reuse the cached string for keep-alives. Bumping a counter is cheap even from a per-action event. Do not subscribe to a HOT event for this (see labapi-events).
- Key state by `UserId`, never keep `Player` across a round restart, and drop it on `Left` and `WaitingForPlayers`. `PlayerId` is recycled. A hub destroyed mid-period is why `Visit` checks `IsDestroyed`. SlKit already runs each job in its own try/catch, but keep the inner one so one failing send does not skip its own bookkeeping.
- Everything here runs on the main thread. A worker thread computes strings only and hands them over with `MainThread.Post`. The Mirror writer pool and batches are plain unsynchronised collections.

## Absolute vertical placement: anchor lines

Without anchors, where a `<voffset>` line lands depends on the rest of the hint: the same `voffset=0` text can move by about 60 px when the other lines of the hint differ, so segments written by different plugins do not line up. Two zero-height lines at the start and the end of the composed string remove that. With them, a `voffset=0` line stays at the same place across hints with different content, so `voffset` is absolute (verified, client: 1920x1080, 16:9; the pattern is two off-screen characters at `voffset` +9999 and -9999 on their own lines). Add them in the compositor, once, never in a segment, and keep the body lines inside `<line-height=0>`, one `<voffset>` per line. That is the only form that was measured: a body with the normal line height under the anchors was not tested.

```csharp
// Two off-screen anchor lines (one character at voffset +9999, one at -9999).
const string AnchorTop    = "<line-height=0><voffset=9999>P</voffset>\n";
const string AnchorBottom = "<line-height=0><voffset=-9999>P</voffset>\n";

// Apply after the "nothing to show" check: in Visit, string text = Anchored(Sb.ToString());
static string Anchored(string body) => AnchorTop + "<line-height=0>" + body + AnchorBottom;

// At 1080p: screen y (px from the top edge) = 688 - voffset; a line's baseline sits 9 px lower and its capitals are
// 0.70 x size tall, so cap top = 697 - voffset - 0.70 x size (sizes 20..80; independent of the aspect ratio).
// voffset=0 sits about 148 px below the screen centre; the visible range is about +688 (top) to -392 (bottom).
// A left-aligned line 300 units above voffset=0 (y = 388), starting at the container edge (x = 360):
string text = Anchored("<size=24><align=left><voffset=300>HP 100</voffset></align></size>\n");
// To start at the left screen edge instead: <align=left><voffset=300><pos=-360>HP 100</voffset></align>
```

Each segment then appends one line of the form `<align=..><voffset=N>...</voffset></align>\n` and closes its own tags. The anchors add 98 bytes to every hint, so count them in the wire estimates above. The 688 and 148 px figures are for a 1080-pixel-high window; at another height multiply by height / 1080.

## Hint storms (RefreshAll on spawn)

Refreshing every player's HUD from every spawn, role-change and death handler costs N handlers x N recipients in one frame at round start: on the order of 20 000 `HintMessage`s, plus one MEC coroutine each if the refresh starts one (an estimated 40-80 ms spike, 6-8 MB of garbage). Empty "clear" hints sent the same way also blank other plugins' HUDs.

```csharp
// WRONG: O(N^2) at round start, and a spike in the frame that is already the heaviest.
PlayerEvents.ChangedRole += ev => { foreach (Player p in Player.ReadyList) p.SendHint(Build(p), 3f); };

// RIGHT: events change data; only Hud.Visit sends, at most once per player per visit.
PlayerEvents.ChangedRole += ev => { /* update your own data; nothing is sent here */ };
```

If EXILED is installed, its always-on `CurrentHint` patch adds roughly 0.5-1.5 us and 250 B per hint on top (est.), and it starts one coroutine per hint, so hint volume matters more there.

## Counters that tick: do not resend them

Session time since join, round time, cooldowns and respawn timers are pure functions of time from a fixed start. Send them once as a `TimespanHintParameter` and resend only on keep-alive or structural change. Resending "Session 12:03" as text every 2 s is the common mistake. "Active time excluding AFK" pauses and resumes, so it is not a client-evaluated count-up. Show it at minute resolution and send on change. Details: hint-parameters.md.

## Other surfaces

- **Broadcasts** are for rare, global, one-off announcements, not a HUD. See hint-limits-and-broadcasts.md.
- **SSS text areas** are visible only while the player has the settings tab open (`IsTabOpenForUser`). They are not a HUD. See scpsl-server-specific-settings.
- Lint rule **SL012** (scpsl-lint) flags `SendHint`/`SendBroadcast` inside a per-frame path.

## Re-verifying on a new build

- Re-check that `HintDisplay.Show` is still stateless (no current-hint field, no dedupe) and still throws for the local host player.
- Re-run the client measurements on a new build: the replace rule, the identical resend, the container width, the anchor formula, the cap-top fit and the aspect-ratio landmarks (method: adding-player-hud/hud-verification.md). Settle what is still open (fade effects on an identical resend, maximum length, tags outside the verified list), update the "unverified" marks and the keep-alive margin.
- Re-measure `SendHint` cost with a Stopwatch around 60 sends on the target server; replace every "est." above with the measured number.
- Re-check the vanilla senders (search the game assembly for `hints.Show`) and `HintDisplay.SuppressedReceivers`: the game suppresses hints for a connection briefly while it re-creates pickups after an escape or role change, so a hint sent in that window is silently dropped. The next keep-alive covers it.
- Re-check that `Player.ReadyList` still excludes the host and includes dummies.
