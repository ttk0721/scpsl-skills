# Hint parameters: updating HUD text on the client without resending

Verified against LabAPI 1.1.7 / SCP:SL 14.2.7 (2026-10).

A hint's text may contain `{0}`, `{1}` placeholders, and `Player.SendHint(text, HintParameter[] parameters, HintEffect[] effects, float duration)` ships the parameter objects with it. The server is a pure value carrier. What the client does with a parameter is client code that is **stripped from the dedicated-server build**, so live updating is inferred from the protocol and the server-side classes unless it says it was observed on a real client (SCP:SL 14.2.7). Only the `TimespanHintParameter` count-up is verified on a client. Where a claim rests on inference it is marked **unverified**.

## Which parameters are live

| Parameter | Updates on the client without traffic? | What it carries |
|---|---|---|
| `TimespanHintParameter` | **Yes** (verified on a real client for a count-up: one send, then the seconds advance on the client) | a server-time anchor + a .NET `TimeSpan` format string + a negate flag |
| `AnimationCurveHintParameter` | **Yes** (re-formatted on every client update; inferred, unverified on a real client) | an anchor time, an `AnimationCurve`, a number format, a round-to-int flag |
| `SSKeybindHintParameter` | Resolved once on the client from ITS synced setting list | the setting id + format (default `"[{0}]"`); prints the player's own bound key, or `KEY NOT ASSIGNED` / `SERVER SETTING NOT FOUND` |
| `ItemHintParameter`, `AmmoHintParameter`, `ItemCategoryHintParameter`, `Scp330HintParameter` | Once | client-localised item names, so you need no translation table |
| String, Int, Byte..ULong, Float, Double, Packed* | Once | a value formatted a single time (floats and doubles use the CLIENT's culture) |

Only the first two keep changing. Every other parameter is formatted once and then frozen, so a number in one of them goes stale until you resend the hint.

Exactly four parameter classes override the per-update state hook, and only Timespan and AnimationCurve lack a "stop formatting after the first time" guard. That is the evidence that those two are the live ones.

## TimespanHintParameter (counters and timers)

- Build it with `TimespanHintParameter.FromOffset(double secondsOrTimeSpan, string format, bool negate)`. It stores `NetworkTime.time + offset` (server time at call).
- The client shows `(anchor - now)`, negated if `negate` is true.
- Countdown: `FromOffset(30, @"mm\:ss", negate: false)`. Hint duration must not exceed the countdown, because a .NET custom `TimeSpan` format has no sign, so after zero the display may count back up (unverified on the client's Mono).
- Count-up from now: `FromOffset(0, @"mm\:ss", negate: true)`. Verified (client): with `{0}` in the text, `FromOffset(0, @"h\:mm\:ss", true)` advances its seconds on the client from a single send (the direction follows the formulas above).
- Count-up from a moment `age` seconds ago (session time since join): `FromOffset(-age, @"h\:mm\:ss", negate: true)`. This follows from the formulas above and is **derived, not tested**.
- Always recompute `age` from your stored start timestamp when you resend. Resending with offset 0 restarts the counter at 00:00.
- Custom `TimeSpan` format fields are components, not totals: `mm` is minutes modulo 60 and `h` is hours modulo 24. Use `h\:mm\:ss` for anything that can pass an hour, and add a `d` field if it can pass a day.
- **Never use the `TimespanHintParameter(DateTimeOffset, ...)` constructor.** It feeds a relative number of seconds into the constructor that expects an absolute `NetworkTime`, so the result is nonsense. `FromOffset` is the correct entry.
- Fit: anything that is a pure function of time from a fixed start (session time, round time, a cooldown or a respawn timer). It does not fit "active time excluding AFK", which pauses and resumes. For that, update the text at minute resolution and send on change.

Wire cost of the parameter: about 1 type byte + 8 (double) + 2 + format length + 1 negate byte (derived from the serialiser, not captured).

```csharp
// Count-up of session time, one hint, then the client keeps it ticking.
// ageSeconds = wall-clock seconds since the join, from YOUR stored start timestamp.
static void ShowSession(Player p, double ageSeconds)
{
    var t = TimespanHintParameter.FromOffset(-ageSeconds, @"h\:mm\:ss", negate: true);
    p.SendHint("Session {0}", new HintParameter[] { t }, null, 3f);
}
```

## AnimationCurveHintParameter (progress bars, regenerating numbers, blinking)

The client evaluates `curve(NetworkTime.time - offset)` and formats it. Pass `NetworkTime.time` as the offset to start the curve now. When you resend a hint that carries one, **keep the original offset**. A fresh offset restarts the ramp.

### Curve traps (all of them are in the wire serialiser)

1. **Looping.** The client forces the curve's post-wrap mode to `Loop`, so it repeats after its last key. A 0..10 counter over 10 s drops back to 0 at t = 10. Make the curve at least as long as the hint, or use the loop on purpose for a pulse.
2. **Key count.** 2 to 257 keys. More than 257 throws `ArgumentException` on send. The count is stored as `length - 2` in one byte, so a 0- or 1-key curve underflows and corrupts the stream.
3. **Tangents.** The keyframe serialiser tests the in-tangent twice and never tests the out-tangent. A key with `inTangent == 0` and `outTangent != 0` is sent with BOTH tangents zero. `new Keyframe(t, v)` has zero tangents, so a two-key curve built from it evaluates as an ease (flat ends), not a line.

### The refuted claim: `AnimationCurve.Linear` is a safe way to build a ramp

**Refuted.** `AnimationCurve.Linear(t0, v0, t1, v1)` builds key 0 as `(t0, v0, in 0, out slope)` and key 1 as `(t1, v1, in slope, out 0)`. Key 0 has `inTangent == 0`, so trap 3 sends it with zero tangents and the slope is lost. Key 1 keeps `in = slope`. The client therefore evaluates the single segment with out-tangent 0 at the start and slope at the end, which is `v0 + (v1 - v0) * (2u^2 - u^3)` for normalised time `u`. That is an ease-in that reaches only **37.5 %** at the midpoint instead of 50 %. (Derived by hand from the Hermite basis; not run on a client.)

The fix is to give EVERY key equal, non-zero in and out tangents:

```csharp
// Client-side ramp 0 -> 100 over 20 s. Keep the hint duration <= 20 s (the curve loops after).
const float slope = 100f / 20f;
var curve = new AnimationCurve(
    new Keyframe(0f, 0f, slope, slope),
    new Keyframe(20f, 100f, slope, slope));
var bar = new AnimationCurveHintParameter(NetworkTime.time, curve, "0", integral: true);
p.SendHint("Charge {0}%", new HintParameter[] { bar }, null, 20f);
```

Rule of thumb: never rely on a key whose `inTangent` is 0 while its `outTangent` is not. For a flat curve leave all four tangents at zero.

Wire cost of a curve parameter: 1 + 8 + format string + 1 + curve (1 count byte, `ceil(keys/4)` flag bytes, 8 bytes per key for time and value, plus 8 for the tangent pair when the in-tangent is non-zero and 8 for the weight pair when a weight is non-zero). A two-key curve is roughly 20-40 bytes.

Whether `new AnimationCurve(...)` is legal off the main thread is **unverified**. Build it on the main thread.

## Effects

`HintEffect` entries (alpha, alpha curve, outline) carry a start scalar and a duration scalar. That the scalars are fractions of the hint duration is inferred from vanilla usage (a fade-in/out preset on a 1.5 s hint) and is **unverified**. Presets such as `HintEffectPresets.FadeInAndOut` exist. An identical resend every 0.5 s does not flicker or pulse, with or without an `AlphaEffect(1, 0, 1)` attached (verified, client). Whether an identical resend re-triggers a fade preset is unverified, so test with a real client before combining a fade with a keep-alive.

## Durations

`SendHint(..., float duration = 3f)`. LabAPI documents seconds and vanilla uses 3 s, 2 s and 1.5 s, but how the client interprets the scalar for short values is unverified (a 99999 s hint stays until replaced; verified, client, over a few seconds). The server keeps no timer and no "current hint" state. Expiry is entirely client-side.

## Re-verifying on a new build

- Find the hint parameter reader/writer types in the game assembly and re-check the type bytes (0..19 in 14.2.7) and which parameter classes override the per-update state hook. A fifth live parameter changes the table above.
- Re-check the animation-curve serialiser: does it still test the in-tangent twice, still force the `Loop` wrap mode, still store the key count as `length - 2`? If any of these changed, the Linear trap and the 2..257 limit change with it.
- Decompile Unity's `AnimationCurve.Linear` again if the Unity version changes. The refutation depends on key 0 having `inTangent == 0`.
- Check that `TimespanHintParameter.FromOffset` still adds the server `NetworkTime.time` and that the `DateTimeOffset` constructor still passes a relative value.
- Still to prove on a real client (the count-up from `FromOffset(0, ...)` is confirmed): send the Linear curve and the explicit-tangent curve side by side and read the midpoint value; send a `FromOffset(-120, ..., negate: true)` hint and confirm it starts at 02:00 and keeps ticking for the full duration.
