# Review report format

Verified against LabAPI 1.1.7 / SCP:SL 14.2.7 (2026-10).

A review is a list of findings sorted by severity, then by rate class. Each finding stands on its
own: a reader who opens only that entry knows what is wrong, how sure you are, and what to change.

## Report header

```
Plugin: <name> <version> (commit/file hash)   Built against: LabAPI 1.1.7 Managed assemblies: yes/no
scpsl-lint: <N errors, M warnings>, rules disabled: <none | list + reason>
Measured: <what, where: test copy with K dummies / production A/B, how long> | nothing measured
Not checked: <e.g. client-side hint rendering, behaviour with EXILED loaded, Lua scripts bound at runtime>
```

"Not checked" is mandatory. A gap you name is worth more than a gap you hide.

## Finding

File names, lines and numbers in the examples below are illustrative.

```
[S2-major] F3  Plugin.cs L212  OnVoice logs every voice packet
Rate:      SendingVoiceMessage, 100 raises/s per speaker -> ~1 000/s at 10 speakers
Evidence:  Plugin.cs L212 calls Log.Debug($"voice {p.Nickname}") with no flag check; lint SL008;
           measured 1 040 lines/s in the console log during a 10-speaker test round
Fix:       log the start of a speaking session only (edge: gap > 2 s), behind a config flag
Confidence: measured | traced | suspected
```

- **Rate**: event or loop, its frequency class, and the arithmetic at 60 players. Without a rate
  the severity is a guess.
- **Evidence**: file and line plus one of: lint rule id, a code path you traced (handler -> helper ->
  blocking call), or a measurement with its setup. Quote the plugin's code, never decompiled
  game code.
- **Fix**: concrete and local ("move the subscription after the config load and rethrow"), with
  the skill that explains it. Not "consider optimising".
- **Confidence**: `measured` (numbers from the target or a test copy), `traced` (code path plus a
  verified rate from the event catalog), `suspected` (plausible, not shown; say what would
  confirm it). Never present an estimate as a measurement.

## Severity

| Severity | Meaning | Typical findings |
|---|---|---|
| S1-critical | Can stall or freeze the server for everyone, lose data, or break other plugins | blocking IO on the main thread; non-trivial work on per-pair events; throwing on any hot event; process-wide `FirstChanceException`; forced verbose logging; `Timing.KillCoroutines()`; `UnpatchAll(null)`; a `false` prefix on a shared hot method or an `On...` invoker; game objects touched off-thread |
| S2-major | Measurable per-frame cost, a player-visible bug, or a build failure | logging or allocation on per-packet / per-frame events; per-frame `Player.List` scans or hint sends; refresh-all hint storms; cross-round leaks; `IsAllowed = true` undoing others' cancels; assignments to ignored setters; flush only in `Disable()`; does not compile (e.g. CS0104 on `Logger`); enforcement feature with no shadow mode |
| S3-minor | Bounded cost or a robustness gap | missing per-handler try/catch on per-action events; subscribe-before-seed in `Enable()`; LINQ in a per-action handler; vacuous `RequiredApiVersion`; dead config options; `Server.Tps` in a status line |
| S4-info | Style, docs, a cheaper alternative with no measurable win | one `CallDelayed` per rare event; lambdas in per-round handlers |

Escalate one level when the pattern fires on a per-pair or per-packet event, or when it can take
down other plugins (shared MEC, shared Harmony registry, shared hint slot). Do not escalate on
code volume: one line on `ValidatedVisibility` outranks a 500-line per-round handler.

## Worked example: the diagnostic block

A plugin carried a "diagnostics" block meant for debugging. It forced the verbose log level on
regardless of config, and it subscribed a handler to `AppDomain.CurrentDomain.FirstChanceException`.
With this block, reported TPS can fall from 60 to about 10 at 3 players.

Why player count did not protect the server:

- A first-chance handler runs for every exception thrown anywhere in the process, before any
  `catch` is searched. That includes exceptions the game and other plugins throw and catch as
  normal control flow. The plugin pays for code it does not own.
- LabAPI's `Logger` has no level gate. Every line pays a stack walk, an `AssemblyName`
  allocation, two regex passes and one dispatcher closure per console output. Forcing verbose on
  turns every debug call site into that cost.
- Both costs scale with events and exceptions per second, not with players. A near-empty server
  is no protection, and a test with 3 players is not a performance test.
- The ~10 figure came from the TPS readout, which is smoothed and clamped, so individual frames
  were likely worse. Measure frame time before quoting it (measuring-scpsl-performance).

As a finding:

```
[S1-critical] F1  Diagnostics.cs L30-58  Process-wide FirstChanceException hook + forced verbose logging
Rate:      every exception thrown in the process (game + all plugins) and every debug log site
Evidence:  Diagnostics.cs L31 AppDomain.CurrentDomain.FirstChanceException += OnFirstChance (lint SL009);
           Diagnostics.cs L40 sets the plugin's Verbose = true unconditionally (config ignored);
           production: reported TPS 60 -> ~10 at 3 players while the block was active
Fix:       delete the hook; catch at the plugin's own entry points (handlers, clock jobs) instead;
           verbose off by default, read from config at use time, rate-limited (RateLimitedLog)
Confidence: measured (production symptom) + traced (both mechanisms in code)
```

Review rule that follows: grep every review for `FirstChanceException`, `UnhandledException`,
hard-coded `Debug`/`Verbose = true`, and diagnostics that cannot be switched off without a rebuild.

## Confirming a suspicion

Pick the cheapest test that can falsify it (details: measuring-scpsl-performance):

| Suspicion | Test |
|---|---|
| A handler is expensive | count calls/s and summed Stopwatch time/s inside it, reported once per second (see the example in review-checklist.md); compare with the rate you predicted |
| A feature costs frame time | `SlKit.FrameProfiler`: p50/p95/p99/max frame period and busy time, % frames > 50 ms, with the feature on and off; on a test copy 60 s on / 60 s off, repeated; in production per-round A/B (many rounds per arm, bucketed by player count), judged by p95 frame time and % frames > 50 ms. Voice and inbound traffic cost can only be judged in production |
| Hitches every N seconds | FrameProfiler CSV with `Mark()` around the suspect job; look for max-period spikes at that period |
| Log storm | lines per second in the LocalAdmin log, by prefix |
| Who subscribes to hot events | `SlKit.HotEventAudit.Log()` at `WaitingForPlayers` |
| Cross-round leak | collection sizes logged at each `WaitingForPlayers`; they must not grow round over round |
| GC pressure | `GC.CollectionCount(0)` delta per second next to frame-time spikes |

Dummies (`DummyUtils.SpawnDummy`) load position sync and serialization like real players but
produce no network input, voice or join cost. Report that gap with any dummy-based number. A dummy
also blocks idle mode and, as the only "player", can keep a round from ending: lock the round.

## Re-verifying on a new build

- Re-check the severity table against the current lint rule severities (`scpsl_lint.py --list-rules`).
- Re-run the hot-event list from `tools/catalog/labapi-events.json`; a new hot event belongs in
  the S1 row.
- Confirm `Logger` still has no level gate and still resolves the calling assembly per call
  (LabAPI `Logger.cs`); if a gate appears, forced verbose logging drops from S1 to S2.
- Confirm SlKit member names used here (`FrameProfiler`, `HotEventAudit`, `RateLimitedLog`)
  against `tools/SlKit/README.md`.
