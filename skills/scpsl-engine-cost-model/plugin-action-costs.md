# What common plugin actions cost

Verified against LabAPI 1.1.7 / SCP:SL 14.2.7 (2026-10).

Tags: **[M]** measured, **[C]** counted or read from code, **[E]** estimate, **[U]** unverified. Untagged time figures are [E]. N = ready players (plus the host loopback where Mirror counts it), O = spawned network identities. Framing is not included in wire sizes (about 2 B message id plus 1-2 B batch length per message, plus transport headers). For where the base budget goes see tick-budget.md.

## 1. Event subscriptions

| Action | Cost | Notes |
|---|---|---|
| Raise with no subscriber | game builds the args object and its `Player.Get`/wrapper lookups anyway; then returns on a null check | [C]. Not subscribing removes the handler and the `Delegate[]`, not the args |
| Raise with 1+ subscribers | adds `GetInvocationList()` (a new `Delegate[]`, about 40 B for one subscriber [E]) per raise, a type check and a delegate call per subscriber | [C]. No `HasSubscribers` API exists in LabAPI |
| `ValidatedVisibility` handler | raised per receiver x target pair per position send: 9,900 raises per send at 100 players, 212k raises/s at 60 players and 60 TPS [C] | args 40 B, two `Player.Get` (dictionary lookup, native hash call): 110-220 ns and 0.7-2.2 ms per send even with no subscriber; 14-24 MB/s garbage at 60 TPS, 5-8 MB/s at 20 TPS [E]. A subscriber doubles the allocation count |
| **Subscribing** to `ValidatedVisibility`, measured | Measured with 60 dummies and a running round [M]. A counter-only handler adds a small but measurable amount to frame time; a handler that builds one string, one small list and does one `Player.Get` per raise adds several times more | The subscription costs little; per-raise work costs far more, even when it is this small. Logging, locks, LINQ, exceptions or script VMs per raise cost more still (not measured). Numbers, spread, arms, and what was not measured (real players, the full-server rate, GC over hours): tools/hot-bench/README.md, which is the one place they are kept |
| `ReceivingVoiceMessage` | per packet x listener: speakers x 100 x listeners per second, about 100k raises/s at 10 speakers [C] | args allocated per listener with no subscriber |
| `SendingVoiceMessage` | per packet: speakers x 100 raises/s, so 10 speakers is about 1,000 `Delegate[]` allocs/s with one subscriber [C] | rate is 100 packets/s per speaker (480 samples at 48 kHz) |
| Per-frame events (`UsingRadio`/`UsedRadio` per radio, `StayingInHazard` per hazard, `Escaping` per player in the escape zone, `UsingIntercom` while talking, `Scp106` vigor events, 173 observer events per hub per 173) | raised every frame | `UsingRadio`+`UsedRadio`: two args and four wrapper lookups per radio per frame; about 2.6 ms/s at 40 radios and 60 TPS with a custom-item handler attached [E] |
| `IdlingTesla`, `TriggeringTesla` | FixedUpdate rate (50 Hz default) | gate x player checks per step |
| Per-action events (door, damage, item, role) | about 20-40 events/s at 100 players (an assumption [U]) | Fine; keep handlers short. Shot and hit events: about 6-9 args per shot, roughly 2,000 allocations/s at 20 shooters, negligible [E] |
| `CustomEventsHandler` registration | once per `RegisterEventsHandler`: reflection over every handler method, about 1 MB transient garbage [U] | fine at `Enable`, never per player or round |

Full list with frequency classes: events-catalog.md in labapi-events.

Handler hygiene on hot paths: early return on a flag, no allocation, no LINQ, no `Logger` call, no throw. Per-raise work matters more than the subscription itself (numbers in tools/hot-bench/README.md): at roughly two thousand raises per frame even a small per-raise cost (one string, one list, one lookup) adds up to a visible part of the frame, and a `Logger` call (stack walk plus regexes) or a lock costs far more than that per call. Wrap in your own try/catch; LabAPI's log header names the last subscriber, not the thrower.

## 2. Exceptions and logging

| Action | Cost |
|---|---|
| `Logger.*` call (LabAPI) | `GetCallingAssembly` stack walk, `GetName()`, two regex passes in the console formatter, a dispatcher closure per output; the string is already built [C]. Cost unmeasured; plausibly tens of us per call [U] |
| Caught handler exception | stack-trace string, calling-assembly walk, regexes: 10-100+ us per throw [U]. A handler that throws every frame is a log storm: 90-360 ms/s at 30 radios [E] |
| `Debug.Log` warning storm from a plugin (for example a double `NetworkServer.Spawn` of the same object) | 5,145 warnings in one second [M for the count]; at an estimated 5-30 us each (formatting plus the native write) that is 26-155 ms of CPU in that second [E]. Error, assert and exception messages also pay a managed stack walk |
| `GC.Collect()` | a stop-the-world pause on the main thread; the game already collects between rounds (scpsl-lint SL010) |

Rules: build the message only after a flag check, never log per player or per frame, log rate-limited summaries (SlKit `RateLimitedLog` spec: token bucket per key, message built lazily).

## 3. Timers, coroutines, per-player work

| Action | Cost | Notes |
|---|---|---|
| One long-lived 1 Hz coroutine, yield before work, try/catch around the body | negligible | the pattern to copy |
| A coroutine or `CallDelayed` per player or per event | an iterator, a closure and MEC bookkeeping per call; 100 players x several events per spawn is thousands of entries in one frame [E] | use one clock that walks your own list (SlKit `SlClock.Every`, `EveryStaggered`) |
| EXILED `CurrentHint` | its hint postfix kills and starts a MEC coroutine and allocates for every hint shown [C] | hints sent while EXILED is installed each carry that extra cost |
| `Player.List`, `Player.ReadyList` in a per-frame path | O(N) per frame; `ReadyList` is a LINQ chain with small allocations per call | poll at 1-5 Hz, stagger, or keep your own set from `Joined`/`Left` |
| Staggered per-player job: N players over period P | about N/P work items per second, spread by wall clock | at 60 players, 1 s period, 60 fps: about one player per frame |
| Keep-alive burst (60 HUD resends in the same frame) | about 0.1-0.2 ms per burst (60 packs at 1-3 us [E]), but it recurs every period and aligns on restart | stagger the phase per player |

Update-segment MEC time is scaled and clamped; for wall-clock periods use `SlowUpdate` or a stopwatch deadline (scpsl-main-thread-and-timing).

## 4. Hints, broadcasts, console text

| Action | Main thread | Wire per receiver | Scaling |
|---|---|---|---|
| `Player.SendHint` to 1 player | one `HintMessage` pack, small array allocations | text + 15-25 B [U] | N hints = N independent packs, nothing shared |
| Per-frame hint HUD | 60 x N packs/s | 60 x N x (text + ~20 B) | do not do this. Send on change and re-send before expiry (keep-alive refresh shorter than the duration) |
| Server-wide `SendBroadcast` | one payload build, then N repacks of the RPC | 13 B RPC header + text | N packs, same order as N targeted sends. A player-targeted call saves audience size, not CPU per message |
| `SendConsoleMessage` | AES-GCM per player that has a key: new cipher, random nonce, several arrays [C] | ~60 B + text | N independent encryptions; per-call cost [U], tens of us guess |
| 20k hints in one frame (a HUD plugin refreshing everyone on each spawn and role change) | 40-80 ms [E] | | coalesce into one deferred refresh |

The client replaces: one hint slot per player, no stack, no queue (measured); see scpsl-hints-and-hud.

## 5. Spawned objects

| Action | Main thread | Wire | Scaling |
|---|---|---|---|
| Spawn one `PrimitiveObjectToy` | synchronous: per connection serialize initial state and pack | 133 B per observer [C] | x N, one-off. 50 toys x 100 players is about 670 KB in that frame; 1,744 toys is about 228 KiB per player |
| Reliable-stream consequence | a burst queues ahead of position messages | | with a 64-packet reliable window and about 1 KB fragments (about 65 KB in flight per RTT, MTU [U]), a 228 KiB burst is roughly 150-200 ms of delayed positions and voice at 50 ms RTT [E] |
| Hold one static toy | one more identity in the Broadcast walk: about 3 us per tick at 100 players [E] | 0 B/s | permanent |
| Hold one non-static, still toy | the above plus a server `LateUpdate` per frame, 0.3-0.5 us [E] | 0 B/s | 4,000 toys: 0.6-1.4 ms/frame [E] |
| Move a primitive every frame | marks dirty each frame | 48 B position only, 64 B with rotation, 76 B with scale per delivered update | rate is `min(frames, 1 / SyncInterval)`; primitives default 0.1 s (about 480-760 B/s per observer), 0 for text and speaker toys (2.9-4.6 KB/s) [U for defaults] |
| Move an `IsStatic` toy | transform write is ignored | 0 B | silent no-op; move a non-static parent or keep non-static until the update has been sent |
| `TextToy` text change | string sync var plus `SyncList<string>` ops that are always recorded, even for equal content | text bytes + overhead | prefab interval 0 [U], so every change goes out that tick; compare before writing |
| Spawn one pickup | per-connection spawn | ~95 B [U] | x N; moving pickups send about 50 B per observer every 0.25 s while awake |
| Despawn | one 6 B message | 6 B | x N |

Spawn in slices sized by bytes per frame: slice x 134 B x players. Despawn the same way. A static toy still costs per tick; only `IsStatic` removes the server's own `LateUpdate`.

## 6. Player state

| Action | Cost |
|---|---|
| `Player.Scale` set | one pack to every ready client, no equality check, 18-20 B each; per-frame scale animation is about 1.1 KB/s per receiver per animated player [E]; rate-limit |
| `Player.Position` set | one 14 B message to the owner plus a normal position update; a waypoint change sends one full 7 B entry to others; avoid per frame |
| Fake role per observer (`RoleSyncEvent`) | invoked for every pair on every send (N x (N-1) per tick) plus a pooled writer per pair; wire only when the per-pair role changes |
| Hand-built per-viewer messages | one pack and one send per call; overridden by a real dirty serialization of the same field; re-send on events |

## 7. Audio

| Action | Cost |
|---|---|
| `AudioTransmitter` / `SpeakerToy` playback | one Opus encode per 10 ms frame, 100 frames/s; sent to all authenticated players unless `ValidPlayers` is set; budget about 15 KB/s per listener per clip (average of the 120 kbps target, VBR can exceed it [U]); 100 listeners is up to about 1.5 MB/s |
| `ValidPlayers` predicate | runs per hub per frame: keep it O(1) (a `HashSet` refreshed at 10 Hz or less). A non-spatial speaker is heard everywhere, so its audience is not set by distance |

## 8. Harmony patches

| Patch | Cost |
|---|---|
| Any prefix on a hot method | a few ns per call [E]; the stub itself is about 4-5 ns. A prefix microbenchmark measured +17 ns per `CurrentRole` read and +10 ns per `GetRoleId` [M]; at about 13.7 reads per pair that is about 2 ms/frame at 100 players [E] |
| Prefix on an event invoker (for example `OnValidatedVisibility`) | does not remove args construction, which happens at the game call site first; the invoker is about 12 B of IL and may be inlined so the patch may not apply [U] |
| Prefix with `return false` on `WriteAll` or similar | skips the original, so every other plugin's behaviour that depends on that method silently stops (scpsl-lint SL017); a bug in it hits position sync for everyone |
| Transpiler, 1-for-1 swap at the call site, `Priority.Last` | no per-call dispatch cost beyond the replacement; keep the original instruction positions that EXILED anchors on, fingerprint the IL, refuse on mismatch |
| Patches on methods under about 20 B of IL | may be inlined by the JIT, so the patch may not run; patch the caller instead |
| Patching generic `EventManager.InvokeEvent<T>` | do not: shared generic code on Mono would change dispatch for every event |

Other plugins already patch these paths (ghost-mode transpilers, custom-role prefixes, anti-ESP tools), and some call `UnpatchAll(null)` on disable, so re-check `GetPatchInfo` at lifecycle points. See scpsl-harmony-patching.

## 9. HTTP, files, joins

| Action | Cost |
|---|---|
| Synchronous HTTP on the main thread | the whole server stalls for the call: 35-50 ms every minute for a telemetry POST [M], up to 15 s on a timeout. Use a static `HttpClient` on a worker with a snapshot taken on the main thread (SlKit `SlHttp`, `Background`) |
| `File.ReadAll*`/`WriteAll*` per event | blocks the frame; read once in `Enable` |
| Work in `PreAuthenticating` | runs on the single connection-request thread (not the main thread): a slow handler delays every queued join |
| Join | about 5 ms of main-thread crypto [M], ban-file parse, then a spawn replay of O identities; heavy plugin work in `Joined` stacks onto this |

## 10. Patterns that cost almost nothing

- A 1 Hz position sample compared by squared distance. No Update or `*Moving` hook.
- A cached monotonic clock so a voice handler is one dictionary lookup and one field write.
- Change-driven HUD: a cheap integer key, a reused `StringBuilder`, and a keep-alive resend (for example every 8 s for a 10 s duration) that bypasses the change check.
- HTTP off the main thread: a static client, a snapshot built on the main thread from plain data, a timeout, a single-flight guard, a bounded retry queue.
- One long-lived coroutine or clock job with the yield outside the try block, a try/catch inside, and the handle kept so you can kill only your own.
- Re-baseline derived state on `ChangedRole` rather than recomputing every frame.

## 11. Sketches

Per-pair handler, only if a per-pair decision is truly unavoidable. Measure it with `FrameProfiler` before shipping and see scpsl-networking-for-plugins for what hiding does to audio and spawn data.

```csharp
// Design target 20-50 ns per pair: flat array, no dictionary, no allocation, never throws.
private static readonly bool[] Hide = new bool[128 * 128];   // refilled once per frame elsewhere

private static void OnValidatedVisibility(PlayerValidatedVisibilityEventArgs ev)
{
    int o = ev.Player.PlayerId, t = ev.Target.PlayerId;
    if ((uint)o < 128u && (uint)t < 128u && Hide[(o << 7) + t])
        ev.IsVisible = false;
}
```

Frame-rate alert against the target in force, not a literal (`Server.MaxTps` is only the configured value, which Mirror may have overridden; call `FrameProfiler.Install()` once in `Enable`; the 0.8 ratio and window are example values):

```csharp
private static int _bad;

private static void CheckFrameRate()               // SlClock.Every(10 s, ...)
{
    if (Server.IdleModeActive) { _bad = 0; return; }      // idle: no data
    int target = Application.targetFrameRate;             // target in force, not 30 or 60
    if (target <= 0) return;                              // -1 = uncapped
    double ratio = FrameProfiler.Snapshot().RealFps / target;
    _bad = ratio < 0.8 ? _bad + 1 : 0;
    if (_bad == 3) Logger.Warn("Frame rate at " + (int)(ratio * 100) + "% of target " + target);
}
```

Staggered HUD refresh instead of 60 resends in one frame:

```csharp
SlClock.EveryStaggered<Player>(TimeSpan.FromSeconds(8), RefillReadyPlayers, ResendHud, "hud.keepalive");
```

## Re-verifying on a new build

- Re-run the allocation check on the hot events: with zero subscribers, count allocations per second at 60 dummies (a no-subscriber baseline) and compare with sections 1 and 8 of this file.
- Check `EventManager.InvokeEvent` still returns early on a null delegate and still calls `GetInvocationList()` only with subscribers.
- Check the spawn wire size by logging one `NetworkWriter` length for a spawned toy.
- Re-read the toy defaults (`SyncInterval`, `IsStatic`, `MovementSmoothing` inversion) through the LabAPI toy wrapper at runtime.
- Verify `AudioTransmitter` still defaults to all authenticated players unless `ValidPlayers` is set.
- Confirm whether hint, console and broadcast calls still pack per receiver (a message counter on a capture connection).
- Re-run the catalog generator and diff the hot list (`tools/catalog/labapi-events.json`); scpsl-lint reads the same file.
