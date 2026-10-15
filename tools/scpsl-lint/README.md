# scpsl-lint

A dependency-free (Python 3, standard library only) static scanner for C# SCP:SL server plugins.
It flags performance and correctness traps that were verified against **LabAPI 1.1.7 / game 14.2.7**.

It is a text scanner, not a compiler. It masks comments and string literals, builds a small
brace-structure model (types, methods, lambdas, loops), and then asks, for each suspicious call,
whether it sits inside an event handler, a per-frame method, a loop over players, or an off-thread
lambda. Event facts (frequency class, `hot`, `burst`, `conditionallyHot`, `ignoredSetters`, `conditionallyIgnoredSetters`) are read at runtime from
[`../catalog/labapi-events.json`](../catalog/labapi-events.json), so a catalog update changes the
linter's behaviour without touching the code.

## Usage

```
python3 scpsl_lint.py <path...> [--json] [--min-severity info|warn|error]
                      [--catalog FILE] [--disable SL015,SL017] [--list-rules]
```

- Paths are `.cs` files or directories (searched recursively; `bin/`, `obj/`, `.git/` are skipped).
  Scan the whole plugin project in one call: SL015 looks at the project as a whole.
- Text output is one line per finding: `file:line:col: severity SLxxx: message`.
  `--json` prints a JSON array of `{rule, severity, file, line, column, message}`.
- Exit code: `0` no errors, `1` at least one **error**-severity finding (also when
  `--min-severity` hides it from the output), `2` usage problem (missing path, unreadable catalog).

### Suppressing a finding

Put a comment in the C# source, on the same line or alone on the line above:

```csharp
// scpsl-lint: ignore SL004 -- comparison column in the profiler CSV, never used for decisions
double tps = Server.Tps;
```

`// scpsl-lint: ignore` (no id) silences every rule for that line;
`// scpsl-lint: ignore-file SL010` silences a rule for the whole file.

### Tests

```
python3 -m unittest discover -s tools/scpsl-lint/tests
```

Every file in `tests/fixtures/` is linted on its own and compared with its inline
`// expect: SL001 SL008` markers (exact match, so a missing finding and an extra finding both fail).
`clean_plugin.cs` carries no markers and guards against false positives; `tricky_lexing.cs` guards
the comment/string masking. `tests/test_hot_and_contexts.py` holds the snippet tests for the catalog
classification, SL001/SL018/SL019, `using static` resolution and the SL007 contexts, and checks that
the repository's own C# (`tools/hot-bench`, `tools/SlKit`, `tools/sl-probe`) reports no errors.

### Event classes

The catalog (`labapi-events.json`) carries three flags per event, derived from the frequency class and
never set by hand (the generator is the only place that decides):

| Flag | Meaning | Examples |
|---|---|---|
| `hot` | raised every frame, per observer x target pair, or per packet at high rate (> ~100/s at 60 players) | `ValidatedVisibility`, `ReceivingVoiceMessage`, `TriggeringTesla` |
| `burst` | raised once per shot, bullet hit, hitmarker or damage application; spikes in a firefight, idle otherwise. All such events are classed alike | `ShootingWeapon`, `ShotWeapon`, `PlacingBulletHole`, `SendingHitmarker`, `Hurting`, `Hurt` |
| `conditionallyHot` | the row has `hotConditions`: cheap normally, per-frame in the listed situations | `Hurting` (SCP-3114 strangling, MicroHID fire, damage-over-time ticks), `Hurt` |

How the linter treats them: a subscription to a `hot` or `conditionallyHot` event is the warning SL001;
per-raise work inside the handler of a `hot` or `conditionallyHot` event is the error SL018, and the
same work in a `burst` handler is the warning SL019. A burst-only event has no SL001.

## What counts as a "handler"

- **Static style**: `PlayerEvents.Hurting += OnHurting;`, with a method group, a lambda
  (`ev => ...`, `(PlayerHurtingEventArgs ev) => ...`), an `delegate` expression or
  `new LabEventHandler<T>(OnHurting)`. The handler body is the lambda, or the method of that name
  in the same file. Three spellings of the event name are recognised: `PlayerEvents.Hurting += ...`
  (also fully qualified), an alias (`using PE = LabApi.Events.Handlers.PlayerEvents;` then
  `PE.Hurting += ...`) and a static import (`using static LabApi.Events.Handlers.PlayerEvents;` then
  a bare `Hurting += ...`). Imports are resolved per file; a bare name that two imported classes both
  declare is skipped (C# would not compile it), and `other.Hurting += ...` is never an event.
- **`CustomEventsHandler` style**: `public override void OnPlayerHurting(PlayerHurtingEventArgs ev)`.
  The override name is `On{Category}{Event}` (category = handler class minus `Events`), derived from
  the catalog. Registering the handler with `CustomHandlersManager.RegisterEventsHandler` subscribes
  every overridden method, so an override is a subscription.

A *per-frame path* is a method named `Update`/`LateUpdate`/`FixedUpdate`/`OnUpdate` with no
parameters, or the body of a `while`/`for`/`do` loop that contains
`yield return Timing.WaitForOneFrame` (or `yield return 0f`). If that body also contains an obvious
throttle (`Time.time`, `Stopwatch`, `DateTime.UtcNow`, a `%` modulo, ...) it is not treated as
per-frame, which keeps SL011 and SL012 quiet for already-throttled code.

## Rules

Each rule lists its id, severity, what it detects, why (the verified fact), the fix, and the skill
that explains it. Skill names come from `docs/design.md`; the ones not written yet are marked
*planned*.

### SL001

- **Severity**: warn
- **Detects**: a subscription (static `+=`, including `using static` and alias spellings, or a
  `CustomEventsHandler` override) to an event the catalog marks `hot: true` or `conditionallyHot: true`.
  The message includes the frequency class (`per-frame-per-pair`, `per-frame`, `high-rate-network`) and,
  for a conditionally hot event, the first sentence of its `hotConditions` (parenthetical asides left out). A burst-only event (per shot / per hit)
  is not reported here.
- **Why**: hot events are raised per frame, per observer x target pair, or per voice packet.
  `PlayerEvents.ValidatedVisibility` is raised from `FpcServerPositionDistributor.WriteAll` for every
  receiver x target pair on every position tick, so the rate grows with the square of the player
  count (players x (players - 1) x tick rate; see the benchmark README for the figures). `ReceivingVoiceMessage` runs once per voice packet per listener, and the
  game allocates its event args for every listener even with no subscriber. The handler runs on the
  main thread at that rate. **The subscription itself is not the main cost**: the benchmark in
  [`../hot-bench/README.md`](../hot-bench/README.md) measured a counter-only handler as cheap and a
  handler that allocates and looks things up on every raise as clearly more expensive. Per-raise
  work dominates, which is SL018; that is why this rule is a warning and not an error. The numbers,
  the method and what was not measured live only in that README, so they are not repeated here.
- **Fix**: keep the handler trivial and gated: one early-return flag or set lookup, no allocation, no
  logging, no iteration. Subscribe only while the feature is active and unsubscribe afterwards.
  `HotEventAudit` in SlKit lists who is subscribed at runtime. Where the subscription is deliberate and
  measured (a benchmark), say so with `// scpsl-lint: ignore SL001 -- reason`.
- **Skill**: `labapi-events`, `designing-scpsl-plugins` (*planned*).

### SL002

- **Severity**: warn
- **Detects**: a subscription to an event whose catalog frequency is `never-fired`.
- **Why**: LabAPI declares the event, but the game build has no call site that raises it
  (`PlayerEvents.SearchedAmmo`, `SearchedArmor`, `ServerEvents.CassieAnnouncing` and
  `CassieAnnounced` in 1.1.7 / 14.2.7). The handler never runs and nothing reports that.
- **Fix**: use an event that is actually raised (the catalog's `pair` column often names it), or a
  Harmony patch if there is no event.
- **Skill**: `labapi-events`.

### SL003

- **Severity**: warn for a setter the game always ignores, info for one it ignores only in some situation
- **Detects**: an assignment (`=`, `+=`, ...) to a setter the catalog lists for the event, inside a handler
  of that event. Two lists, two severities:
  - `ignoredSetters` (names only, ignored in every situation) gives the warning: `ev.DamageHandler = ...`
    in `Dying` and `SpawningRagdoll`; `ev.IsIntercom = ...` in `Muting`/`Unmuting`; and so on.
  - `conditionallyIgnoredSetters` (`[{setter, when}]`, ignored only when `when` holds) gives an info
    finding whose message quotes the condition ("ignored when ..."): `ev.DamageHandler = ...` in `Hurting`,
    `ev.Duration = ...` in `UpdatingEffect`, `ev.Priority = ...` in `Scp127Events.Talking`,
    `ev.IsAllowed = ...` in `SendingVoiceMessage`, and so on. The linter cannot tell whether your handler
    can reach the situation, so it points at the catalog text and leaves the decision to you.
- **Why**: the game consumes the local variable, never the args property. In `PlayerStats.DealDamage`
  the damage handler that is applied is the local parameter, not `e.DamageHandler`, so reassigning the
  property compiles, looks right and does nothing. Only mutating the *same* object (cast to
  `StandardDamageHandler` and change `Damage`) has an effect, because the args hold the same
  reference.
- **Fix**: mutate the existing object in place, or cancel with `IsAllowed = false` and apply your own
  effect.
- **Skill**: `labapi-events`.

### SL004

- **Severity**: error
- **Detects**: any read of `Server.Tps`, and the `tps` console command run from code
  (`Server.RunCommand("tps")`, `ExecuteCommand("tps")`).
- **Why**: `Server.Tps` is `Math.Round(1f / Time.smoothDeltaTime)`, and the `tps` command prints the
  same expression. The delta is smoothed, clamped by `maximumDeltaTime` and scaled by `timeScale`,
  so isolated 100-300 ms hitches barely move it, it cannot drop much below about 3, and in idle mode
  (`timeScale = 0.01`) it is meaningless. A server can show 58-60 while players rubber-band.
- **Fix**: measure wall-clock frame period and busy time with a `Stopwatch` pair in PlayerLoop
  insertions (`SlKit.FrameProfiler`), or read Mirror's `NetworkServer.actualTickRate` and
  `fullUpdateDuration.average`. Keep `Server.Tps` only as a labelled comparison column and mark it
  with `// scpsl-lint: ignore SL004`.
- **Skill**: `measuring-scpsl-performance` (*planned*).

### SL005

- **Severity**: warn
- **Detects**: `Timing.RunCoroutine*`, `Timing.CallDelayed`, `Timing.CallPeriodically` or
  `Timing.CallContinuously` (a) inside a handler of an event whose frequency is `per-action` or
  hotter, or (b) inside a `foreach` over players (`Player.List`, `Player.ReadyList`,
  `ReferenceHub.AllHubs`, a `Player`/`ReferenceHub` loop variable, or a collection named `*players`).
  Per-lifecycle, per-round, once and on-request handlers are deliberately not flagged: one welcome
  coroutine per join is cheap and idiomatic. Inside a handler of a hot, conditionally hot or burst event
  the same call is reported by SL018 / SL019 instead (one finding per line, not two).
- **Why**: each call registers a coroutine/timer entry (closure plus MEC bookkeeping), so the cost
  scales with events x players. Update-segment waits are also scaled by `timeScale` (about 100 x
  slower in idle mode) and clamped, so a per-event timer is not even a reliable wall-clock delay.
- **Fix**: one clock (`SlClock.Every`/`EveryStaggered`, or a single coroutine) that walks your own
  data structure of pending work; for wall-clock delays use a `Stopwatch` deadline or
  `Segment.SlowUpdate`.
- **Skill**: `scpsl-main-thread-and-timing` (*planned*).

### SL006

- **Severity**: error
- **Detects**: `Timing.KillCoroutines()` with no arguments (also `Timing.Instance.KillCoroutines()`).
- **Why**: the no-argument overload reallocates all coroutine arrays and clears every dictionary of
  the single process-wide `Timing` object, killing every coroutine on the server, other plugins' and
  the game's own included. The game itself only kills by handle.
- **Fix**: keep the `CoroutineHandle` and `KillCoroutines(handle)`, or tag your coroutines with a
  plugin-unique tag and `KillCoroutines("my-plugin")`.
- **Skill**: `scpsl-main-thread-and-timing` (*planned*).

### SL007

- **Severity**: error
- **Detects**: a blocking call on the main thread, and not inside a `Task.Run`/`Thread` lambda:
  `.Result` on a task, `.Wait()`/`.WaitOne()`/`.WaitAll()`, `.GetAwaiter().GetResult()`,
  `new WebClient()` (also `new System.Net.WebClient()` and `new global::System.Net.WebClient()`),
  `HttpWebRequest.GetResponse()`, `Thread.Sleep` (also `System.Threading.Thread.Sleep`),
  `Thread.Join()`, `File.ReadAll*/WriteAll*/AppendAll*` (also `System.IO.File.*`).
  (`.Result` is only flagged on a task-looking receiver: a call result, a variable declared as a
  `Task`, or a name like `*task*`/`*request*`; a plain `ev.Result` property is not.)
  The main-thread contexts are:
  - event handlers (static subscription, lambda, method group, `CustomEventsHandler` override);
  - `Update`/`LateUpdate`/`FixedUpdate`/`OnUpdate` and MEC coroutines (`IEnumerator<float>`);
  - console and Remote Admin commands: `ICommand.Execute`, `ParentCommand.ExecuteParent` and any
    `Execute*` method of a class that implements `ICommand`/`ParentCommand`/`CustomCommand` or carries
    `[CommandHandler]`, or that takes an `ICommandSender`;
  - lambdas and method groups passed to the scheduler: `SlClock.Every`, `SlClock.After`,
    `SlClock.EveryStaggered` (the source and the per-item callback), `MainThread.Post`,
    `MainThreadDispatcher.Dispatch`, `Timing.CallDelayed/CallPeriodically/CallContinuously`, and the
    `onMainThread` / `onError` callbacks of `Background.TryRun` (its first argument is the off-thread
    part and is not flagged).
  `new System.Threading.Thread(...)`, `new System.Threading.Tasks.Task(...)` and the qualified
  `Task.Run` count as off-thread, like their short forms.
  - **Same-file helpers**: a method defined in the same file and called from a main-thread context,
    directly or through up to three further levels of same-file calls, is itself a main-thread context.
    The finding names the path (`Save() is reached from Execute`, or
    `... (Execute -> A -> B -> C)` for a longer chain). Followed calls are a bare `Name(...)`, `this.Name(...)`
    (resolved to a method of the calling type or an enclosing type) and `Type.Name(...)` for a type
    declared in the file; `other.Name(...)` is not followed because the receiver's type is unknown.
    Nothing is propagated into `Task.Run` / `new Thread` / `Background.TryRun` work, or into a method
    that is itself handed to one of those.
  - **Every call is reported**: two blocking calls on one line are two findings, each at its own
    column. A `// scpsl-lint: ignore SL007` comment still silences the whole line.
  - **Not reported**: the `*Async` file APIs (`File.WriteAllTextAsync`), and blocking IO in a handler of
    `ServerEvents.Shutdown` or in a helper only that handler reaches. SL015 asks for a synchronous flush
    there because the process is about to quit and there is no frame left to stall.
- **Why**: the handler runs inside the game's frame, so a blocking call stalls every player for its
  full duration. Waiting for a worker that needs the main thread (for example through the game's
  dispatcher) is a deadlock, because only the main thread drains the dispatcher queue. The game's
  own synchronous `UnityHttp.Get` blocks the main thread in a sleep loop; that is the anti-pattern,
  not a precedent.
- **Fix**: use async I/O with `ConfigureAwait(false)` or `Background.TryRun`, never wait for the
  result, and post it back with `MainThread.Post`. Read files at `Enable()` time, not per event.
- **Skill**: `scpsl-main-thread-and-timing` (*planned*).

### SL008

- **Severity**: warn
- **Detects**: `Logger.Debug/Info/Raw` (or `Log.*`) (a) inside a handler of a `per-action` or hotter
  event, (b) inside any loop, or (c) in a per-frame path; and any level, including `Warn`/`Error`,
  inside a loop over players or a per-frame path. In a handler of a hot, conditionally hot or burst
  event, logging is reported by SL018 / SL019 instead; (a) keeps covering the other `per-action` events.
- **Why**: `Logger` has no level gate (`Debug(object, bool canBePrinted = true)` is only a caller
  flag). Every call does `Assembly.GetCallingAssembly()` plus `GetName()` (a stack walk and an
  allocation), then two regex passes in `ServerConsole.PrintFormattedString`, one dispatcher closure
  per console output, and the string interpolation has already run. A handler exception storm of
  about 5000 warnings per second cost 26-155 ms of CPU per second (measured).
- **Fix**: guard with your own flag *before* building the message, log a rate-limited summary
  (`RateLimitedLog`), or do not log in the hot path.
- **Skill**: `scpsl-engine-cost-model` (*planned*).

### SL009

- **Severity**: error
- **Detects**: `AppDomain.CurrentDomain.FirstChanceException += ...`.
- **Why**: the handler runs for every exception thrown in the AppDomain, before any `catch` is
  searched, including exceptions the game and other plugins throw and handle themselves. On a server
  that throws routinely it becomes a permanent tax on code you do not own.
- **Fix**: catch where you call, or wrap your own entry points (handlers, coroutines) in try/catch.
- **Skill**: `scpsl-main-thread-and-timing` (*planned*).

### SL010

- **Severity**: error
- **Detects**: `GC.Collect()` (any overload).
- **Why**: the server runs the Boehm GC (non-moving, incremental, 3 ms slices), where mark cost
  scales with the live heap and the allocation rate decides how often it runs. A forced collection
  is a stop on the main thread that the collector would have spread out. The game already collects at
  scene load and before a round restart; a plugin should not add more.
- **Fix**: remove the call and cut per-frame allocations instead (no LINQ, closures or params arrays
  in hot paths).
- **Skill**: `scpsl-main-thread-and-timing` (*planned*).

### SL011

- **Severity**: warn
- **Detects**: `Player.List`, `Player.ReadyList`, `Player.GetAll(...)` or `ReferenceHub.AllHubs`
  (except a bare `.Count`) inside a per-frame path, as defined above.
- **Why**: it is an O(N) pass every frame. `Player.List` is the raw dictionary values view (a boxed
  enumerator per `foreach`); `ReadyList` is a LINQ `Where` chain with one or two small allocations
  per call, and the predicate runs per player.
- **Fix**: poll at 1-5 Hz, stagger the work across frames (`SlClock.EveryStaggered`), or maintain
  your own collection from `Joined`/`Left`.
- **Skill**: `designing-scpsl-plugins` (*planned*).

### SL012

- **Severity**: warn
- **Detects**: `SendHint`, `ShowHint`, `SendBroadcast`, `Broadcast` (and the Broadcast component's
  `TargetAddElement`/`RpcAddElement`) inside a per-frame path.
- **Why**: each call is a network message to that player. At 60 fps with N players that is 60 x N
  messages per second, usually repeating the same text. The client has one hint slot per player, so
  each hint replaces the previous one and a repeat of the same text changes nothing on screen.
- **Fix**: send when the text changes (compare with the last sent string), cap the update rate, or
  use the client-side `HintParameter` overloads so the client updates a value by itself.
- **Skill**: `scpsl-hints-and-hud` (*planned*).

### SL013

- **Severity**: warn
- **Detects**: `Assembly.GetExecutingAssembly().Location` / `.CodeBase`, `GetType().Assembly.Location`
  and `typeof(YourType).Assembly.Location` where `YourType` is declared in the scanned source.
- **Why**: LabAPI loads plugins with `Assembly.Load(byte[])`, so `Location` is `""` and
  `Path.GetDirectoryName("")` throws `ArgumentException: Invalid path`, which fails `Enable()` (verified, server log). `CodeBase` of a byte-loaded assembly was not tested on this runtime, so
  the advice to avoid it rests on the same reasoning.
- **Not reported**: a read stored in a local that the file tests with `string.IsNullOrEmpty` /
  `IsNullOrWhiteSpace` (the guarded idiom), and a read passed straight to a method that is not
  `Path`/`File`/`Directory`/`FileInfo`/`DirectoryInfo` (the callee owns the contract; the read alone
  returns `""` and does not throw).
- **Fix**: use `Plugin.FilePath` or the plugin's config-directory helpers. `FilePath` is assigned
  after the constructor returns, so read it in `Enable()` or later.
- **Skill**: `deploying-scpsl-plugins` (*planned*).

### SL014

- **Severity**: warn
- **Detects**: a public property of type `Vector4` (also `List<Vector4>`, `Vector4[]`) in a config
  class. A class is config-like when its name ends in `Config`/`Configuration`/`Settings`/`Options`,
  it implements `IConfig`, it is the `T` of `Plugin<T>`, or any of its properties has `[Description]`.
  Properties with `[YamlIgnore]` are skipped.
- **Why**: the LabAPI 1.1.7 YAML writer for `Vector4` emits `w` from `v.z`, so the value is silently
  corrupted on the first save, and every successful load rewrites the file.
- **Fix**: use `Vector3`, `Color` (`{r,g,b,a}`), `Color32` (hex string) or a DTO with four floats.
- **Skill**: `deploying-scpsl-plugins` (*planned*).

### SL015

- **Severity**: info
- **Detects**: `override void Disable()` / `OnDisabled()` whose body does cleanup (`-=`, `Dispose`,
  `Unregister*`, `Kill*`, `Stop`, `Cancel`, `Clear`, `Save*`, `Flush`, `Close`, `Write*`) while no file
  in the scanned set subscribes to `ServerEvents.Shutdown` (or `OnServerShutdown`, `ProcessExit`,
  `Application.quitting`).
- **Why**: nothing in LabAPI or the game calls `Plugin.Disable()`; there is no unload path. The only
  notification is `ServerEvents.Shutdown`, raised synchronously at the start of `Shutdown.Quit`, which
  then waits well under two seconds before `Application.Quit()`. Data you flush only in `Disable()`
  is lost on every stop.
- **Fix**: flush and release in a synchronous `ServerEvents.Shutdown` handler and on a timer; assume a
  SIGKILL or crash fires nothing. Keep `Enable()` idempotent.
- **Skill**: `scpsl-game-lifecycle` (*planned*).

### SL016

- **Severity**: warn
- **Detects**: Unity/Mirror/LabAPI members used inside code handed to `Task.Run`,
  `Task.Factory.StartNew`, `new Thread`, `new Task`, `ThreadPool.QueueUserWorkItem` or the first
  argument of `Background.TryRun`: `Player.`, `Server.`, `Map.`, `Round.`, `Warhead.`, `ReferenceHub.`,
  `NetworkServer.`, `GameObject.`, `Physics.`, `Timing.`, `transform`, `gameObject`, `ev.Player` and
  variables named `player`/`plr`/`hub`. A method group passed to a thread (`new Thread(Worker)`) is
  followed to the method body. Calls inside `MainThread.Post(...)`, `MainThreadDispatcher.Dispatch(...)`
  or `SynchronizationContext.Post(...)` arguments are the marshalled-back part and are not flagged.
- **Why**: almost nothing in the game, Mirror, the LabAPI wrappers or MEC is thread-safe. The
  `Player` wrapper cache is a plain `Dictionary` mutated from Unity callbacks (a lookup that misses
  creates a wrapper and adds to it), Mirror's writer pools are plain stacks, and MEC has no locks.
  Only logging, `MainThreadDispatcher.Dispatch` and `SynchronizationContext.Post` are safe from other
  threads. Whether a given Unity getter throws or silently corrupts off-thread is unverified; treat it
  as undefined.
- **Fix**: copy plain data (strings, ints, structs) on the main thread, work on the snapshot, return
  results through `MainThread.Post` with try/catch around the action.
- **Skill**: `scpsl-main-thread-and-timing` (*planned*).

### SL017

- **Severity**: info
- **Detects**: a Harmony prefix (`[HarmonyPrefix]`, or a `bool` method named `Prefix`) that contains
  `return false;`, on a patch target named `WriteAll`, `ValidateVisibility`, `OnValidatedVisibility` or
  `get_CurrentRole` (including `CurrentRole` with `MethodType.Getter`). The target name is read from
  the `[HarmonyPatch]` attribute on the method or its class.
- **Why**: these methods sit on the position-sync and role paths, which several plugins patch (the
  visibility hook `PlayerEvents.ValidatedVisibility` itself is raised from `WriteAll`). A prefix that
  returns false skips the original method, so every other plugin's behaviour that depends on it
  silently stops. The rule only claims the certain part: the original is skipped.
- **Fix**: prefer a postfix or a transpiler; if you must replace the method, say so in your plugin's
  documentation and give the patch an explicit priority.
- **Skill**: `scpsl-harmony-patching` (*planned*).

### SL018

- **Severity**: error
- **Detects**: expensive work done on every raise inside a handler of a `hot` or `conditionallyHot`
  event (see Event classes). Everything found on one line is one finding that names each kind:
  - `Logger.*` / `Log.*` calls at any level;
  - string interpolation (`$"..."`), concatenation with a literal (`"a" + x`, `x += "a"`) and
    `string.Format/Concat/Join`;
  - heap allocation with `new`: `new List<T>()`, `new T[n]`, `new { ... }`, `new[] { ... }`
    (value types such as `Vector3`, `Quaternion`, `Color`, `DateTime`, `new()` and `throw new` are not
    counted as allocations);
  - LINQ calls (`Where`, `Select`, `ToList`, `Any`, `Count()`, `OrderBy`, ... but not `Math.Max` or
    `Mathf.Min`);
  - `lock` and `Monitor.Enter/TryEnter/Exit`;
  - player list enumeration: `Player.List`, `Player.ReadyList`, `Player.GetAll(...)`,
    `ReferenceHub.AllHubs` (a bare `.Count` of `List`/`AllHubs` is O(1) and allowed) and `Player.Count`
    (a LINQ count over `ReadyList`);
  - `throw`;
  - file and network IO: `File.*`, `Directory.*`, `new StreamWriter/FileStream/HttpClient/...`,
    `UnityWebRequest`, `SlHttp`, `*.GetStringAsync/PostAsync/...` (a blocking call the SL007 rule
    already reports on that line is left to SL007);
  - `Timing.RunCoroutine*` / `CallDelayed` / `CallPeriodically` / `CallContinuously`.
  A method subscribed to several events is checked at the strictest class. Two shapes are not
  reported because they are not per-raise work: the body of a `catch` (the failure path; log through a
  rate limiter there) and a `new` inside the braces of a lazy-init `if` (`TryGetValue`, `ContainsKey`,
  `== null`, `is null`), which allocates once per key.
- **Why**: the handler runs on the main thread once per raise, so its cost is multiplied by the event
  rate. [`tools/hot-bench`](../hot-bench/README.md) measured the subscription with a counter-only
  handler as cheap and per-raise allocation and lookups as the real cost (figures there), so the cost
  is mostly whatever the handler does. `Logger` has no level gate and walks the
  stack on every call; string building, allocations and LINQ feed the Boehm GC at the event rate;
  `lock` and enumerating every player scale with N on every raise; a `throw` costs an exception per raise.
- **Fix**: reduce the handler to a flag test and a dictionary or set lookup; collect the data you need
  (a counter, a set of ids) and do the real work from one clock (`SlClock.Every`). Where a rare branch
  must log or allocate (a once-per-session error), gate it behind your own flag and suppress with
  `// scpsl-lint: ignore SL018 -- reason`.
- **Skill**: `labapi-events`, `scpsl-engine-cost-model`.

### SL019

- **Severity**: warn
- **Detects**: the same work as SL018, inside a handler of a `burst` event that is neither `hot` nor
  `conditionallyHot`: `ShootingWeapon`, `ShotWeapon`, `PlacingBulletHole`, `PlacedBulletHole`,
  `PlacingBlood`, `SendingHitmarker`, `CheckedHitmarker`, shooting-target and window damage, and so on
  (`Hurting` and `Hurt` are conditionally hot as well, so they get the SL018 error).
- **Why**: a burst event is idle most of the time and then raised per bullet. A full-auto firefight on
  a full server raises it many times per second, so per-raise work adds up exactly when the server is
  busiest. It is a warning because the rate is bounded by fire rate, not by the frame.
- **Fix**: as SL018: keep the handler allocation-free and early-out; build strings and write logs
  outside it.
- **Skill**: `labapi-events`, `scpsl-engine-cost-model`.

## Known limits

- It is regex-and-brace based, so it does not resolve types. SL007 follows same-file calls up to three
  levels deep (see SL007), but not calls through another receiver, across files, through delegates
  stored in fields or through virtual dispatch; a `Player`-typed variable with an unusual name is not
  recognised inside a thread lambda (SL016).
- SL018 / SL019 look at the handler's own body only (they do not follow calls the way SL007 does): work
  done in a helper the handler calls is not seen, and a type name that is a value type but not in the scanner's list
  (`new MyStruct()`) counts as an allocation. A lambda nested in a handler (a `Timing.CallDelayed`
  callback) is scanned as part of the handler.
- A method group is resolved by name within the same file. A handler defined in another file still
  counts as a subscription (SL001/SL002), but its body is not inspected.
- Expression-bodied members, lambdas, local functions, interpolated, verbatim and raw string
  literals are handled; preprocessor `#if` branches are scanned as if all were active.
- Findings are heuristics with a stated reason. Where you have measured the cost and accept it, say so
  with a `// scpsl-lint: ignore SLxxx -- reason` comment.
