# Threads, marshalling, async/await, logging and GC: reference

Verified against LabAPI 1.1.7 / SCP:SL 14.2.7 (2026-10). Companion to SKILL.md. Figures marked
"estimate" were not measured on this build. Items marked UNVERIFIED are open questions.

## Threads in the process

| Thread | What runs there | Your code? |
|---|---|---|
| Unity main thread | the whole frame: almost all LabAPI events, commands, MEC, `MainThreadDispatcher` drains, `SlClock` jobs, `MainThread.Post` actions | yes, by default |
| `ConnectionRequestThread` | preauth, including the **`PlayerEvents.PreAuthenticating` / `PreAuthenticated` handlers**. These are not raised at all while connections are delayed (boot, restart) | your handler runs here, so it must be pure data with no Unity or wrapper calls (see scpsl-game-lifecycle) |
| `NetworkPollThread` | LiteNetLib polling with a 30 ms sleep. It only enqueues closures, and the main thread runs them in `PreLateUpdate` | no |
| Game background threads | round-log writer (every 100 ms), TCP/file console output, central-server refresh, the GC thread at scene load | no |
| Thread pool | `Task.Run`, `System.Threading.Timer`, every continuation after `ConfigureAwait(false)` (the game's own HTTP helper uses it too) | yes, but snapshots only |
| `SlKit.Background` workers | one below-normal-priority thread with a bounded FIFO per plugin (`ForOwner`, named `SlKit.Background.<name>`), plus one shared default worker (capacity 1024) behind the static `TryRun` | yes, but snapshots only |

## Not thread-safe: assume everything

| Area | Evidence of unsafety | Consequence off-thread |
|---|---|---|
| Unity API | `Transform` has no thread-safe bindings. `Time.*` has no thread attribute | Use `Stopwatch` off-thread. Whether a given getter throws or silently misbehaves is UNVERIFIED; treat it as undefined |
| Mirror | `NetworkServer.connections`, `handlers` and `spawned` are plain `Dictionary`s. The writer pool is a plain `Stack` | Any `Send`, `Spawn` or SyncVar write races `Broadcast` |
| Game | `ReferenceHub.AllHubs` is a `HashSet`, the hub lookups are `Dictionary`s | `Collection was modified`, corrupted state |
| LabAPI wrappers | `Player.Dictionary` and the user-id cache are plain `Dictionary`s. **`Player.Get(hub)` adds a wrapper on a miss**, and `Player.List` enumerates the dictionary values. LabAPI has no locks or concurrent collections apart from one internal pool | Even a "read" can corrupt the cache |
| LabAPI events | `EventManager` invokes handlers synchronously on the caller's thread | Raising an event off-thread runs game logic off-thread |
| MEC | no `lock`, `Interlocked` or `Volatile` anywhere. `Timing.Instance` uses `GameObject.Find` | `RunCoroutine`/`KillCoroutines` off-thread corrupts it |

Pure value math on copied values (`Vector3`, `Mathf`) is fine on any thread.

## Safe from any thread

- LabAPI `Logger.*` and `ServerConsole.AddLog`. Their sinks are queues, and the game itself calls
  them from background threads.
- `ServerLogs.AddLog`, which takes a lock.
- `MainThreadDispatcher.Dispatch` (a `ConcurrentQueue` enqueue).
- `SynchronizationContext.Post` on the Unity context captured on the main thread.
- `Debug.Log` (a thread-safe binding, but it is logged twice; see Logging).
- Plain .NET: `HttpClient`, sockets, file IO, JSON over POCOs, hashing, compression, `Interlocked`,
  `Concurrent*`, `Stopwatch`, `DateTime`.
- SlKit: `MainThread.Post`, a `BackgroundWorker`'s `TryRun`, an `SlHttpClient`'s methods, `FrameProfiler.Mark`.

## Snapshot, work, marshal back

1. On the main thread, copy what the worker needs into strings, numbers, structs or POCOs. No
   `Player`, `ReferenceHub`, `Item` or `GameObject` crosses the thread boundary.
2. The worker touches only that snapshot.
3. The result comes back to the main thread with a try/catch around it. Re-resolve players there by
   `UserId`: the player may have left, and after a round restart every old wrapper is dead.

With SlKit, on the plugin's own worker (`_worker = Background.ForOwner(Name, 64)`, once in `Enable`):

```csharp
string[] names = SnapshotNames();                     // main thread, plain strings
bool queued = _worker.TryRun(
    () => Summarize(names),                           // worker: snapshot only
    summary => ApplySummary(summary),                 // main thread, wrapped in try/catch by SlKit
    error => Logger.Warn("summary failed: " + error.Message));
if (!queued) Logger.Warn("background queue full, summary skipped");   // TryRun never blocks
```

The static `Background.TryRun` uses ONE default worker (1024 items) for every plugin that shares `SlKit.dll`: a plugin whose disk write stalls for seconds delays everyone queued behind it, and one that fills the queue makes the others' `TryRun` return `false`. A worker per plugin keeps both problems inside the plugin. Keep the static call for a one-off.

Without SlKit (plain .NET plus the game's dispatcher):

```csharp
private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
// Set Timeout (and BaseAddress) before the first request: changing them afterwards throws
// InvalidOperationException. Default headers do not throw, but mutating them while requests are in flight is not thread-safe.
private int _inFlight;                                // single-flight guard, touched with Interlocked

private void SendReport(string json)                  // main thread; json built from POCOs here
{
    if (Interlocked.CompareExchange(ref _inFlight, 1, 0) != 0) return;
    Task.Run(async () =>
    {
        try
        {
            using (var body = new StringContent(json, Encoding.UTF8, "application/json"))
            using (var resp = await Http.PostAsync(_url, body).ConfigureAwait(false))
            {
                int code = (int)resp.StatusCode;      // pool thread: plain values only
                MainThreadDispatcher.Dispatch(() =>
                {
                    try { OnReportDone(code); }       // the dispatcher has no try/catch of its own
                    catch (Exception e) { Logger.Error(e); }
                }, MainThreadDispatcher.DispatchTime.Update);
            }
        }
        catch (Exception e) { Logger.Warn("report failed: " + e.Message); }  // Logger is thread-safe
        finally { Interlocked.Exchange(ref _inFlight, 0); }
    });
}
```

When an alert or report is dropped, record that in your state or at least log it. A silent drop
after the cooldown has already been spent loses the alert for good.

## Getting back to the main thread

| Option | Drain | Exception in one item | Budget | Allocation | Runs |
|---|---|---|---|---|---|
| `MainThreadDispatcher.Dispatch(a, DispatchTime.Update)` | `Update`. Unbounded: items enqueued during the drain run in the same drain | No try/catch. **It aborts the rest of the drain**, and the remainder (including the game's own callbacks) waits a frame | none | closure + delegate per call | next `Update` |
| `... DispatchTime.FixedUpdate` | `FixedUpdate` | same | none | same | may skip a frame above 50 FPS. Avoid |
| `SynchronizationContext.Post` (capture `SynchronizationContext.Current` in `Enable`) | Unity's `ExecuteTasks`, with a lock and a list that is removed from the front (O(n^2) for big batches) | Rethrown inside the drain. The rest of that frame's batch is postponed | none | delegate + state | at least 1 frame later |
| `await UnityEngine.Awaitable.MainThreadAsync()` (Unity 6) | Unity | — | none | awaitable | Unity-scheduled |
| **SlKit `MainThread.Post(a)`** | `SlClock` in `Update` | Per-action try/catch, counted in `FailureCount` | `Budget`: 1 ms per frame, set only by the server owner (`SLKIT_MAIN_THREAD_BUDGET_MS`); at least one action per frame | delegate | next `Update`, never inline |
| Your own `ConcurrentQueue<TStruct>` drained in your clock | You decide | Yours to catch | Yours (`Stopwatch`) | none per item | your clock |

For bursts (hundreds of results), use a struct queue drained under a time budget, not a closure
per item.

**Never block the main thread waiting for a worker.** That rules out `Task.Wait()`, `.Result`,
`GetAwaiter().GetResult()`, `Thread.Join`, and a `lock` that a worker holds across IO.
`NorthwoodLib.ActionDispatcher.Wait` sleep-polls until the main thread drains the queue, so called
on the main thread it never returns. The game's own `UnityHttp.Get`, called on the main thread,
blocks it in a sleep loop until the request finishes. That is the anti-pattern, not a precedent.
scpsl-lint SL007 flags the common forms.

## async/await on the server

- Unity installs a `UnitySynchronizationContext` on the main thread. An `await` there **without**
  `ConfigureAwait(false)` resumes on the main thread at the next `ExecuteTasks`, at least one frame
  later. An awaitable that has already completed continues inline in the same frame.
- **With** `ConfigureAwait(false)`, or inside `Task.Run` or a `Timer` callback, everything after
  the `await` runs on a pool thread and must follow the snapshot rules.
- LabAPI invokes handlers synchronously. In an `async void` handler, everything after the first
  `await` runs **after the event has returned**, so `ev.IsAllowed = false`, damage edits and other
  argument changes are ignored. Decide synchronously, then start async side work with copied
  values.
- An exception escaping an `async void` method is posted to the Unity context and rethrown inside
  its drain. That aborts the rest of that frame's continuations, which then run a frame late. Wrap
  the whole `async void` body in try/catch.

```csharp
private void OnPlayerDying(PlayerDyingEventArgs ev)
{
    if (IsProtected(ev.Player.UserId)) { ev.IsAllowed = false; return; }  // decided now, main thread
    string userId = ev.Player.UserId;                                      // copy before async work
    _ = _http.PostJsonAsync(_auditUrl, "{\"user\":\"" + JsonEscape(userId) + "\"}");  // _http = this plugin's SlHttp.CreateClient(Name); fire and forget, never awaited here
}
```

## Logging cost

| Call | Cost per line | Notes |
|---|---|---|
| `Logger.Info/Warn/Error/Debug` | `Assembly.GetCallingAssembly()` plus `GetName()` (a stack walk and an allocation), then `ServerConsole.AddLog`: 2 regex passes, a queue enqueue, and one `MainThreadDispatcher` closure per registered console output | Thread-safe. No level gate |
| `Logger.Debug(msg, canBePrinted)` | the string is built **before** the flag is checked | Guard at the call site: `if (Config.Debug) Logger.Debug(...)` |
| `Debug.Log` on the main thread | synchronous native write. 5-30 µs per line (estimate) | A storm of about 5000 lines per second was put at 26-155 ms of CPU per second (estimate) |
| `Debug.Log` from a worker | logged once, then queued by Mirror's `ThreadLog` and **logged again** on the main thread in `NetworkLateUpdate` | Use `Logger` from workers |

Rules: no logging per frame or per player per tick, rate-limit repeated failures (SlKit's
`RateLimitedLog.Warn(key, () => message)`, which builds the string only when the bucket allows; its keys are
shared by every plugin on the server, so prefix them with your plugin name, or create a `RateLimitedLogger` of your own
for your own limits), and log state changes instead of states. scpsl-lint SL008 flags logs in loops and hot handlers.

## GC

- The runtime is Mono with the **Boehm** collector: non-moving and non-generational, incremental
  with a 3 ms maximum time slice (`gc-max-time-slice=3` in `boot.config`).
- Every cycle marks the **whole live heap**. Your allocation rate decides how often a cycle starts:
  roughly every heap/3 bytes allocated with the default free-space divisor (estimate; whether the
  embedded runtime honours `GC_*` environment variables is UNVERIFIED). The slices land on frames.
- The game already calls `GC.Collect` at scene load and before every round restart. Never add
  more (SL010).
- Hot-path heuristic (every frame, every hit, shot or voice packet): no LINQ, no capturing lambdas,
  no `params` arrays, no boxing enumerators. Pre-size collections and reuse `StringBuilder`s and
  lists. `Player.ReadyList` is a LINQ `Where` over dictionary values, so it allocates per call;
  read it once per tick. A lambda in a rare handler costs nothing measurable, and no source
  measured LINQ or lambda cost on this runtime.

## SlKit map

| Need | SlKit | Raw alternative |
|---|---|---|
| Periodic or delayed work on the wall clock | `SlClock.Every`, `SlClock.After` | one coroutine + `Stopwatch` deadlines (frame-and-timing.md) |
| Per-player work spread over a period | `SlClock.EveryStaggered` | your own index cursor per frame |
| Round-bound timers | `ClockScope.Round` (ended by `SlClock.Install`'s wiring) | kill handles at `RoundRestarted` |
| Off-thread work | own `Background.ForOwner(Name, n)` worker: `TryRun` / `TryRun<T>` | `Task.Run` + snapshot |
| HTTP | own `SlHttp.CreateClient(Name, options)`: `GetStringAsync` / `PostJsonAsync` (per-plugin limits and headers, timeout counted from the slot, never faults) | static `HttpClient` + `ConfigureAwait(false)` |
| Back to the main thread | `MainThread.Post` | `MainThreadDispatcher.Dispatch` + your own try/catch |
| Thread guard | `MainThread.IsMainThread`, `ThrowIfNotMainThread` (`SlClock.Install` and registrations throw off the main thread) | compare `Thread.CurrentThread` with `Timing.MainThread` |
| Real frame time | `FrameProfiler.Install`, `Snapshot()`, `StartCsv` | PlayerLoop `Stopwatch` pair |

Ship `SlKit.dll` in `LabAPI/dependencies/`, never in `plugins/` (see deploying-scpsl-plugins).
`Plugin.Disable()` is never called, so `SlClock` has no uninstall. Make `Enable()` idempotent instead (EXILED can also call `Enable()` twice for a LabAPI plugin it loads).

## Re-verifying on a new build

- LabAPI source: does `Player.Get` still add on a miss? Is there still no `lock`/`Concurrent` in
  `LabApi/` (grep)? Is `EventManager` invocation still synchronous? Does `Logger` still call
  `GetCallingAssembly`, and does `Logger.Debug` still take a `canBePrinted` flag?
- Game: is the drain in `MainThreadDispatcher` / `NorthwoodLib.ActionDispatcher` still without a
  try/catch or budget? Check locally with a decompiler and do not publish the output.
- In a probe plugin (sl-probe), log `MainThread.IsMainThread` inside `PreAuthenticating`, inside
  `Joined`, and after an `await` with and without `ConfigureAwait(false)`. Also log
  `SynchronizationContext.Current?.GetType().Name` in `Enable`.
- Mirror: does `ThreadLog` still hook `Application.logMessageReceivedThreaded`?
- Install: is it still `SCPSL_Data/MonoBleedingEdge/.../libmonobdwgc-2.0.so` (Boehm), and is
  `gc-max-time-slice` still in `SCPSL_Data/boot.config`?
