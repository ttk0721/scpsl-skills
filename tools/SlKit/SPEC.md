# SlKit — specification

A tiny, dependency-free runtime toolkit for SCP:SL LabAPI plugins (net48, compiled against the
game's own `SCPSL_Data/Managed` assemblies). It packages the patterns the research proved
correct so plugin authors do not re-derive them, does nothing behind the user's back, and changes
no server-wide setting (those belong in a plugin the server owner installs).

Isolation rule: one `SlKit.dll` serves every plugin on the server, so SlKit has no public static
setting through which one plugin can change behaviour for another. Per-plugin behaviour is given
where the plugin creates its own object (a job's `ClockJobOptions`, a client's `SlHttpOptions`, a
worker's capacity in `Background.ForOwner`). A value that is genuinely shared by all plugins is
the server owner's and comes from outside the process (the main-thread budget, an environment
variable read once).

Verified against LabAPI 1.1.7 / SCP:SL 14.2.7. Facts it relies on (from the research notes):

- Game, Mirror, LabAPI wrappers and MEC are NOT thread-safe. Only logging, the game's
  `MainThreadDispatcher.Dispatch` and `SynchronizationContext.Post` are safe off-thread.
- The game's dispatcher drains a ConcurrentQueue with no try/catch and no time budget.
- MEC Update-segment waits use scaled, clamped `Time.deltaTime` (idle mode: timeScale 0.01).
  Wall-clock work must use `Stopwatch` deadlines.
- `Server.Tps` is `round(1/Time.smoothDeltaTime)` — smoothed and clamped; it hides hitches.
- `server_tickrate` is overwritten by Mirror (`targetFrameRate = sendRate`) at StartHost (read from
  the code, not yet measured on a running server). A fix would change the whole server's frame
  rate, which is the server owner's decision, so it is not part of SlKit.
- On the game's runtime `HttpClientHandler` runs on the managed socket handler: its per-host
  connection limit defaults to unlimited and `ServicePointManager` (2 per host) does not apply to it.
- `Plugin.Disable()` is never called by LabAPI or the game; the PlayerLoop survives scene loads.
- Modifying the PlayerLoop must insert into `PlayerLoop.GetCurrentPlayerLoop()` (Mirror inserts
  its own systems there); never replace it, never use `GetDefaultPlayerLoop()`.

## Namespaces and components

### `SlKit.SlClock` — the one clock
- One PlayerLoop insertion (Update phase, after scripts) per process, shared by every plugin that
  uses SlKit in the same AppDomain (static, idempotent install; internal marker type `SlKitClockMarker`).
- Wall-clock scheduling on `Stopwatch.GetTimestamp()`; absolute deadlines (no drift).
- `ClockJobHandle Every(TimeSpan interval, Action job, string name, ClockScope scope = ClockScope.Server, ClockJobOptions options = null)`
- `ClockJobHandle After(TimeSpan delay, Action job, string name, ClockScope scope = ClockScope.Round, ClockJobOptions options = null)`
- `ClockJobHandle EveryStaggered<T>(TimeSpan period, Func<IReadOnlyList<T>> source, Action<T> perItem, string name, ClockScope scope = ClockScope.Server, ClockJobOptions options = null)`
  — spreads per-item work evenly across the frames of one period (e.g. 60 players over 1 s at
  60 fps = 1 player per frame) instead of a burst on one frame. Source is re-read once per period.
- Registration returns a `ClockJobHandle` (`IDisposable`, plus `Reenable()`, `FailurePolicy` and
  the job's read-only failure settings).
- Per job: try/catch; exceptions are counted and logged rate-limited; after the job's
  `ClockJobOptions.MaxConsecutiveFailures` (default 5) in a row it is disabled. Policy `Backoff`
  (default): re-enabled after the job's `ReenableBaseDelay` (30 s), doubling per further disable
  without a success in between, capped at its `ReenableMaxDelay` (30 min), each disable and
  re-enable logged. Policy `DisablePermanently`: stays off until `handle.Reenable()` or
  `SlClock.Reenable(name)` (only jobs the calling assembly registered). The options are copied and validated at registration (invalid values
  throw); there is no static default a plugin could change for others.
- `Install` is MAIN only and checks it before touching anything: before the thread id is captured,
  `MainThread.IsMainThread` asks Unity (`Awaitable.MainThreadAsync().IsCompleted`, which compares
  with the main thread Unity records at startup; a Unity without `Awaitable` falls back to the
  current `SynchronizationContext` being Unity's). Off the main thread it throws.
- The first `Install` logs the SlKit version and main-thread budget, and a warning naming every
  other SlKit copy loaded in the process.
- `ClockScope.Round` jobs are cancelled automatically on round restart: `Install` (run by every
  registration) subscribes the internal `OnRoundRestart` to `ServerEvents.RoundRestarted` once per
  process. A restart cancels only Round jobs registered before the current Unity frame, so a job
  that another `RoundRestarted` handler registers for the next round survives whatever the handler
  order. `AutoWireLabApi()` is obsolete and only calls `Install`.
- Per-frame budget: the clock measures its own work; `SlClock.Stats` exposes per-job last/avg/max
  microseconds and run counts, so the plugin's own cost is observable.
- No allocation per frame in steady state.

### `SlKit.MainThread`
- `MainThread.Post(Action action)` — thread-safe; queued to a ConcurrentQueue drained by SlClock
  under a time budget, each action in try/catch.
- `MainThread.Budget` is read-only: 1 ms per frame, or the server owner's
  `SLKIT_MAIN_THREAD_BUDGET_MS` (0.05..50 ms), read once per process. Decision: a host-level value,
  not per-owner budgets. The budget bounds the frame time that all plugins' callbacks take together;
  per-owner budgets would multiply that bound by the number of plugins, and attributing a `Post` to
  its owner would need a stack walk per call on a hot path, which is still wrong for callbacks SlKit
  posts for a plugin from the thread pool. The queue stays FIFO and bounded by `MaxPending`.
- `MainThread.IsMainThread` — compares to the thread id captured at install; before that, asks
  Unity (see `Install`). `ThrowIfNotMainThread` therefore throws off the main thread from the start.
- Backlog limit `MaxPending` (100000): further posts are dropped and counted; the first drop logs a
  warning, then at most one per 10 s while dropping continues.
- Callbacks queued by `Background.TryRun<T>` and `SlHttp` install the clock when queued on the main
  thread; off the main thread, a missing clock is logged once.

### `SlKit.Background`
- Dedicated worker threads (background, below-normal priority), each with its own bounded queue
  (default 1024; when full, `TryRun` returns false — callers decide; never blocks the main thread).
- `BackgroundWorker Background.ForOwner(string name, int capacity = 1024)` — one worker per owner,
  keyed by name AND calling assembly (idempotent per pair), so one plugin's slow IO or full queue
  never blocks another's, even when both pick the same name; a name already used by another
  assembly gives the caller its own worker and logs a warning naming both. `"default"` is reserved
  (throws); the shared worker is `Background.Default`.
- `bool Background.TryRun(Action work)` and
  `bool Background.TryRun<T>(Func<T> work, Action<T> onMainThread, Action<Exception> onError = null)`
  — on a shared default worker (same methods exist on `BackgroundWorker`). `work` runs on the worker
  and MUST only touch plain snapshots; `onMainThread` is posted back.
- `Background.Capacity` is read-only (1024): the default worker is shared, so no plugin may resize
  it for the others. A plugin that needs another size asks `ForOwner(name, capacity)`.
- `SlHttp` — `SlHttp.CreateClient(name, SlHttpOptions)` gives each plugin a client with its own
  request timeout (default 10 s, counted from getting a slot), queue timeout (default 30 s, the
  wait for a slot), default headers, concurrency limit (default 4), waiting limit (default 64) and
  response body cap (default 1 MiB), over one internal shared `HttpClient`/handler for connection
  reuse. No public mutable `HttpClient`. The transport adds no per-host connection limit below the
  clients' (raised, never lowered; logged once). `PostJsonAsync`/`GetStringAsync`/`SendAsync` with
  `ConfigureAwait(false)`; each takes an optional main-thread callback (marshalled via
  `MainThread.Post`) and also returns a `Task<HttpResult>` that never faults; refusal, queue timeout,
  request timeout and oversize are distinct result flags, never exceptions, and every result says
  whether the request was sent. Never `.Result`/`.Wait()`.

### `SlKit.FrameProfiler`
- PlayerLoop insertions at the very start (Initialization, first) and very end
  (PostLateUpdate, last) of the frame. Records frame period (wall clock between starts) and busy
  time (start→end) with `Stopwatch`.
- Ring buffer of the last N frames (default 3600; `Install(capacity)` sets the size). `Snapshot()` returns p50/p95/p99/max of period
  and busy time, % frames over 50 ms and 100 ms, real FPS, plus Mirror
  `NetworkServer.actualTickRate` and `NetworkServer.fullUpdateDuration` average, and the
  `Server.Tps` value for comparison.
- Optional CSV sink (`StartCsv(path)` / `StopCsv()`): one line per second written by the profiler's
  own background worker (never on the main thread).
- `FrameProfiler.Mark(string name)` for event markers (round start, wave, detonation) in the CSV.

### `SlKit.HotEventAudit`
- At call time, reflects over LabAPI handler classes' static event backing fields and lists, for
  every HOT event (a built-in list, `HotEvents`), the subscribing methods and their assemblies.
- `HotEventAudit.Report()` returns lines; plugins call it from `ServerEvents.WaitingForPlayers`.
  `Scan()` returns the structured result and `Log()` writes the report with LabAPI's logger.

### `SlKit.RateLimitedLog`
- Token bucket per key; `RateLimitedLog.Warn(string key, Func<string> message)` (and `Info`,
  `Error`) — the message factory runs only when the bucket allows, so a suppressed line costs no
  string building.
- Periodic summary "N messages suppressed for key K". SlKit starts no timer on its own: the summary
  comes with a later log call, or every `SummaryInterval` after `EnablePeriodicSummary()`;
  `FlushSuppressed()` writes it at once.
- The static class is one table shared by every plugin with fixed limits (burst 3, one line per
  10 s, summaries every 30 s, 512 keys); it exposes no setter and no way to wipe the table. A plugin
  that needs other limits or keys of its own creates `new RateLimitedLogger(name, burst,
  refillInterval, summaryInterval)`: same methods, own keys, buckets, counters and `Reset()`, and
  nothing it does reaches another logger or the shared table. Invalid limits throw at creation.
- A refused call takes one short lock, increments a counter, builds no string and allocates nothing.

### `SlKit.Polyfills`
- Only APIs truly absent from the server BCL: `MinBy`, `MaxBy`, `DistinctBy`, `Chunk`, and
  `System.Runtime.CompilerServices.IsExternalInit` (for `init`/records). The class is named
  `EnumerablePolyfills`, because a class named `Polyfills` inside namespace `SlKit.Polyfills` would
  be confusing.
  (`KeyValuePair.Deconstruct`, `Dictionary.Remove(key, out value)`, the `ArraySegment<T>` indexer,
  `Span<T>`, `HttpClient` and `System.Text.Json` ARE present at runtime.)

## Packaging

- `SlKit.dll` goes to `LabAPI/dependencies/global` (or `/<port>`), never next to plugins.
- Or source-include: the files have no inter-file magic; copying `src/` into a plugin works. Rename
  the namespace and make the types internal, so the copy is private to the plugin.
- Every copy in the process has its own static state (clock, queue, workers); the first `Install`
  warns about other copies by name, version and origin.
- Versioning: semantic versioning; the assembly version equals the release version. In
  0.x a minor release may break the API, and every break is listed in `CHANGELOG.md` with a
  migration note.
- Pure logic (scheduler math, staggering, percentiles, token bucket, interval gate, failure backoff,
  clock job options, host budget, bounded read, copy report, owner registry, connection-limit rule)
  lives in files without Unity/LabAPI references so it is unit-testable on plain .NET;
  `SlHttpClient` and `Background` are additionally tested on plain .NET with stubs for the two
  LabAPI-bound helpers, and `SlClock`/`MainThread` with stand-ins for the Unity and LabAPI members
  they touch.
