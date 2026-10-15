# Design checklist (designing-scpsl-plugins)

Verified against LabAPI 1.1.7 / SCP:SL 14.2.7 (2026-10).

Answer every item in one or two lines before writing code. An answer is a number, a named
mechanism or "not applicable, because ...". "Should be fine" is not an answer. Everything marked
*estimate* comes from code reading, not from a profiler.

## 1. Load model

Write down: max players N (use the real slot count; dummies count as receivers), concurrent
speakers S, the target frame rate, and for each trigger its catalog frequency class.

| Frequency class (catalog) | Examples | Rate to plan for | Design rule |
|---|---|---|---|
| `per-frame-per-pair` | `ValidatedVisibility`, `Scp173Events.AddingObserver`/`RemovingObserver`, `IdlingTesla` | `ValidatedVisibility`: receivers x targets x send ticks per second, about 60 x 59 x 60 = 212 000 raises/s with 60 players at 60 Hz, up to about 594 000/s with 100 (*estimates*, upper bounds: spectators are not targets) | Do not subscribe. If the feature cannot exist without it: O(1) flag or array lookup, no allocation, never throw, subscribed only while the feature is on |
| `per-frame` | `Escaping` (while anyone stands in the zone), `UsingRadio`/`UsedRadio`, `StayingInHazard`, `UsingIntercom`, `Scp106Events.ChangingVigor`/`ChangedVigor`, `TriggeringTesla` | frame rate x instances | Same as above. Prefer polling at 1-5 Hz from the clock |
| `high-rate-network` | `SendingVoiceMessage` (about 100 packets/s per speaker: 480-sample frames at 48 kHz, derived from the frame size), `ReceivingVoiceMessage` (per packet x per listener) | 10 speakers = 1000 raises/s for Sending; Receiving multiplies that by listeners | Prefer `SendingVoiceMessage`. Handler is a dictionary lookup plus a field write |
| `per-action` | doors, shots, pickups, `Hurting` | player-driven, bursty | Cheap handler, no coroutine, no log line. `Hurting`/`Hurt` turn per-frame per victim in some cases, e.g. SCP-3114 strangle and Micro-HID fire |
| `per-lifecycle` | `Joined`, `Left`, `ChangedRole`, `Spawned`, `Death` | about N per round, plus N `Left` and N `Joined` at every restart | Fine. Must be idempotent |
| `per-round` | `WaitingForPlayers`, `RoundStarted`, `RoundRestarted`, `WaveRespawned` | about 1 per round (`RoundRestarted` can fire twice) | Reset and rebuild per-round state here |
| `once` | `PluginsEnabled`, `Shutdown` | 1 per process | Wiring and final flush |
| `on-request` | commands, RA requests, nickname changes | rare | Anything reasonable |
| `never-fired` | `SearchedAmmo`, `SearchedArmor`, `CassieAnnouncing`, `CassieAnnounced` (declared, never raised); `AimingWeapon` (commented out in the LabAPI source, absent from the 1.1.7 build) | 0 | Handler never runs (or does not compile). Pick a raised event |

Per-event facts (cancel, ignored setters, hot conditions): `events-catalog.md` in labapi-events,
or grep the event name in `tools/catalog/labapi-events.json`.

Two cost facts that change the arithmetic:

- The game builds the event args object (with `Player.Get` lookups) on every raise, even with
  zero subscribers. Subscribing adds one `Delegate[]` allocation per raise (`GetInvocationList`)
  plus your handler. On a hot event, "just watching" is not free.
- The frame budget is 1000 / real frame rate. Mirror resets `Application.targetFrameRate` to its
  `sendRate` (60) at `StartHost`, so `server_tickrate` below 60 does nothing until a config reload
  or the first idle exit. Budget against what actually paces the loop: read
  `Application.targetFrameRate`. `Server.MaxTps` is the configured value and can differ from it
  until the override is undone (the `tools/tickrate-fix` plugin). Never hard-code 60. See scpsl-engine-cost-model.

Budget context (*estimate*, 100 players): the vanilla position path alone is
3-7 ms per frame. Your plugin is spending from the same budget. Pick a steady-state budget
for it (a policy, for example "well under 0.1 ms per frame") and measure it (item 12).

## 2. Events

- List each event with its class from item 1. For every hot one, write the gate or remove it.
- Pick the event that already carries the state you need: `ChangedRole` is the last event of a
  role change, after `Spawned` and the loadout events, so role, position and starting inventory are
  in place. `RoundStarted` handlers still see role `None` (roles are assigned later in the same
  frame). A late joiner gets `ChangedRole`/`Spawned` before `Joined`, with no nickname yet.
- Cancelling: write only `IsAllowed = false`. Every handler still runs and the last writer wins.
  Later subscribers get the last word: plugins enabled later (`Priority` `Low`/`Lowest`, which is
  the higher number) and anything subscribed at runtime. Ties follow file order, not a contract.
- Setters are advisory: check `ignoredSetters` in the catalog before relying on one.
- `PreAuthenticating`/`PreAuthenticated` run on the connection-request thread: args and your own
  thread-safe state only.
- Nothing that users or scripts configure may subscribe to hot events at runtime without an
  explicit opt-in and a rate limit. A scripting VM that can attach to any event puts about 594 000 script calls/s within reach of a script edit (*estimate*, 100 players at
  60 Hz, `ValidatedVisibility`).

**REQUIRED SUB-SKILL:** labapi-events

## 3. One clock

- All periodic, delayed and per-player-staggered work goes through one clock:
  `SlClock.Every`, `SlClock.After`, `SlClock.EveryStaggered<T>` (SlKit). It uses absolute
  `Stopwatch` deadlines, skips missed periods instead of replaying them, wraps each job in
  try/catch, measures each job, and takes a job out after 5 failures in a row (per job:
  `ClockJobOptions.MaxConsecutiveFailures`, passed at registration, so one plugin cannot change another's). By default (`JobFailurePolicy.Backoff`) the job comes back by itself after
  30 s, then 1, 2, 4 ... up to 30 min; `DisablePermanently` keeps it off until `Reenable()`.
- MEC's Update segment runs on scaled, clamped `Time.deltaTime`. In idle mode (`timeScale` 0.01)
  a 30 s wait takes about 50 minutes, and frame hitches longer than `maximumDeltaTime` are lost.
  Wall-clock work does not belong there.
- An exception inside a MEC coroutine ends it for good (MEC only logs it). Coroutines also
  survive round restarts, because the `Timing` object is `DontDestroyOnLoad`: kill or tag them.
- No coroutine or `CallDelayed` per player or per hot event. A `CallDelayed` per rare event
  (round start, one role change) is fine, and a handful of long-lived coroutines are cheap: don't
  rewrite them only for speed.
- Per-player periodic work (HUD, AFK checks) uses `EveryStaggered`, so 60 players are spread
  over the period instead of all landing on one frame.
- While idle (default `idle_mode_tickrate` 1) every frame-polled clock has about 1 s resolution.

**REQUIRED SUB-SKILL:** scpsl-main-thread-and-timing

## 4. Per-round state: one owner, one reset point

- A round restart is a scene reload. Every `ReferenceHub`, `Player` wrapper, `CustomDataStore`,
  room, door, pickup and ragdoll is destroyed and rebuilt. Statics, static event subscriptions,
  MEC coroutines, Harmony patches and `ClockScope.Server` SlClock jobs survive.
- Name the one object that owns per-round state. Clear or replace it at `WaitingForPlayers`
  (fires every round: new map plus host hub exist). Register round-bound jobs with
  `ClockScope.Round` (the default of `SlClock.After`); `SlClock.Install` ends them at `RoundRestarted`.
- `RoundRestarted` can fire twice, and `softrestart` does not raise it at all
  (`RoundRestart.OnRestartTriggered` sees that path). Handlers must be idempotent.
- At every restart `Left` fires for every non-host hub, then `Joined` fires again after re-auth.
  Decide what a restart means for your data. `RoundRestart.IsRoundRestarting` is true from the
  start of the restart until the new host hub starts, so a `Left` inside that window is usually
  teardown, not a disconnect. A player who really disconnects while the old scene is still loading
  looks the same. Both facts are derived from code order, not observed.
  During `Left` the hub and wrapper still exist (removal follows), so `UserId` and `Nickname` can
  be read; treat `Role` as likely readable too (derived).
- World state (rooms, doors, `Server.Host`, players) does not exist in `Enable()`. Rooms by
  coordinate and doors by room are registered after `MapGenerated`. Use `WaitingForPlayers`;
  items and lockers appear seconds later (`ItemSpawned`).
- Objects that live shorter than the server (per round, per player) must unsubscribe from static
  events when they die, or route through one static handler.

**REQUIRED SUB-SKILL:** scpsl-game-lifecycle

## 5. Keys

| Key | Lifetime | Use for |
|---|---|---|
| `Player.UserId` | stable for authenticated online-mode players | anything that outlives a round or a reconnect |
| `Player` / `ReferenceHub` reference | one hub, one round | never store across a restart; re-resolve on use |
| `PlayerId` | recycled (FIFO pool, reused once 16 or more ids are free) | short-lived per-frame arrays only, reset on join/leave and at round boundaries |
| `netId` | grows for the whole process | nothing persistent |
| `CustomDataStore` | as long as the hub (lost on rejoin) | per-life scratch data |

`UserId` caveats: offline mode gives `ID_Offline_<netId>_<timestamp>` per connection, all dummies
share `ID_Dummy`, unauthenticated hubs have none. Skip dummies explicitly.

## 6. Threads and IO

- Everything in the game, Mirror, LabAPI wrappers and MEC is main-thread-only. Even
  `Player.Get` can write to a dictionary. Off the main thread, touch only plain snapshots
  (strings, numbers, POCOs) copied on the main thread. `Logger.*` is safe from any thread.
- Pattern: snapshot on the main thread, then `_worker.TryRun(work, onMainThread, onError)` or
  `_http.PostJsonAsync(...)`, and the result comes back through `MainThread.Post`. Make both in
  `Enable`: `_worker = Background.ForOwner(Name, capacity)` and `_http = SlHttp.CreateClient(Name, options)`.
  The static `Background.TryRun` and `SlHttp.PostJsonAsync` share one default worker and one default
  client (4 concurrent, 64 queued) with every other plugin, so another plugin's backlog becomes your
  failed `TryRun` or `Rejected` result.
- Never wait on the main thread: no `.Result`, `.Wait()`, `Thread.Sleep`, synchronous HTTP or
  per-event file IO. A synchronous telemetry POST on the main thread every
  minute costs 35-50 ms per call normally, and up to a 15 s timeout when the network fails.
- Decide the failure policy as a small state machine before coding, and write it down in the
  words the code and the status text will use. Cover: queue full (`TryRun` returns `false`),
  timeout, and every status class. Do not treat all non-2xx alike:
  - Transient (no answer: `StatusCode` 0 with `TimedOut` or `Error`, or never sent: `Rejected`,
    `QueueTimedOut`; 5xx, 429): bounded retry with backoff; for 429 wait at least the server's
    `Retry-After`. Test `Success`, not `StatusCode != 0`, for failure: a failed result can carry the
    real status. `Sent == false` is always safe to resend; `TimedOut` may already have been processed.
  - Permanent (other 4xx: bad payload, revoked or deleted webhook): retrying cannot succeed.
    Stop, mark the sink broken in the status command, log once, re-arm on config reload.
  - Name terminal states by what the code does. *Deferred* = still pending, will be tried again.
    *Dropped* = never tried again. If the pending flag stays set after the last failed attempt,
    the item is deferred, whatever the comment says. Be able to answer "after the last failure,
    will this item be sent again?" with yes or no, and make state, counters and docs agree.
  - Unbounded retry is allowed only on purpose, with a rate bound (for example 3 attempts per
    10 minutes) and a stated worst case. Otherwise cap the attempts per item and clear it.
  - Count items, not attempts: keep separate counters for attempts, failed attempts and distinct
    items abandoned, so one stuck alert does not inflate "dropped" on every retry.
  - A failed send must be visible in state (counter, retry flag), not only in a log line, and
    must not consume a cooldown as if it had succeeded.
- Use your own `SlHttp.CreateClient` client and its per-request timeout (counted from the moment the
  request gets a slot). `HttpClient.Timeout` cannot be changed after the first request (throws
  `InvalidOperationException`), and SlKit has no public shared `HttpClient` to change.
- Escape every control character in hand-built JSON, or the receiver rejects it.

## 7. HUD

- Each hint is one reliable-ordered message per recipient, about `20 + text bytes` each. The
  server keeps no state about what a player sees and does not arbitrate between plugins. Vanilla
  sends its own hints (pickup warnings) on the same path.
- One HUD owner (compositor) per server. Other features hand it text.
- Send on change, plus a keep-alive resend before expiry that bypasses the change check. Hint
  duration must be longer than the refresh period, and the rate at most 1-2 Hz per player
  (a policy, not a measured limit). Validate `refresh < duration` when loading config.
- Anything that ticks every second goes into `TimespanHintParameter.FromOffset(...)` or
  `AnimationCurveHintParameter`, which the client evaluates without further traffic (a Timespan count-up
  is verified (client); the curve parameter is unverified). Keep a curve's
  `offset` constant across keep-alive resends, or the ramp restarts.
- Coalesce bursts (round start, mass death) with a per-player dirty flag; never "refresh
  everyone" per event. Stagger keep-alives with `EveryStaggered`.
- Targets: `Player.ReadyList`, skip `IsDummy`. Sending to the host throws.
- Escape `{`/`}` in player-controlled text (nicknames keep braces; what the client does with a
  stray brace is unverified, so this is cheap insurance); keep text short.

**REQUIRED SUB-SKILL:** scpsl-hints-and-hud

## 8. Logging budget

- `Logger` has no level gate. Every call walks the stack (`GetCallingAssembly`), runs two regex
  passes and allocates, and string interpolation runs before the call. A storm of about 5000
  warnings/s is estimated at 26-155 ms CPU per second.
- Budget: zero lines per hot-event raise and per loop iteration. Debug output behind your own
  config flag, checked before the message is built. Bursts become a rate-limited summary
  (SlKit `RateLimitedLog`, or `SlKit.Core.TokenBucket`, plus "N suppressed").
- With two or more subscribers, LabAPI's handler-exception log line names the last subscriber, not
  the thrower. Wrap your handlers in your own try/catch with your own message.
- Logging volume can destroy the data itself: logging every voice packet can push over 99% of the
  gameplay entries out of a bounded buffer.

## 9. Config shape

- `class` with a public parameterless constructor, public `{ get; set; }` properties with
  initializer defaults, `[Description]` for the YAML comment. Fields are ignored; get-only
  properties are written but never read. Keys are snake_case of the property name.
- The file is rewritten on every load: comments regenerated, unknown keys dropped, defaults
  added. Lists and dictionaries in the file replace the defaults. Enum values match the C# member
  name. One unparsable value makes the whole file fall back to defaults, with a log line that
  does not name your plugin.
- `labapi reload configs` calls `LoadConfigs()`, which replaces the `Config` object. Read
  `Config.X` at use time, or override `LoadConfigs()`, call `base`, and rebuild derived caches
  there. `LoadConfigs()` also runs before `Enable()` and for plugins that never enabled, so it must
  not touch state that `Enable()` creates.
- An empty or comment-only `config.yml` deserialises to `null`, so `Config` becomes `null`.
  Guard it (`Config ??= new TConfig()`).
- No `Vector4` (the writer corrupts `w`). `Plugin.Name` is the config folder: keep it constant.

**REQUIRED SUB-SKILL:** deploying-scpsl-plugins

## 10. Plugin lifecycle

- `Enable()` runs inside `ServerStatic.Awake`, before the network and the map exist. Keep it fast;
  no world access, no permission queries.
- `Disable()` is never called by LabAPI or the game, and there is no unload. Code changes need a
  process restart. Flush synchronously in `ServerEvents.Shutdown` (the process quits about
  0.6-2 s later) and on a timer; a crash or SIGKILL fires nothing, and whether a SIGTERM reaches
  the event is unverified.
- `Enable()` must be idempotent: EXILED's reload calls `Enable()` again on LabAPI plugins that sit
  in EXILED's folder, with no `Disable()` in between.
- Fallible work first, subscribe and register jobs last. On an exception, undo your own
  subscriptions and rethrow: subscriptions made before the throw stay live, and a swallowed
  exception makes the loader log "Successfully enabled".
- `RequiredApiVersion => new Version(1, 1, 7)` (a literal). `LabApiProperties.CompiledVersion` is
  computed at runtime from the running `LabApi.dll`, so using it makes the check always pass.

## 11. Kill switches

- A master `Enabled` flag and one flag per feature, read at use time, so `labapi reload configs`
  turns a feature off without a restart.
- Hot-event features subscribe only while switched on (`-=` then `+=` keeps it idempotent). A
  later subscription lands at the end of the handler list.
- Keep the `IDisposable` of every SlClock job. Decide what a job taken out by the failure breaker
  means for the feature: with the default backoff it returns on its own, so the feature must
  tolerate gaps; if a half-working job is worse than none (it writes data), register it with
  `new ClockJobOptions { FailurePolicy = JobFailurePolicy.DisablePermanently }`. Show the state in a status command.
- A status command (permission-gated) prints switches, job stats and failure counters.
- Features that change what players see, hear or hit (visibility culling, damage, anti-cheat):
  shadow mode first. Compute, do not apply, and log the would-be effect and a sampled audit.
  Where input data is missing, fail open to vanilla behaviour. Write every unverified assumption
  (API, geometry, latency margin) into the design and test it in shadow mode. A module that
  replaces vanilla logic can run both paths on a sample of calls and switch itself off, with one
  log line, when they diverge.

### 11b. Visibility filters (anti-ESP, culling): latency budget

A hidden player is parked at (0, 6000, 0) on the client, so everything attached to the model goes
silent (footsteps, proximity voice, item and SCP sounds) and the client drops voice from a speaker
it still sees parked. The filter only narrows vanilla: a pair vanilla hides stays hidden. Write
these numbers into the design, they are acceptance criteria, not tuning:

- **Reveal latency is at most 1 send tick from the trigger.** Triggers: speaking
  (`ServerIsSending`, any channel: Proximity, Radio, Scp1576), playable footstep or landing,
  shot, item sound, door or portal opening, damage, role change, noclip. Check these as O(1)
  flags on the position-send path itself (once per send tick, up to 60 Hz), set by the trigger and
  read by the same tick's decision.
- **A polled sweep is not a reveal path.** A trigger that only records a timestamp for a 10 Hz
  sweep to act on adds up to a whole sweep period of latency. A sweep may drive only the
  expensive part (multi-ray geometry, room-graph rebuilds) and the hide side, and hiding needs
  hysteresis (hide only after about 1 s without need, at most one switch per second per pair).
  Reveal early, hide late.
- **Voice shows the cost.** A speaker sends about 100 packets/s (10 ms each). Same-tick reveal
  still loses about 1-3 packets, because voice is forwarded earlier in the frame than the
  position message and the client unparks the model only afterwards. A reveal delayed by one
  sweep period loses about 10 more per 100 ms. State the expected loss in packets and test it. To
  get below 1-3, reveal before the first packet or hold the first packets about 2 ticks (about
  35 ms added at the start of an utterance only).
- **Margin formula:** `m = (v_obs + v_tgt) x (RTT/2 + 2/tickrate + decision_age) + 2 m`, with `v`
  from the role's `FpcModule.MaxMovementSpeed` (sprint speeds live in prefabs: read them at
  runtime) and `decision_age` = how old the data behind the decision can be (sweep or cache
  period plus snapshot staleness). A fixed 5 m margin assumes tick-fresh
  decisions and RTT up to 200 ms. With RTT 100 ms at 60 Hz the time term is about 0.13 s fresh and
  about 0.33 s with a 0.2 s old decision, so the speed-dependent part grows about 2.5 times. Compute `m`
  for the worst pair (both sprinting) with your own age, do not copy 5 m.
- The margin is a floor: the client interpolates a revealed player out of the parking position
  for roughly 0.7-1 s (SCP-939 and SCP-106 excepted), and a 180 degree turn takes about 0.1 s,
  so field-of-view culling is unsafe for vanilla clients (line of sight without FOV).
  Verify both in shadow mode with an audit of would-be hidden pairs that were later reachable.
- Never hide a pair that is already making sound: switching visible to hidden mid-sound cuts
  playing audio.

**REQUIRED SUB-SKILL:** scpsl-networking-for-plugins (and scpsl-voice-and-audio for voice paths)

## 12. Measurement plan (written before code)

- Metrics: frame period and busy time p50/p95/p99/max and % of frames over 50 ms
  (`SlKit.FrameProfiler`), per-job µs (`SlClock.Stats`), raises per second of each subscribed
  event, hints and bytes per second.
- Never `Server.Tps` as a pass/fail signal: it is `round(1 / Time.smoothDeltaTime)`, a smoothed and
  clamped value that hides hitches and is meaningless in idle mode (the exact filter and the
  `timeScale` scaling are unverified).
- Baseline without the plugin, then with it, same player count. Dummies are real serialisation
  receivers but generate no network, auth or voice load, so a dummy test under-measures.
- Scenarios: full server steady state, round start, round restart, mass death, idle.

**REQUIRED SUB-SKILL:** measuring-scpsl-performance

## 13. Review gate

- `python3 tools/scpsl-lint/scpsl_lint.py <project>` with zero errors; every suppression carries a
  `-- reason`.
- reviewing-scpsl-plugins before the first deploy. deploying-scpsl-plugins for build references
  and the boot-log check (`Successfully enabled '<Name>'`).

## Design note template

```
Plugin / feature:
Load: N=  S=  target fps=  budget=  ms/frame (steady)
Events: <event> - <class> - <gate or "rare">
Clock jobs: <name> - <period> - <scope> - <per-run cost estimate>
Round state owner: <class>; reset at: WaitingForPlayers; restart semantics: <...>
Keys: <UserId / ...>
Off-thread work: <what>, snapshot = <fields>
Failure policy: transient=<codes, backoff, bound> permanent=<codes, action> state names=<deferred/dropped> counters=<attempts/items>
HUD: owner=<...>, refresh=<s>, duration=<s>, client-side parameters=<...>
Logging: hot paths=0; debug flag=<...>; summaries=<...>
Config: <properties>; read at use; null guard; validation=<...>
Lifecycle: idempotent Enable; Shutdown flush=<...>
Kill switches: <flags>; shadow mode=<yes/no, why>
Visibility filter (if any): reveal latency=<ticks, trigger path>; margin=<formula with decision_age, worst-case m>; expected voice loss=<packets>
Unverified assumptions: <list>
Measurement: <metrics, scenarios, pass criteria>
Review: lint + reviewing-scpsl-plugins
```

## Re-verifying on a new build

- Regenerate the event catalog and diff the frequency classes, especially `hot` and `never-fired`.
- `Plugin<TConfig>.LoadConfigs` in LabAPI: does it still assign a new `Config` object?
  `ConfigurationLoader`: does an empty file still give `null`?
- Grep the LabAPI loader and the game for callers of `Plugin.Disable()`; check whether
  `LabApiProperties.CompiledVersion` is still `static readonly` computed at runtime.
- `Player.ReadyList`, `Player.IsReady`, `HintDisplay.Show` (host check) and `Logger`'s format path.
- MEC `Timing`: time source per segment, and whether the controller is still `DontDestroyOnLoad`.
- Where `WaitingForPlayers`, `RoundRestarted` and `Left` are raised relative to the scene reload,
  and whether `RoundRestart.IsRoundRestarting` is still set across the teardown.
- Mirror `NetworkManager.ConfigureHeadlessFrameRate`: does it still override `targetFrameRate`?
  Does the `Server.MaxTps` getter still return the configured value?
- SlKit README: API names used here (`SlClock`, `ClockScope`, `Background`, `SlHttp`,
  `FrameProfiler`, `RateLimitedLog`, `RateLimitedLogger`, `SlKit.Core.TokenBucket`; `TickrateFix` is no longer in SlKit,
  see `tools/tickrate-fix`).
