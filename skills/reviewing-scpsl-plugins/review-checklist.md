# Review checklist (ordered by impact on a 60-player server)

Verified against LabAPI 1.1.7 / SCP:SL 14.2.7 (2026-10).

Walk it top to bottom. The order is the order of damage: a section near the top can stall the
whole server, a section near the bottom costs a log line or one round. For every hit, write a
finding in the format of `review-report.md`. "Lint" names the scpsl-lint rule that finds the
pattern automatically; everything else needs reading.

Arithmetic you will reuse (60 players, 60 frames/s):

| Rate class | Calls per second | Source of the rate |
|---|---|---|
| per receiver x target pair, per position send | up to 60 x 59 x 60 = ~212 000 | at most 60 position sends/s (the send rate follows `server_tickrate`, clamped to 10-60) |
| per voice packet | 100 per speaker (`SendingVoiceMessage`) | 480-sample frames at 48 kHz |
| per voice packet x listener | 100 x listeners per speaker (`ReceivingVoiceMessage`); 10 speakers ~ 59 000 | one raise per listening hub |
| per frame | 60 x instances (radios, hazards, gates, players in escape zone) | Update / FixedUpdate loops |
| per player, per frame | 3 600 | `Update` or `WaitForOneFrame` loop over players |
| per action (door, damage, role) | tens, bursts at round start and detonation | player input |

Per-call costs in this file are estimates unless marked measured. Use them to rank, then measure
(measuring-scpsl-performance) before you quote milliseconds.

## 1. Hot event subscriptions — Lint: SL001, SL002, SL003

- Grep `+=` on `PlayerEvents.ValidatedVisibility`, `ReceivingVoiceMessage`, `SendingVoiceMessage`,
  `UsingRadio`, `UsedRadio`, `StayingInHazard`, `IdlingTesla`, `TriggeringTesla`, `Escaping`,
  `UsingIntercom`, `Scp106Events.ChangingVigor/ChangedVigor`, `Scp173Events.AddingObserver/RemovingObserver`,
  `PlacingBulletHole/PlacedBulletHole` (per-action, borderline), and the `override void On...` equivalents on a `CustomEventsHandler`. Full list with rates:
  events-catalog.md in labapi-events.
- Also grep reflection subscriptions (`EventInfo.AddEventHandler`, `GetEvent(`): a scripting layer
  that lets admins bind any event can bind a hot one at runtime. Require a deny-list for hot events.
  On a running test server, `SlKit.HotEventAudit.Log()` at `WaitingForPlayers` lists every handler
  subscribed to a hot event at that moment, including runtime and reflection subscriptions that no
  grep sees (call it again later to catch late binds). It shows who is subscribed, not how slow they are.
- Why it ranks first: the game builds the args object (with wrapper lookups such as `Player.Get`)
  for every raise even with zero subscribers. Subscribing adds one `Delegate[]` allocation per
  raise plus your handler. Anything beyond a flag test or dictionary lookup, multiplied by the
  rates above, is a per-frame cost on the main thread. A handler that throws on a hot event logs a
  full stack trace per raise.
- Acceptable: a hot subscription whose handler is O(1), allocation-free, cannot throw, and exists
  only while the feature is active. A cheap `SendingVoiceMessage` handler (dictionary lookup plus a
  timestamp write) can be the right design; 1 Hz polling would miss short speech.
- Also check: subscriptions to never-raised events (SL002: the handler is dead code), and
  assignments to setters the game ignores (SL003: e.g. reassigning `ev.DamageHandler` in `Hurting`
  does nothing; mutate the same handler object or cancel).

## 2. Per-frame and per-pair work — Lint: SL011, SL012, SL005

- Grep `void Update(`, `LateUpdate(`, `FixedUpdate(`, `Timing.WaitForOneFrame`, `yield return 0f`,
  `Segment.Update` loops, and PlayerLoop insertions. For each: what runs, over how many players,
  and is it throttled?
- Red patterns: `Player.List`/`ReadyList`/`ReferenceHub.AllHubs` iterated every frame (SL011);
  `SendHint`/`SendBroadcast` every frame (SL012); `Physics.*cast`/`Linecast` per pair per frame;
  LINQ in the loop; a coroutine or `CallDelayed` started per event or per player on a
  per-action-or-hotter path (SL005).
- Fix direction: 1-5 Hz polling with squared-distance compares, work staggered across frames
  (`SlClock.EveryStaggered`), own player collection maintained from `Joined`/`Left`, one clock for
  all periodic work (scpsl-main-thread-and-timing).
- Per-pair designs (visibility, anti-ESP): require a flat array indexed by `PlayerId`, no
  dictionaries or allocation per pair, and a stated per-pair budget (scpsl-engine-cost-model).

## 3. Blocking the main thread — Lint: SL007

- Grep `.Result`, `.Wait(`, `.GetAwaiter().GetResult()`, `WebClient`, `GetResponse()`,
  `Thread.Sleep`, `.Join()`, `File.ReadAll*`/`WriteAll*`/`AppendAll*`, `UnityHttp`, database
  drivers, inside handlers, `Update`, coroutines, and helpers they call (lint does not follow calls).
- Why: handlers and coroutines run inside the frame. A synchronous telemetry POST on the main thread every minute
  blocks the frame for ~35-50 ms, and up to 15 s when the network or DNS fails.
  Waiting on a worker that needs the main thread is a deadlock.
- Fix: async IO with `ConfigureAwait(false)`, or the plugin's own `Background.ForOwner` worker and `SlHttp.CreateClient` client (the static `Background.TryRun`/`SlHttp.PostJsonAsync` share one worker and one client with every plugin: flag them in anything that runs regularly); snapshot plain data
  on the main thread first; return results via `MainThread.Post`. Read files at `Enable()`.

## 4. Logging and exception handling — Lint: SL008, SL009

- Grep `Logger.`, `Log.`, `Debug.Log` inside handlers, loops and per-frame paths;
  `FirstChanceException`; `UnhandledException`; config flags like `Debug`/`Verbose` that default
  to true or are forced on in code; `catch` blocks that log with no rate limit.
- Why: LabAPI `Logger` has no level gate. `Logger.Debug(msg, canBePrinted)` only checks a caller
  flag after the message string is already built. Every call does a calling-assembly stack walk,
  allocates an `AssemblyName`, runs two regex passes in the console formatter and posts one
  closure per console output. For scale: a burst of about 5 100 native `Debug.Log` warnings in one
  second is estimated at 26-155 ms of CPU (an estimate, not a measurement;
  `Logger` does more work per line than that, and its per-line cost is unmeasured).
- `AppDomain.CurrentDomain.FirstChanceException` runs for every exception thrown anywhere in the
  process, before any `catch` is searched, including exceptions the game and other plugins throw
  and handle as normal control flow. It is a tax on code the plugin does not own. See the worked example in review-report.md.
- Exceptions on hot paths are expensive even when caught (throw, unwind, stack-trace string;
  roughly 10-100+ us each, unmeasured). The fix is to not throw, not to log less.
- Fix: guard with the plugin's own flag before building the message; rate-limit and summarise
  ("N suppressed", e.g. `SlKit.RateLimitedLog`, whose message factory runs only when a line is
  allowed); no logging in hot handlers; diagnostics off by default and switchable in
  config without a rebuild; catch at the plugin's own entry points instead of a process-wide hook.

## 5. Allocations in hot paths — Lint: SL010 (GC.Collect only) and SL005; the rest needs reading

- Grep LINQ (`.Where(`, `.Select(`, `.ToList(`, `.Any(` with a lambda), lambdas capturing locals,
  `string.Format`/interpolation, `params` calls, `new List<`, boxing via non-generic collections,
  `foreach` over `Player.List` (boxed enumerator) in anything rated per-frame or hotter.
- `GC.Collect()` anywhere (SL010).
- Why: the server runs the Boehm collector: non-moving, non-generational, incremental in 3 ms
  slices. Every collection marks the whole live heap, and the allocation rate decides how often
  that happens. The game already collects at scene load and before a round restart.
- Fix: reuse buffers and `StringBuilder`s, pre-size collections, index loops, cache delegates.
  A lambda in a per-round handler is fine; do not report it as a performance finding.

## 6. Hint and broadcast storms

- Grep `SendHint`, `ShowHint`, `SendBroadcast`, `ClearBroadcasts`, `shouldClearPrevious`, and any
  "refresh all" called from `Spawned`, `ChangedRole`, `Death`, `Joined`, `RoundStarted`.
- Why: each hint is one full network message per recipient, serialised at the call, with no
  dedupe, queue or arbitration. Refresh-everyone per spawn event is O(N^2) per burst: it can send
  ~20 000 hint messages in one frame at round start (estimated 40-80 ms
  spike, not measured). Iterating `Player.List` reaches the host, for which the hint send throws,
  aborting the loop; iterate `Player.ReadyList` (it excludes the host but includes dummies, which
  cost serialisation and receive nothing).
- Check also: send only on change plus a keep-alive resend before expiry (the keep-alive must
  bypass the change check); keep-alives staggered instead of all firing in one frame after a
  restart; empty "clear" hints only to players who were shown one; player-controlled text
  sanitised (`{`/`}`, length); one HUD owner per server (the client has one hint slot per player,
  verified (client)). Pixel placement scales with the window height; at other aspect ratios only the area left of the container moves. Details:
  scpsl-hints-and-hud.

## 7. Cross-round leaks after a scene reload

- Grep `static` fields holding `Player`, `ReferenceHub`, `GameObject`, `Door`, `Room`, `Pickup`;
  dictionaries keyed by `PlayerId` or `NetId`; coroutines started without a kill on round restart;
  `+=` inside per-round or per-player code; `Server.Host` cached.
- Why: a round restart is a full scene reload. Every hub, wrapper, room, door and pickup is
  destroyed and rebuilt; statics, static event subscriptions, Harmony patches and MEC's `Timing`
  object survive. `Left` fires for every non-host player at each restart; `RoundRestarted` can
  fire twice and is not raised by `softrestart`. `PlayerId` is recycled.
- Fix: rebuild per-round state at `WaitingForPlayers`; key persistent data by `UserId`; kill
  coroutines by handle or tag at restart; make `Joined`/`Left`/`RoundRestarted` idempotent;
  treat restart-driven `Left` as a session end, not a real disconnect. Details: scpsl-game-lifecycle.

## 8. Threading misuse — Lint: SL016

- Grep `Task.Run`, `new Thread`, `ThreadPool`, `Timer`, `ConfigureAwait(false)`, `async void`,
  and what those bodies touch: `Player`, `Server`, `Round`, `Map`, `Timing`, `transform`,
  `NetworkServer`, hint sends.
- Why: game state, Mirror, LabAPI wrappers and MEC have no locks; even `Player.Get` writes to a
  dictionary on a miss. Thread-safe: LabAPI `Logger`, `MainThreadDispatcher.Dispatch`,
  `SynchronizationContext.Post`. An `async void` handler's code after the first `await` runs after
  the event has returned, so `IsAllowed` set there does nothing. `PreAuthenticating` and
  `PreAuthenticated` run on a background connection thread.
- Also: the game's `MainThreadDispatcher` drains with no try/catch, so wrap posted actions.
- Fix: snapshot plain data on the main thread, work off-thread, post results back.

## 9. Timing and coroutines — Lint: SL004, SL006, SL005

- `Timing.KillCoroutines()` with no argument kills every coroutine on the server, the game's too
  (SL006). Require handles or a plugin-unique tag.
- Update-segment MEC waits run on scaled, clamped `Time.deltaTime`: about 100x slower in idle mode
  (`timeScale` 0.01), and time is lost on frames longer than `maximumDeltaTime`. A 30 s heartbeat
  can take ~50 min on an empty server (idle mode needs an empty server; any player or dummy blocks it). Wall-clock work belongs on `Segment.SlowUpdate` or
  Stopwatch deadlines (`SlClock`).
- A coroutine that throws ends for good (MEC only logs it). Code before the first `yield` runs
  inside `RunCoroutine` (Update, FixedUpdate, LateUpdate and SlowUpdate segments); an exception there
  is also only logged. Require try/catch inside the loop body, with the `yield` outside it.
- `Server.Tps` used for decisions or alerts (SL004): smoothed, clamped, scaled; it hides hitches
  and cannot read below about 3. Also: the game's `server_tickrate` is overridden to 60 after the host
  starts, while `Server.MaxTps` still returns the configured value. A threshold hard-coded to 60 or
  to the config value can be wrong; derive it from `Application.targetFrameRate` or from measured
  frame time.

## 10. Harmony conflicts — Lint: SL017 (narrow: four named targets only)

- Grep `UnpatchAll(` (with no id, or `null`), `Unpatch(` with `"*"` or no owner, `PatchAll()`
  called more than once, `HarmonyPrefix` returning `false`, patches on `XxxEvents.OnX`, on
  `EventManager.InvokeEvent`, on tiny getters, transpilers that index by position.
- Why: `UnpatchAll(null)` removes every owner's patches. A prefix returning `false` skips the
  original and every transpiler-injected event in it (position sync, voice, radio are crowded).
  A false prefix on an `On...` invoker disables that event for every plugin. Methods under 20 bytes
  of IL can be inlined into callers compiled before the patch, which then bypass it. An exception
  inside a per-pair patch propagates into game code. Registration has no dedup.
- Fix: one Harmony instance with a stable own id, `UnpatchAll(ownId)`; postfix or transpiler when
  only observing; transpilers at `Priority.Last`, matched by operand with an exact count, original
  IL fingerprinted and refused on mismatch; try/catch inside per-pair patch bodies; check
  `GetPatchInfo` at `Enable` and `WaitingForPlayers`, never per frame. Details: scpsl-harmony-patching.

## 11. Lifecycle and handler robustness — Lint: SL015

- `Enable()` order: fallible work (config, IO, reflection) first, seed state, start clocks, and
  subscribe last. A throw midway leaves earlier subscriptions live; a catch that swallows the
  exception makes the loader log "Successfully enabled". Require undo plus rethrow.
- `Enable()` must be idempotent: a LabAPI plugin that EXILED loads from its own folder can get
  `Enable()` again on EXILED's plugin reload, with no `Disable()` in between. Registering the same `CustomEventsHandler` instance twice throws and leaves
  one stale subscription.
- `Disable()` is never called by LabAPI or the game. State flushed only there is lost on every
  stop (SL015); flush in `ServerEvents.Shutdown` (synchronous; the process quits within about 2 s)
  and on a timer.
- Every handler and every clock job in its own try/catch with the plugin's own log prefix.
  LabAPI catches per subscriber, but its error header names the last-registered subscriber, not
  the thrower; one try/catch around a whole tick loop lets one bad player abort the rest.
- Cancellation: `ev.IsAllowed = true` or `= !cond` silently undoes an earlier plugin's cancel.
  Only assign `false`. `SetRole` inside `ChangingRole` (or damage inside `Hurting`) re-enters the
  same event; require a guard flag.

## 12. Config and deploy traps — Lint: SL013, SL014

- Does it compile against the server's Managed assemblies? `using UnityEngine;` together with
  `using LabApi.Features.Console;` and a bare `Logger.` call is an ambiguous reference (CS0104),
  because `UnityEngine.Logger` is a public class. This is standard C# name resolution, not an
  engine fact; confirm by building. Fix with an alias (`using Log = LabApi.Features.Console.Logger;`).
- `RequiredApiVersion => new Version(LabApiProperties.CompiledVersion)` is vacuous: that value is
  computed at runtime from the running `LabApi.dll`, so the check never fires. Use a literal.
- Cached `Config` or values derived from it go stale on `labapi reload configs` (the object is
  replaced). An empty or comment-only `config.yml` yields a null `Config`.
- `Vector4` in config (SL014), `Assembly.Location`/`CodeBase` (SL013: empty for byte-loaded plugins).
- Shipping `LabApi.dll`, game assemblies, a second copy of `0Harmony.dll`, or a "MEC.dll" (MEC
  lives in `Assembly-CSharp-firstpass.dll`) next to the plugin; every `*.dll` under the plugin folders is
  loaded. Details: deploying-scpsl-plugins.
- Every config option has a reader. An option documented as doing something that no code does
  (a `Debug` flag promising periodic counters with no code that prints them) is a finding.

## 13. Feature risk

- Enforcement features (visibility hiding, anti-ESP, anti-cheat kicks, auto-bans) must ship with
  a shadow mode first: compute but do not apply, log the would-be action rate and audit a sample
  of decisions. For visibility hiding the top risk is an enemy hidden while in a real sightline
  (windows, grates, galleries), found only by such an audit. See scpsl-networking-for-plugins.
- Ask what happens on a full server, at round start, at detonation, after a round restart, and in
  idle mode. A plugin tested with 3 players has not been tested.

## Example: what "guarded" looks like

```csharp
using System;
using System.Diagnostics;
using LabApi.Events.Arguments.PlayerEvents;
using Log = LabApi.Features.Console.Logger;

public sealed class HurtCounter
{
    private long _calls, _ticks, _errors;

    public void OnHurting(PlayerHurtingEventArgs ev)
    {
        long t0 = Stopwatch.GetTimestamp();
        try { Count(ev); }
        catch (Exception e) { if (_errors++ < 5) Log.Error("[HurtCounter] Hurting: " + e); }
        finally { _calls++; _ticks += Stopwatch.GetTimestamp() - t0; }
    }

    // Called once per second from one clock job: one log line, not one per event.
    public void Report()
    {
        double us = _ticks * 1e6 / Stopwatch.Frequency;
        Log.Info($"[HurtCounter] {_calls} calls/s, {us:F0} us/s, {_errors} errors");
        _calls = _ticks = 0;
    }

    private void Count(PlayerHurtingEventArgs ev) { /* plugin logic */ }
}
```

## Re-verifying on a new build

- Re-run `scpsl_lint.py --list-rules` and the lint tests; check `tools/catalog/labapi-events.json`
  was regenerated for the new build (hot list, never-raised list, ignored setters).
- LabAPI source (`LabApi/Events/EventManager.cs`): does `InvokeEvent` still call
  `GetInvocationList` per raise, wrap each subscriber in try/catch, and pass the multicast
  delegate to `FormatErrorMessage`?
- `LabApi/Features/Console/Logger.cs`: still no level gate, still `GetCallingAssembly` per call?
- `LabApi/Loader/PluginLoader.cs`: any new caller of `Disable()`, any change to enable-failure
  handling; `LabApiProperties.CompiledVersion` still runtime-computed?
- `LabApi/Features/Wrappers/Server/Server.cs`: `Tps` formula unchanged?
- Game build: round restart still a single-mode scene reload (watch `Left` at restart); MEC
  Update-segment time base; Mirror still overriding the configured tickrate after host start;
  voice frame size and sample rate; hint send path still per-recipient.
- Harmony version shipped in `LabAPI/dependencies`; Mono inline size limit.
