# SlKit

> Commands below are run from the repository root (`scpsl-skills`). The game assemblies are found
> through the `SL_REFERENCES` environment variable, see [`Directory.Build.props`](Directory.Build.props).

A small runtime toolkit for SCP: Secret Laboratory plugins on **LabAPI 1.1.7 / game 14.2.7**.
It has no dependencies beyond the game. It gives you the patterns the server needs and that are
easy to get wrong on your own:

- **`SlClock`**: one wall-clock scheduler for all periodic, delayed and staggered plugin work.
  It is a single PlayerLoop entry with absolute `Stopwatch` deadlines, a try/catch per job,
  per-job cost statistics, and a backoff that takes a failing job out and brings it back later.
- **`MainThread`**: gets results back onto the main thread, through a queue that is drained under
  a time budget, with a try/catch around each action.
- **`Background`**: low-priority worker threads with bounded queues, for IO and CPU work. Each
  plugin can have its own (`Background.ForOwner`, keyed by name and calling assembly); a shared
  default one serves the static methods.
- **`SlHttp`**: HTTP that never blocks the main thread. Each plugin gets its own client with its
  own timeouts, headers, concurrency limit and response size cap, and every result says whether the
  request was sent and which phase (queue or wire) ended it.
- **`FrameProfiler`**: frame timing that shows every hitch. It records p50/p95/p99/max frame
  period and busy time, shown next to Mirror's counters and `Server.Tps`, and can write a CSV log
  once per second.
- **`HotEventAudit`**: lists which plugin handlers are subscribed to the LabAPI events that fire
  at high rate (voice, visibility, observers, ...).
- **`RateLimitedLog`**: logging with a token bucket per key and a message factory, so a line that
  repeats every frame costs a counter increment.
- **`SlKit.Polyfills`**: `MinBy`, `MaxBy`, `DistinctBy`, `Chunk` and `IsExternalInit`, which the
  server's BCL lacks.

SlKit does nothing behind your back. It patches nothing, does not use Harmony and starts no MEC
coroutines. Nothing runs until you call it, and nothing it does changes a server-wide setting.
Because one `SlKit.dll` serves every plugin on the server, it also has no static setting that one
plugin could change for the others: a job's failure handling is given when the job is registered,
a plugin that wants other log limits creates its own `RateLimitedLogger`, a plugin that wants
another queue size asks `Background.ForOwner` for a worker of its own, and the one value that is
genuinely shared, the main-thread budget per frame, belongs to the server owner (an environment
variable, see `MainThread`). `tests/SlKit.Runtime.Tests` and `tests/SlKit.Clock.Tests` check this by
reflection (no public static setter or writable public static field on those types). What the
plugins do still share, and why none of it is a setting, is listed under
[What the plugins share](#what-the-plugins-share). The full design contract is in
[SPEC.md](SPEC.md). This README documents what is implemented.

A `server_tickrate` fix changes the frame rate of the whole server, so it is deliberately not
library code: it is a standalone plugin the server owner installs,
[`tools/tickrate-fix`](../tickrate-fix/README.md) (see also
[`server_tickrate`](#server_tickrate-not-in-slkit)).

## Install

**As a shared dependency (recommended).** Build it, then copy `SlKit.dll` to

```
~/.config/SCP Secret Laboratory/LabAPI/dependencies/global/    (Linux; or dependencies/<port>/)
%APPDATA%\SCP Secret Laboratory\LabAPI\dependencies\global\    (Windows; or dependencies\<port>\)
```

Never put it in `plugins/`. LabAPI loads every `*.dll` under the configured folders, and libraries
belong in `dependencies/`. When every plugin references this one copy, they all share a single
clock entry, a single main-thread queue and the default worker.

**As source.** Copy `src/` into your plugin. The files have no hidden coupling between them, and
`src/Core` has no Unity/LabAPI references at all. Each source-included copy gets its own PlayerLoop
entry, because the marker type's identity belongs to the assembly that defines it. Two copies
therefore never collide, but they don't share anything either. See the next section for what each
choice costs.

### Versions and sharing

SlKit follows [semantic versioning](https://semver.org): `MAJOR.MINOR.PATCH`, and the
assembly version is the same number. While the major version is 0, a minor release (0.2 to 0.3) may
break the API; every break is listed in [CHANGELOG.md](CHANGELOG.md) with a migration note. A patch
release never breaks it. From 1.0, only a major release breaks it.

All of SlKit's state is static, and LabAPI loads every DLL with `Assembly.Load(byte[])` into one
AppDomain without removing duplicates. So every copy of SlKit in the process runs its own clock, its
own main-thread queue (each with its own budget per frame), its own default worker and its own
rate limits. The first `SlClock.Install()` therefore looks through the loaded assemblies and logs a
warning naming every other copy, with its version and where it came from: a second `SlKit.dll`, or
a plugin that compiled SlKit's sources in under the `SlKit` namespace. Choose one of these:

| Strategy | What you get | What it costs |
|---|---|---|
| **One shared `SlKit.dll`** in `LabAPI/dependencies/global` (recommended) | One clock, one main-thread queue and budget, one default worker; `SlClock.Stats` shows every plugin's jobs; the server owner upgrades SlKit by replacing one file. | All plugins on the server must work with that one version. In 0.x a plugin built against an older minor version can fail with `MissingMethodException` when the code that calls a changed member first runs. Check CHANGELOG.md before upgrading, and keep exactly one copy (the folders are scanned recursively, backups included). |
| **Source include, internalized**: copy `src/`, rename the namespace (for example `MyPlugin.Internal.SlKit`), make the public types `internal` | No version conflict, whatever other plugins ship. | Your plugin runs its own PlayerLoop entry, its own main-thread queue with its own budget (so the owner's budget is spent once per copy), and its own threads; it does not appear in the shared `SlClock.Stats`; SlKit fixes need a rebuild of your plugin. |
| Source include **without** renaming | Nothing extra. | The same costs as internalizing, plus the copy warning at every start and a namespace clash for anyone reading stack traces. Avoid it. |

Do not ship `SlKit.dll` inside your plugin as an embedded resource. LabAPI loads an embedded DLL
only if no assembly with the same simple name is loaded yet, so the first one wins and your plugin
may silently run against a different version.

### What the plugins share

One `SlKit.dll` means one set of statics. None of them is a setting a plugin can change for the
others; these are the shared things that remain, and what a plugin can and cannot do with each:

| Shared state | What one plugin can do to the others | Why that is acceptable, and the plugin-owned alternative |
|---|---|---|
| The clock's job list and the main-thread queue (`SlClock`, `MainThread.Post`) | Add jobs or posted actions that use up part of the owner's per-frame budget. It cannot change the budget, another plugin's job settings, or cancel/disable another plugin's job (`Reenable(name)` only touches jobs the calling assembly registered; `OnRoundRestart` is internal). | This is the point of one clock. `SlClock.Stats` names every job and its cost. |
| The shared `RateLimitedLog` table | Use up its 512 keys (later keys share `(other)`), or use the same key text as another plugin and so share its bucket. It cannot change the limits (3 lines, then one per 10 s) or wipe the table. | A key is a log label, so a collision costs a suppressed line, never behaviour. Prefix keys with the plugin name, or create a `RateLimitedLogger`: own keys, own limits, own `Reset`. |
| The default worker and default HTTP client (`Background.TryRun`, `SlHttp.GetStringAsync`) | Fill their queues, so another plugin's `TryRun` returns `false` or its request is `Rejected`. Their sizes are fixed. | Documented, and visible in `PendingCount`/`RejectedCount`. Use `Background.ForOwner` and `SlHttp.CreateClient` for anything regular. |
| The shared connection pool's per-host limit (`SlHttp`) | On a WebRequest-backed handler, creating a client raises the limit for the hosts it calls to the sum of the clients' `MaxConcurrency`. It is only ever raised, never lowered, and logged once. | It removes a hidden cap of 2 connections per host that would otherwise queue every plugin's requests. |
| `FrameProfiler` (PlayerLoop entries, window, CSV sink) | Install it, reset the window, start/redirect/stop the CSV, or uninstall it, for everyone. | It measures; no plugin's behaviour depends on it. It is a server owner's diagnostic (the [`sl-probe`](../sl-probe/README.md) plugin drives it). A plugin that only wants its own timings uses its own `Stopwatch`. |
| `HotEventAudit` | Nothing: it reads, and logs when asked. | Stateless. |

### Build

```
dotnet build tools/SlKit/src/SlKit.csproj -c Release
dotnet build tools/SlKit/src/SlKit.csproj -c Release -p:SL_REFERENCES="/path/to/SCPSL_Data/Managed"
```

Building needs a .NET SDK 10.x or newer (the test projects target `net10.0`). The project targets
`net48`, but it compiles **only against the game's own `SCPSL_Data/Managed` assemblies**. They are
never copied into this repository. [`Directory.Build.props`](Directory.Build.props) disables implicit
framework references, turns off the `Microsoft.NETFramework.ReferenceAssemblies` pack and points
`FrameworkPathOverride` at `SL_REFERENCES` (found at the default Steam install path on Linux and
Windows, otherwise pass it explicitly). Without that, the SDK silently resolves `mscorlib`/`System.Net.Http` from the
desktop .NET Framework reference pack (this was checked on the `csc` command line). With it, an API
that the server's Mono BCL lacks fails at compile time and not on the server. No NuGet packages are
used.

### Tests (pure logic, plain .NET)

```
dotnet run --project tools/SlKit/tests/SlKit.Tests/SlKit.Tests.csproj
```

This compiles `src/Core/**/*.cs` for `net10.0` and runs a small assertion runner with no test
framework. It covers deadline math, staggering, percentiles, the ring buffer, the token bucket, the
bounded queue, the interval gate behind the `MainThread` drop warning, the job failure backoff
schedule, the per-job clock options and the owner's budget setting, the bounded body read and text
decoding behind the `SlHttp` size cap, and the copy report.
Exit code 0 means every test passed.

```
dotnet run --project tools/SlKit/tests/SlKit.Runtime.Tests/SlKit.Runtime.Tests.csproj
```

This compiles the real `SlHttp`, `SlHttpClient`, `Background`, `BackgroundWorker`, `RateLimitedLog`
and `RateLimitedLogger` sources for `net10.0`, with small stubs for the LabAPI-bound `MainThread`,
`SlLog`, `SlClock` and `Logger`. It runs
`SlHttpClient` against a loopback HTTP server that counts which requests actually arrived (a request
that timed out in the queue must be `QueueTimedOut`, not `TimedOut`, and must never reach the
server), and calls `Background.ForOwner` from a second assembly emitted at run time (same name,
different assembly: separate worker and a warning). It also checks that the shared types have no
public static setter, that a `RateLimitedLogger` has its own limits and keys and a `Reset` that
touches nothing else, that a refused log call builds no string and allocates nothing, and that eight
threads never get more than the burst through. It cannot show anything specific to the
server's Mono BCL; those facts come from the game's own assemblies (see `SlHttp` below).

```
dotnet run --project tools/SlKit/tests/SlKit.Clock.Tests/SlKit.Clock.Tests.csproj
```

This compiles the real `SlClock`, `MainThread`, `PlayerLoopInjector` and job sources for `net10.0`
against small stand-ins for the Unity and LabAPI members they touch (a PlayerLoop tree the test runs
one frame at a time, `ServerEvents.RoundRestarted`, `Time.frameCount`, `Awaitable.MainThreadAsync`
and a recording logger). It checks that `Install` and registration refuse a worker thread before
anything is captured and leave the PlayerLoop untouched, that `Install` wires `RoundRestarted` once,
that `After` jobs end at a restart without any wiring by the plugin, that failure settings are per
job, that `SlClock` and `MainThread` have no public static setter left, that `OnRoundRestart` is not
public, and that `SlClock.Reenable(name)` only brings back jobs of the calling assembly. It cannot show that the
real engine answers the same way; that is checked by compiling against the game and on a server.

```
dotnet run --project tools/SlKit/tests/SlKit.Polyfills.Tests -c Release
```

The polyfills are pure code, so they compile unchanged for `net10.0`. This runner checks them
against the real `System.Linq.MinBy/MaxBy/DistinctBy/Chunk` of .NET 10, including ties, empty
input, null keys and argument validation. `FrameProfiler`, `HotEventAudit` and
the copy detection need the game's assemblies and are verified by compiling only (see the notes in
each section); their pure parts are in `src/Core` and tested.

## Quick start

The same code is compiled as [`examples/QuickStart`](examples/QuickStart/ExamplePlugin.cs)
(`dotnet build tools/SlKit/examples/QuickStart -c Release`), so it stays in sync with the API. The file is
formatted with braces around single statements and doc comments, so it reads a little longer than
the block below.

```csharp
using System;
using System.Collections.Generic;
using LabApi.Features.Console;
using LabApi.Features.Wrappers;
using LabApi.Loader.Features.Plugins;
using SlKit;

public sealed class ExamplePlugin : Plugin
{
    public override string Name => "Example";
    public override string Description => "SlKit quick start";
    public override string Author => "you";
    public override Version RequiredApiVersion => new Version(1, 1, 7);

    // Main-thread scratch lists, reused: steady state allocates nothing.
    private readonly List<Player> _players = new List<Player>(64);
    private readonly List<string> _names = new List<string>(64);

    // This plugin's own worker thread and queue: another plugin's slow IO cannot delay it.
    private BackgroundWorker _worker;

    public override void Enable()
    {
        SlClock.Install();          // main thread: insert the clock entry; Round jobs end at RoundRestarted
        _worker = Background.ForOwner(Name, capacity: 64);

        // Wall-clock heartbeat. Unlike MEC Update-segment waits, idle mode's timeScale 0.01
        // does not stretch it.
        SlClock.Every(TimeSpan.FromSeconds(30), Heartbeat, "example.heartbeat");

        // One hint per player per second, spread over the frames (1 player/frame at 60 players, 60 fps).
        SlClock.EveryStaggered<Player>(TimeSpan.FromSeconds(1), RefillPlayers, ShowHint, "example.hints");
    }

    public override void Disable() { } // never called by LabAPI or the game; flush in ServerEvents.Shutdown

    private void Heartbeat()
    {
        // 1) Snapshot on the main thread: plain strings, no wrappers leave this thread.
        _names.Clear();
        foreach (Player p in Player.List)
            if (p.IsReady && !p.IsHost) _names.Add(p.Nickname);
        string[] snapshot = _names.ToArray();

        // 2) Work off-thread on the snapshot only; 3) result comes back on the main thread.
        bool queued = _worker.TryRun(
            () => string.Join(", ", snapshot),                       // worker thread
            text => Logger.Info("Online: " + text),                  // main thread
            error => Logger.Warn("Heartbeat failed: " + error.Message));
        if (!queued)
            Logger.Warn("Worker queue full; heartbeat skipped");

        // Any thread may hand work to the main thread:
        MainThread.Post(() => Logger.Info("Clock cost avg: " + SlClock.FrameCost.AverageMicroseconds + " us"));
    }

    private IReadOnlyList<Player> RefillPlayers()
    {
        // Called once per period, after the previous period's items are done, so refilling is safe.
        _players.Clear();
        foreach (Player p in Player.List)
            if (p.IsReady && !p.IsHost) _players.Add(p);
        return _players;
    }

    private static void ShowHint(Player p)
    {
        if (p.IsDestroyed) return;  // the list is a snapshot; players can leave mid-period
        p.SendHint("Hello " + p.Nickname, 1.2f);
    }
}
```

Diagnostics from a command handler (main thread):

```csharp
foreach (ClockJobStats s in SlClock.Stats) response += s + "\n";
FrameProfiler.Install();                       // once, e.g. in Enable
response += FrameProfiler.Snapshot().ToString();
FrameProfiler.StartCsv("/home/scpsl/frames.csv");
FrameProfiler.Mark("round-start");             // from any thread
```

## API reference

Thread affinity is stated per member. **MAIN** means the game's main thread (LabAPI event
handlers, command handlers, `Plugin.Enable`, SlClock jobs, `MainThread.Post` callbacks). **ANY**
means any thread.

### `SlClock` (namespace `SlKit`)

| Member | Affinity | Notes |
|---|---|---|
| `void Install()` | MAIN, checked | Idempotent. Inserts one system into `PlayerLoop.GetCurrentPlayerLoop()`, in the `Update` phase right after `Update.ScriptRunBehaviourUpdate` (falls back to the end of `Update`), captures the main thread id, and subscribes `OnRoundRestart` to `ServerEvents.RoundRestarted` once per process, so `ClockScope.Round` jobs end with their round without further wiring. Called from any other thread it throws `InvalidOperationException` before touching anything (see `MainThread.IsMainThread` for how the thread is recognised). Called implicitly by the registration methods, and by `Background`/`SlHttp` when they queue a main-thread callback on the main thread. Call it in `Enable` so `MainThread.Post` works from the start. The first install logs the SlKit version and the main-thread budget, and warns about other SlKit copies in the process (see [Versions and sharing](#versions-and-sharing)). |
| `ClockJobHandle Every(TimeSpan interval, Action job, string name, ClockScope scope = ClockScope.Server, ClockJobOptions options = null)` | MAIN | First run at now + interval. Deadlines are absolute; missed periods are skipped, never replayed; at most one run per frame. `options`: this job's failure handling (below). |
| `ClockJobHandle After(TimeSpan delay, Action job, string name, ClockScope scope = ClockScope.Round, ClockJobOptions options = null)` | MAIN | One-shot. Defaults to `Round`, so delayed round logic never leaks into the next round; the registration installs the clock, and with it the round-restart wiring. |
| `ClockJobHandle EveryStaggered<T>(TimeSpan period, Func<IReadOnlyList<T>> source, Action<T> perItem, string name, ClockScope scope = ClockScope.Server, ClockJobOptions options = null)` | MAIN | `source` is read once per period. Items are spread by elapsed wall time: `round(N·t/P)` must be done by time `t`. A throwing item is skipped and the frame counts as one failure. |
| `void AutoWireLabApi()` | MAIN | **Obsolete** (CS0618): `Install` does the wiring now. Still works (it calls `Install`). Remove the call. |
| `int MaxConsecutiveFailures { get; }` | ANY | **Obsolete**, read-only: the default threshold, 5. The threshold is per job now: `ClockJobOptions.MaxConsecutiveFailures`. |
| `int Reenable(string name)` | MAIN | Re-enables every disabled job with that name that the CALLING ASSEMBLY registered, now, and returns how many. For an admin command. Another plugin's job of the same name is not touched. |
| `IReadOnlyList<ClockJobStats> Stats { get; }` | MAIN | New list per read: `Name`, `Kind`, `Scope`, `Runs`, `Failures`, `ConsecutiveFailures`, `IsDisabled`, `TimesDisabled`, `ReenableIn`, `FailurePolicy`, `Last/Average/MaxMicroseconds`, `SkippedPeriods`. |
| `RunningStats FrameCost { get; }` | MAIN | The clock's own time per frame (posted actions plus all jobs). |
| `bool IsInstalled`, `long FrameCount`, `int JobCount` | ANY / MAIN / MAIN | Diagnostics. |

`ClockScope { Server, Round }`: `Server` lives until disposed; `Round` also ends at round restart
(`ServerEvents.RoundRestarted`, which `softrestart` does not raise; that path ends the process
anyway). Register work for the next round at `WaitingForPlayers`, not at `RoundRestarted`: the game
can raise `RoundRestarted` more than once for one restart, and a later raise ends Round jobs made at
an earlier one.

`ClockJobOptions` (namespace `SlKit.Core`), copied at registration, so changing the object later
does nothing and one object can serve several registrations:

| Option | Default | Meaning |
|---|---|---|
| `MaxConsecutiveFailures` | 5 | Failures in a row that disable the job. At least 1. Earlier failures are logged rate-limited (burst 2, then 1 per 30 s, with a suppressed count). |
| `FailurePolicy` | `Backoff` | `Backoff`: re-enabled after a doubling delay. `DisablePermanently`: off until re-enabled by hand. The enum is in `SlKit.Core`. |
| `ReenableBaseDelay` | 30 s | First backoff delay. At least 1 s. |
| `ReenableMaxDelay` | 30 min | Longest backoff delay. At least `ReenableBaseDelay`. |

Invalid values throw `ArgumentOutOfRangeException` at registration. The defaults are public
constants (`ClockJobOptions.DefaultMaxConsecutiveFailures`, `DefaultReenableBaseDelay`,
`DefaultReenableMaxDelay`). There is deliberately no static "default options" to set: SlKit is one
DLL for every plugin, so such a setting would change other plugins' jobs.

```csharp
using SlKit.Core;
SlClock.Every(TimeSpan.FromMinutes(1), FlushToDatabase, "stats.flush",
    options: new ClockJobOptions { MaxConsecutiveFailures = 3, ReenableBaseDelay = TimeSpan.FromSeconds(10) });
```

The registration methods return a `ClockJobHandle` (it implements `IDisposable`, so code that stores
`IDisposable` still compiles):

| Member | Affinity | Notes |
|---|---|---|
| `void Dispose()` | ANY | Sets a flag; the entry is removed at the end of the next clock frame. Idempotent. |
| `string Name`, `ClockScope Scope`, `bool IsDisposed` | ANY | `IsDisposed` is also true once a one-shot job has run. |
| `int MaxConsecutiveFailures`, `TimeSpan ReenableBaseDelay`, `TimeSpan ReenableMaxDelay` | ANY | This job's settings, from its options. Read-only. |
| `bool IsDisabled` | MAIN | True while disabled after repeated failures. |
| `JobFailurePolicy FailurePolicy { get; set; }` | MAIN | This job's policy, from its options; a change applies to this job's next disable. |
| `bool Reenable()` | MAIN | Re-enables the job now if it is disabled (logged); false when there was nothing to do. |

**What happens to a failing job.** After its `MaxConsecutiveFailures` failures in a row the job
stops running and one error line is logged with the last exception. Under `Backoff` (the default)
the line also says when the job comes back: after its `ReenableBaseDelay` (30 s by default), then
1 min, 2 min, ... doubling up to its `ReenableMaxDelay` (30 min) for each further disable without
a successful run in between. Every automatic re-enable logs a warning with the delay it waited and
the next one. One successful run resets the schedule to the base delay. So a job that depends on a
database or a file share recovers by itself after an outage, and a job that is broken for good
costs about one log line per 30 minutes. Under `DisablePermanently` the job stays off until
`handle.Reenable()` or `SlClock.Reenable(name)`. Periods missed while disabled count in
`SkippedPeriods`.

There is no public `Uninstall`, on purpose. The entry is shared by every plugin using this SlKit
assembly, and the game never calls `Plugin.Disable()`. If another plugin rebuilds the PlayerLoop
and drops the entry, SlClock reinstalls it on the next registration made after a stall of 10 s or
more.

### `MainThread`

| Member | Affinity | Notes |
|---|---|---|
| `void Post(Action action)` | ANY | Queued to a `ConcurrentQueue`, then drained by SlClock in the next Update under `Budget`, with each action in its own try/catch. Never runs inline. |
| `bool IsMainThread { get; }` | ANY | Once the clock is installed, compares with the captured id (one volatile read). Before that it asks Unity: `UnityEngine.Awaitable.MainThreadAsync().IsCompleted`, which is true exactly when the caller is on Unity's main thread (awaiting it on the main thread continues at once; checked against the game's `UnityEngine.CoreModule.dll`). So the answer is right on every thread from the start, not only after install. |
| `bool IsKnown { get; }` | ANY | Whether the main thread id has been captured. |
| `TimeSpan Budget { get; }` | ANY | Read-only. 1 ms per frame, unless the server owner sets the environment variable `SLKIT_MAIN_THREAD_BUDGET_MS` (0.05 to 50, invariant culture, e.g. `2` or `0.5`), read once per process. An invalid value is ignored with one warning at install. At least one action runs per frame. |
| `void ThrowIfNotMainThread(string api)` | ANY | Guard for your own game-touching APIs. Throws off the main thread, also before the clock is installed. |
| `int PendingCount`, `long FailureCount`, `long ExecutedCount`, `long DroppedCount` | ANY | Diagnostics. |
| `const int MaxPending` (100000) | ANY | Backlog limit. If nothing drains the queue (clock not installed, or another plugin rebuilt the PlayerLoop and dropped the entry), `Post` drops further actions and counts them in `DroppedCount` instead of growing without bound. The first drop logs a warning at once; while dropping continues, another one follows at most every `DropWarningInterval`, with the count since the last line and the likely cause. |
| `static readonly TimeSpan DropWarningInterval` (10 s) | ANY | Minimum distance between two drop warnings. The check is one compare-and-swap on the drop path only; `Post` itself is unchanged. |

`Background.TryRun<T>` and every `SlHttp` call with a main-thread callback make sure the callback
can run: on the main thread they install the clock if no plugin has done so yet; on another thread,
where installing is not allowed, they log one warning per process that callbacks are waiting for
`SlClock.Install()`. Before the clock has captured the main thread id, "on the main thread" is
Unity's own answer (`Awaitable.MainThreadAsync().IsCompleted`, public since Unity 2023.1; the game
runs Unity 6). It compares with the thread Unity recorded at startup, so it is right on every
thread, including one that carries a copy of Unity's synchronization context. Only on a Unity without
`Awaitable` does SlKit fall back to "the current context is Unity's `UnitySynchronizationContext`".

**Why the budget is the owner's.** The budget bounds how much of each frame the posted callbacks of
all plugins together may take, the same kind of server-wide value as the frame rate. When it was a
settable property, any plugin could raise it for everyone or lower it under everyone else's
callbacks. Per-plugin budgets were considered and rejected: `Post` would have to find the caller's
assembly on every call (a stack walk on a hot path, and wrong for callbacks that SlKit posts on a
plugin's behalf from the thread pool), and the frame cost would grow with the number of plugins
instead of staying bounded. The queue is FIFO, so a plugin that floods it delays the others' callbacks
by its own backlog; `MaxPending` bounds that.

### `Background`

A worker runs its items one after another, so a plugin whose disk or database write stalls for
seconds holds up everything queued behind it, and a plugin that fills a queue makes every other
`TryRun` on it fail. Give each plugin that does regular or slow IO its own worker:

```csharp
private BackgroundWorker _worker;
public override void Enable() => _worker = Background.ForOwner(Name, capacity: 256);
// later, on any thread:
_worker.TryRun(() => File.AppendAllText(path, line));
```

| Member | Affinity | Notes |
|---|---|---|
| `BackgroundWorker ForOwner(string name, int capacity = 1024)` | ANY | The calling assembly's worker registered under `name`, created on first use with its own thread (`SlKit.Background.<name>`) and its own bounded queue. The same name from the same assembly returns the same worker, so calling it again leaks no thread; a different capacity is applied only if the thread has not started yet, and otherwise ignored with a warning. Workers are keyed by name **and calling assembly**: when another assembly already uses `name`, the caller still gets a worker of its own (thread `SlKit.Background.<name>@<assembly>`) and one warning names both assemblies, so two plugins never share a queue by picking the same name. `"default"` (any letter case) is reserved and throws `ArgumentException`; the shared worker is `Default`. |
| `BackgroundWorker Default { get; }` | ANY | The shared default worker behind the static methods. |
| `bool TryRun(Action work)` | ANY (work runs on the worker) | On the default worker. Returns `false` immediately when the queue is full. Exceptions are logged rate-limited on the worker. |
| `bool TryRun<T>(Func<T> work, Action<T> onMainThread, Action<Exception> onError = null)` | ANY (work: worker; callbacks: MAIN) | On the default worker. The result or error is posted through `MainThread.Post`. With `onError == null` the error is logged. Installs the clock when called on the main thread before anyone did (see `MainThread`). |
| `int Capacity { get; }` | ANY | The default worker's capacity: 1024, read-only (it is shared, so no plugin may resize it for the others). For another size use `ForOwner(name, capacity)`. |
| `int PendingCount`, `long RejectedCount`, `long FailureCount` | ANY | The default worker's diagnostics. |
| `bool IsBackgroundThread` | ANY | True on any SlKit worker thread of this SlKit copy. |
| `IReadOnlyList<BackgroundWorker> Workers { get; }` | ANY | Every worker created so far, the default one first. New array per read. |

`BackgroundWorker` (every member ANY):

| Member | Notes |
|---|---|
| `bool TryRun(Action work)`, `bool TryRun<T>(Func<T> work, Action<T> onMainThread, Action<Exception> onError = null)` | As the static methods, on this worker. |
| `string Name`, `int Capacity`, `bool IsStarted` | |
| `string Owner`, `string ThreadName` | The assembly that created it (SlKit's own for the default worker) and its thread's name, as seen in thread dumps. |
| `int PendingCount`, `long RejectedCount`, `long FailureCount`, `bool IsWorkerThread` | Diagnostics for this worker. |

Each worker is one dedicated thread with `IsBackground = true` and `ThreadPriority.BelowNormal`.
It starts on the first `TryRun`, runs FIFO and lives for the whole process (the game never unloads
a plugin), so create a worker once, in `Enable`, and keep it. The default worker's thread is named
`SlKit.Background`. The calling assembly is found with `Assembly.GetCallingAssembly()` in a
method marked `NoInlining`; call `ForOwner` from your plugin's own code, not through a helper in a
shared library, or the helper's assembly becomes the owner. `work` must only touch plain snapshots (strings, numbers, POCOs) that
were copied on the main thread.

### `SlHttp`

Each plugin creates its own client once, with its own settings:

```csharp
SlHttpOptions options = new SlHttpOptions { Timeout = TimeSpan.FromSeconds(5), MaxConcurrency = 2 };
options.DefaultHeaders["Authorization"] = "Bearer " + Config.ApiToken;   // this client only
_http = SlHttp.CreateClient(Name, options);
// later, on any thread:
_http.PostJsonAsync(url, json, result => { if (!result.Success) Logger.Warn("Report: " + result); });
```

| `SlHttp` member | Affinity | Notes |
|---|---|---|
| `SlHttpClient CreateClient(string name, SlHttpOptions options = null)` | ANY | A new client. The options are copied, so later changes to them do nothing. Holds no thread or socket of its own. A content header (such as `Content-Type`) in `DefaultHeaders` throws `ArgumentException` here. |
| `SlHttpClient Default { get; }` | ANY | The shared client behind the static methods, with default options. Its slots are shared by every plugin that uses the static methods. |
| `Task<HttpResult> GetStringAsync(...)`, `Task<HttpResult> PostJsonAsync(...)` | ANY (callback: MAIN) | The `SlHttpClient` methods below, on `Default`. |
| `TimeSpan DefaultTimeout { get; }` | ANY | **Obsolete**, read-only: `Default.Timeout` (10 s). |
| `int TransportConnectionLimit { get; }`, `string TransportDescription { get; }` | ANY | The per-host connection limit of the shared transport after SlKit's check (`int.MaxValue` = none, the case on the game's runtime; -1 = decided per host by `ServicePointManager`), and the line logged once when the transport was created. |

`SlHttpOptions` (copied at creation):

| Option | Default | Meaning |
|---|---|---|
| `Timeout` | 10 s | Request timeout, counted from the moment the request gets a concurrency slot. Positive or `Timeout.InfiniteTimeSpan`. Ends a sent request with `TimedOut`. |
| `QueueTimeout` | 30 s | Longest wait for a slot. Zero means "never wait", `Timeout.InfiniteTimeSpan` no limit. Ends a waiting request with `QueueTimedOut`; it is not sent. A request can take up to `QueueTimeout + Timeout` in total. |
| `MaxConcurrency` | 4 | Requests of this client on the wire at once. |
| `MaxQueued` | 64 | Requests that may wait for a slot; more are `Rejected`. |
| `MaxResponseBytes` | 1 MiB | Body size cap. |
| `BaseAddress`, `DefaultHeaders` | null, empty | For relative URLs; headers for this client only. |

Invalid values throw `ArgumentOutOfRangeException` at `CreateClient`.

| `SlHttpClient` member | Affinity | Notes |
|---|---|---|
| `Task<HttpResult> GetStringAsync(string url, Action<HttpResult> onMainThread = null, TimeSpan? timeout = null, CancellationToken cancellationToken = default, long? maxResponseBytes = null)` | ANY (callback: MAIN) | Starts on a pool thread and uses `ConfigureAwait(false)` throughout. The task never faults. **Never `.Result`/`.Wait()` it on the main thread.** |
| `Task<HttpResult> PostJsonAsync(string url, string json, ...same optional parameters)` | ANY (callback: MAIN) | `application/json; charset=utf-8`. |
| `Task<HttpResult> SendAsync(Func<HttpRequestMessage> createRequest, ...same optional parameters)` | ANY (factory: pool thread; callback: MAIN) | Any method, body or per-request header. The factory builds a new message per call. The client's default headers are added unless the request already has them. |
| `string Name`, `TimeSpan Timeout`, `TimeSpan QueueTimeout`, `int MaxConcurrency`, `int MaxQueued`, `long MaxResponseBytes`, `Uri BaseAddress` | ANY | The copied settings. |
| `int InFlightCount`, `int OutstandingCount`, `long RejectedCount`, `long QueueTimedOutCount` | ANY | Diagnostics. |

How it is bounded:

- **Concurrency.** At most `MaxConcurrency` requests of a client are on the wire. Up to `MaxQueued`
  more wait asynchronously for a slot, holding no thread, for at most `QueueTimeout`. A request
  beyond `MaxQueued` is not sent; it completes at once with `Rejected = true` (also through the
  callback). One that waited `QueueTimeout` is not sent either and completes with
  `QueueTimedOut = true`. The pool work per request is a short start-up step, so the thread pool
  never carries more than the slots allow.
- **Two timeouts, two phases.** The wait for a slot and the request itself are timed separately, so
  a result never blames the endpoint for the client's own backlog: `QueueTimedOut` means "too many
  of my requests, never sent", `TimedOut` means "sent, no answer within `Timeout`". `Sent` says
  whether the endpoint can have seen the request; a request with `Sent == false` is always safe to
  retry, one with `Sent == true` (for example a `POST` that `TimedOut`) may already have been
  processed.
- **Per-host connections.** On the game's runtime, `HttpClientHandler` runs on the managed socket
  handler (read from the game's own `System.Net.Http.dll`): its per-host limit
  (`MaxConnectionsPerServer`) defaults to unlimited and lives in the handler, and
  `ServicePointManager` is never consulted. `ServicePointManager.DefaultConnectionLimit` (2 per host
  on Mono) only governs `HttpWebRequest`/`WebClient`. So `MaxConcurrency = 4` means 4 connections,
  and plugins calling the same host do not wait on each other. SlKit checks this once when the
  transport is created, raises a lower handler limit on its own handler (never lowers one), and logs
  the result (`[SlKit] SlHttp transport: per-host connection limit unlimited, ...`). On a runtime
  whose handler is backed by `HttpWebRequest` it instead raises each called host's `ServicePoint`
  limit to the total `MaxConcurrency` of all clients, on first use, and logs it per host; it never
  changes `DefaultConnectionLimit` for the process.
- **Response size.** The body is read from the response stream only up to `MaxResponseBytes` (per
  request: `maxResponseBytes`). A `Content-Length` above the limit is refused before reading (not
  for a `HEAD` response, which declares a length but sends no body); a longer body stops one byte
  past the limit. Both end as a failed result with `TooLarge = true`,
  the real `StatusCode` and `Body = null`, never as an exception and never as a cut-off body.
  Text is decoded by byte order mark, then the declared charset, then UTF-8.
- **Isolation.** All clients send through one internal `HttpClient` and handler, so connections are
  reused, but there is no public `HttpClient`: one plugin cannot change headers, base address or
  timeout for the others.

A per-request `timeout` (the request phase; the queue wait always uses the client's
`QueueTimeout`) of zero or less is raised to 1 ms and one above `int.MaxValue` ms is lowered to it
(`Timeout.InfiniteTimeSpan` means no timeout); bad arguments to the token sources are reported as
an error result as well, so the task never faults. A negative `maxResponseBytes` and a null `url`,
`json` or factory throw at the call.

`HttpResult` (immutable, ANY):

| Member | Meaning |
|---|---|
| `bool Success` | 2xx with a complete body. |
| `int StatusCode`, `string Body`, `Exception Error` | 0 / null when no response headers arrived. `Error` describes a failure; it is never thrown. |
| `bool Rejected` | Not sent: the client already had `MaxConcurrency` running and `MaxQueued` waiting. |
| `bool QueueTimedOut` | Not sent: no free slot within `QueueTimeout`. The cause is this client's backlog. |
| `bool TimedOut` | Sent, then abandoned after `Timeout` (counted from the slot). The endpoint or network was slow. |
| `bool TooLarge` | The body exceeded the limit; `Body` is null. |
| `bool Sent` | The request was handed to the transport, so the endpoint may have received it. False for `Rejected`, `QueueTimedOut`, a cancel while waiting and a throwing request factory. |
| `TimeSpan Elapsed`, `QueueTime`, `RequestTime` | Total; waiting for a slot; on the wire (`Elapsed - QueueTime`, zero when not sent). |

`ToString()` names the phase: `not sent: no free slot within 200 ms (client backlog, not the
endpoint)`, `timeout after 10000 ms on the wire (after 350 ms waiting for a slot)`, `HTTP 200 in 42 ms`.

### `FrameProfiler`

| Member | Affinity | Notes |
|---|---|---|
| `void Install(int capacity = 3600)` | MAIN | Idempotent. Adds two PlayerLoop systems: the **first** in `Initialization` (frame start) and the **last** in `PostLateUpdate` (frame end). |
| `void Uninstall()` | MAIN | Removes both entries and stops the CSV. Samples are kept. |
| `FrameSnapshot Snapshot()` | MAIN | Sorts two arrays of up to `capacity` doubles, so call it on demand and not every frame. |
| `void Mark(string name)` | ANY | Adds an event marker to the current second's CSV line. Does nothing while the CSV is off. |
| `void StartCsv(string path)` / `void StopCsv()` | MAIN | Writes one line per second: utc, frames, fps, period p50/p95/p99/max, busy p50/p95/p99/max, % > 50 ms, % > 100 ms, Mirror tick rate, Mirror full-update ms, Server.Tps, targetFrameRate, marks. The main thread only copies that second's samples into a pooled buffer. Sorting, formatting and IO happen on the profiler's own worker (`Background.ForOwner("SlKit.FrameProfiler")`), so a slow disk does not delay plugins' work and a busy default worker does not drop seconds. If the writer falls 4 s behind, seconds are dropped and counted in `CsvDroppedSeconds`. |
| `bool IsInstalled`, `string CsvPath`, `long CsvDroppedSeconds` | ANY | |
| `int Capacity`, `long FrameCount` | MAIN | |

`FrameSnapshot` has `Frames`, `PeriodMs` and `BusyMs` (`DistributionSummary`: `Count`, `Min`,
`Mean`, `P50`, `P95`, `P99`, `Max`), `PercentOver50Ms`, `PercentOver100Ms`, `RealFps`
(1000 / mean period), `LoadPercent` (mean busy / mean period), `MirrorActualTickRate`
(`Mirror.NetworkServer.actualTickRate`), `MirrorFullUpdateMs`
(`NetworkServer.fullUpdateDuration.average` × 1000), `ServerTps` (LabAPI `Server.Tps`) and
`TargetFrameRate` (`Application.targetFrameRate`). Both Mirror members were checked against the
installed `Mirror.dll`: they are a `public static int` and a `public static TimeSample` with a
`public double average`. Mirror values read 0 while `NetworkServer.active` is false.

The busy time assumes that the frame limiter waits before `Initialization`. That has not been
verified for this Unity build. If it is wrong, the wait shows up in busy time, but the period is
still correct.

### `HotEventAudit`

Answers "which plugin is subscribed to the events that cost the most?". LabAPI exposes no list of
subscribers, but each event is a field-like `static event` whose compiler-generated private
backing field holds the delegate, and the delegate's invocation list is the subscriber list. The
audit reads that field by reflection (checked on the installed `LabApi.dll`: the fields exist,
are private statics and are named like the event). It covers only the events in its built-in list of
hot events (per frame or at high network rate), 14 of them for LabAPI 1.1.7
(`HotEventAudit.HotEvents` returns the exact list), for example
`PlayerEvents.SendingVoiceMessage`, `PlayerEvents.ValidatedVisibility` and
`Scp173Events.AddingObserver`. The list is kept by hand in
[`HotEventAudit.HotList.cs`](src/Runtime/HotEventAudit.HotList.cs); it mirrors the events marked
`hot` in the repository's [event catalog](../catalog/labapi-events.json).

| Member | Affinity | Notes |
|---|---|---|
| `IReadOnlyList<string> Report()` | MAIN | Log-ready lines: a header, then per event with subscribers one line plus one indented line per handler (`Owner.Method [Assembly Version]`, lambdas marked and attributed to the class they were written in), then a count per assembly. Never throws. |
| `void Log()` | MAIN | `Report()` written with `Logger.Info`. |
| `HotEventAuditResult Scan()` | MAIN | Structured form: `Subscribers` (`HotEventSubscriber`: `EventName`, `Method`, `DeclaringType`, `OwnerType`, `Assembly`, `IsCompilerGenerated`), `EventsWithSubscribers`, `NotInspected`, `HotEventCount`, `Error`. Never throws. |
| `IReadOnlyList<string> HotEvents { get; }` | ANY | Copy of the embedded list. |
| `CatalogLabApiVersion`, `CatalogGameVersion` | const | `"1.1.7"` and `"14.2.7"`. |

Typical use, after every plugin has been enabled:

```csharp
ServerEvents.WaitingForPlayers += () => HotEventAudit.Log();
```

A hot event whose class or field is missing (another LabAPI version) is listed under "not
inspected" and does not stop the rest. The audit shows who is subscribed, not how slow they are.
For the cost, use `FrameProfiler` and the per-handler timing you add yourself.

### `RateLimitedLog`

The static class is ONE table shared by every plugin on the server, with fixed limits, so it has no
setting and no way to wipe it. Prefix your keys with your plugin name (`"MyPlugin.voice.parse"`).
A plugin that needs other limits or keys of its own creates a `RateLimitedLogger` (below).

| Member | Affinity | Notes |
|---|---|---|
| `void Info/Warn/Error(string key, Func<string> message)` | ANY | Takes one token from the key's bucket. When allowed it logs `[key] text`; when not, it only counts. `message` is evaluated only when allowed, so a refused call builds no string. An exception inside `message` is logged as a warning instead of thrown. Never throws. |
| `int Burst { get; }` | ANY | Lines allowed back to back. 3, read-only. |
| `TimeSpan RefillInterval { get; }` | ANY | Time to earn one line back. 10 s, read-only. |
| `TimeSpan SummaryInterval { get; }` | ANY | How often suppressed counts are reported. 30 s, read-only. |
| `void FlushSuppressed()` | ANY | Logs `[key] N messages suppressed` for every key with pending refusals, now. It only brings lines forward; nothing is dropped. |
| `void EnablePeriodicSummary()` | MAIN | Registers an `SlClock` job calling `FlushSuppressed` every `SummaryInterval`. Idempotent for the whole process. |
| `long SuppressedTotal`, `int KeyCount` | ANY | Diagnostics of the shared table. |

At most `MaxKeys` (512) keys are tracked per table. Further ones share the `(other)` key, so a key
built from player names cannot grow without bound; still, keys should be constants.

```csharp
RateLimitedLog.Warn("MyPlugin.voice.parse", () => "bad packet from " + player.Nickname);
```

#### `RateLimitedLogger`: limits and keys of your own

```csharp
private RateLimitedLogger _log;
public override void Enable() =>
    _log = new RateLimitedLogger(Name, burst: 10, refillInterval: TimeSpan.FromSeconds(2));
// anywhere, any thread:
_log.Warn("voice.parse", () => "bad packet from " + player.Nickname);
```

Same behaviour as the static class, but the keys, the buckets and the counters belong to this
instance, so nothing another plugin logs or does can use up your keys or change your limits, and
nothing you do reaches theirs.

| Member | Affinity | Notes |
|---|---|---|
| `RateLimitedLogger(string name, int burst = 3, TimeSpan? refillInterval = null, TimeSpan? summaryInterval = null)` | ANY | `name` is the plugin name (used for the summary job's name). Defaults: 3 lines, one per 10 s, summaries every 30 s. `burst < 1`, `refillInterval < 1 ms` or `summaryInterval < 1 s` throw `ArgumentOutOfRangeException`; nothing is clamped silently. Create it once and keep it in a field. |
| `void Info/Warn/Error(string key, Func<string> message)` | ANY | As the static methods, on this logger's keys. |
| `int Burst`, `TimeSpan RefillInterval`, `TimeSpan SummaryInterval`, `string Name` | ANY | Fixed at creation. |
| `void FlushSuppressed()`, `void EnablePeriodicSummary()` | ANY / MAIN | As the static ones, for this logger. The clock job is named `ratelimitedlog.summary.<name>`. |
| `void Reset()` | ANY | Forgets this logger's keys and drops their pending suppressed counts. No other logger and not `RateLimitedLog` is touched. |
| `long SuppressedTotal`, `int KeyCount` | ANY | Diagnostics of this logger. |

Why it exists: LabAPI's `Logger.*` does a stack walk and regexes per line, and building the text
(interpolation, `ToString` on wrappers) is a second cost that a plain "log every Nth" check does not
avoid. A "suppressed" summary appears in three situations: the key next gets an allowed line; any
call, on any key, notices that `SummaryInterval` has passed; or `FlushSuppressed` runs (by hand or
through `EnablePeriodicSummary`). If a key goes quiet and nothing else logs either, its count waits
until `FlushSuppressed` or `EnablePeriodicSummary` is used.

The lambdas in the examples above allocate a closure per call when it captures. On a path that runs per packet,
store a static delegate and put the data in a field it reads, or check a cheap condition first.

All state of a table or logger sits behind one lock that is never held while logging or running `message`. The log lines
carry LabAPI's assembly tag `[SlKit]`, since `Logger` tags by calling assembly.

### `server_tickrate` (not in SlKit)

Reading the code shows Mirror overriding `server_tickrate` with its send rate (60) when the host
starts, so a configured 20 TPS would keep running at 60; this has not been measured yet. A fix
would re-assign `Server.MaxTps` at `WaitingForPlayers`, which sets the frame rate of the whole
server: a decision for the server owner, not for library code that any plugin can call, so SlKit
does not contain one. The standalone plugin [`tools/tickrate-fix`](../tickrate-fix/README.md) does it;
it is off by default. A plugin that wants the server at its configured tickrate should say so in its
README and let the owner decide. `FrameProfiler` reports `Application.targetFrameRate`, so the
effect is visible in its CSV.

### `SlKit.Polyfills`

`using SlKit.Polyfills;` gives `IEnumerable<T>` extensions `MinBy`, `MaxBy` (with and without an
`IComparer<TKey>`), `DistinctBy` (with and without an `IEqualityComparer<TKey>`) and `Chunk`.
They follow the .NET 6 behavior: ties go to the first element, an empty sequence returns `default`
for nullable types and throws `InvalidOperationException` otherwise, null keys are ignored while a
non-null one exists, and `Chunk` yields fresh arrays with a shorter last one. `DistinctBy` and
`Chunk` are lazy but check their arguments at the call. All are ANY thread and allocate, so keep
them out of per-frame code.

`System.Runtime.CompilerServices.IsExternalInit` is also provided, because `init` setters and
`record` types fail with CS0518 without it. It is absent from the server's `mscorlib`,
`System.Core`, `netstandard` and `System` (searched). Guards: it is compiled out on
`NET5_0_OR_GREATER`, and defining `SLKIT_NO_ISEXTERNALINIT` removes it when you source-include
SlKit and already have a copy. Two public copies in two referenced assemblies make `init`
ambiguous (CS0433), so only one library on the server should ship it.

No polyfill is needed for `KeyValuePair.Deconstruct`, `Dictionary.Remove(key, out value)`,
`Span<T>`, `HttpClient` or `System.Text.Json`: they exist at runtime (see SPEC.md).

### `SlKit.Core` (pure, no Unity/LabAPI; compiles on plain .NET)

| Type | Purpose |
|---|---|
| `StopwatchTime` | `Now`, `Frequency`, conversions between `TimeSpan`, ms, µs and Stopwatch ticks. ANY. |
| `DeadlineTimer` (mutable struct) | Absolute-deadline timer: `Periodic(now, period)`, `PeriodicFrom(first, period)`, `OneShot(now, delay)`, `bool Poll(now)` (true at most once per call, skips missed periods and counts them in `SkippedPeriods`), `Disarm()`, `static long NextDeadline(deadline, period, now, out skipped)`. |
| `StaggerPlanner` (mutable struct) | `Begin(start, period, count)`, `int Take(now)`, `Cancel()`, `static int TargetProcessed(total, elapsed, period)`. Rounds instead of taking the ceiling, so frame jitter around item boundaries cannot produce a 2/0 pattern. |
| `RingBuffer` | Fixed-capacity `double` ring with `Add` (allocation-free), `CopyTo` and `CopyLast` (oldest first). |
| `Percentiles` / `DistributionSummary` | Nearest-rank `SummarizeInPlace` (p50/p95/p99/max/min/mean), `NearestRankSorted`, `PercentAbove`. |
| `RunningStats` (mutable struct) | Count, last, max and total in ticks, plus µs helpers. |
| `TokenBucket` | `TryTake(now, cost)`, `Refill`, `TakeDeniedCount()` for "N suppressed" summaries. Not thread-safe. |
| `BoundedWorkQueue<T>` | Lock-free, non-blocking `TryEnqueue` and a blocking-with-timeout `TryDequeue` for one consumer. Thread-safe. |
| `AdmissionLimit` | `TryEnter()` / `Leave()`: lock-free "at most N admitted at once", refusing the rest immediately; the limit is a `long` so `MaxConcurrency + MaxQueued` cannot wrap. Behind `SlHttpClient`'s admission. Thread-safe. |
| `IntervalGate` | `TryPass(now, out blockedBefore)`: true for the first call ever and then once per interval, for any number of threads (one compare-and-swap). Behind the `MainThread` drop warning. Thread-safe. |
| `FailureBackoff` (mutable struct), `JobFailurePolicy`, `FailureVerdict` | Consecutive-failure counting, disable at a threshold, `static long DelayFor(streak, base, max)` = `min(base·2^(streak-1), max)` without overflow, `IsReenableDue(now)`, `Reenable()`. Behind `SlClock`'s backoff. |
| `BoundedRead` / `BoundedReadResult` | `ReadAsync(stream, maxBytes, token)` reads at most `maxBytes + 1` bytes and reports `Exceeded`; `Decode(bytes, count, charset)` honours a byte order mark, then the charset, then UTF-8. Behind `SlHttp`'s size cap. Stateless. |
| `CopyReport` / `SlKitCopy` | Formats the "several SlKit copies are loaded" warning. Stateless. |
| `OwnerRegistry<T>` | Values keyed by name AND owner (compared by reference), with `FirstOwnerOf(name)` to detect a name another owner already uses. Behind `Background.ForOwner`. Not thread-safe; the caller locks. |
| `ConnectionLimit` | `Raise(current, wanted)` (never lowers), `SaturatingAdd`, `Describe`. The rule behind SlHttp's per-host connection limit. Stateless. |
| `ClockJobOptions` | One job's failure handling, passed to `SlClock.Every/After/EveryStaggered` (see `SlClock`). Plain data, copied and validated at registration. |
| `HostBudget` | `VariableName` (`SLKIT_MAIN_THREAD_BUDGET_MS`), the default and the accepted range, and `TryParse(raw, out ms, out problem)`: the owner's main-thread budget. Stateless. |

Instances of `DeadlineTimer`, `StaggerPlanner`, `RingBuffer`, `RunningStats`, `TokenBucket` and
`FailureBackoff` are not thread-safe. Use each one from a single thread.

## Why it is built this way

Each point refers to a fact listed at the top of [SPEC.md](SPEC.md).

- **Main thread only.** Game, Mirror, LabAPI wrappers and MEC are not thread-safe. They use plain
  `Dictionary`/`HashSet` statics with no locks, and even `Player.Get` can write to a dictionary.
  That is why every game-touching SlKit member is MAIN, why `Background` work gets only snapshots,
  and why results come back through `MainThread.Post`. Logging (`Logger.*`) is safe from any
  thread, and SlKit logs from the worker directly.
- **A budgeted, guarded drain.** The game's `MainThreadDispatcher` drains its queue with no
  try/catch and no time budget. One throwing action aborts the rest of that drain, and a burst of
  actions all runs in one frame. `MainThread.Drain` wraps each action in a try/catch and stops at
  `Budget`.
- **`Stopwatch`, not `Time.*` or MEC.** MEC Update-segment waits sum the scaled, clamped
  `Time.deltaTime`. In idle mode (`timeScale = 0.01`) a 30 s wait takes about 50 minutes, and
  hitches longer than `maximumDeltaTime` are lost. `CallPeriodically` re-arms from the frame it
  resumes on, so it drifts. SlClock uses absolute Stopwatch deadlines (`next = previous + period`).
  Its resolution is still one frame, so while idle at the default `idle_mode_tickrate` of 1 nothing
  fires more often than about once a second.
- **Skip, don't replay.** After a freeze, a job that is 20 periods late runs once and not 20 times
  on a frame that is already late. `SkippedPeriods` makes the skip visible.
- **One clock, one exception boundary.** A coroutine that throws ends for good, and MEC only logs
  it. Per-event `CallDelayed` allocates an iterator plus a closure and does dictionary work. SlClock
  keeps a plain job list, wraps each job in a try/catch, disables a job after
  `MaxConsecutiveFailures` and brings it back on a doubling backoff, and measures each run, so a
  plugin's own cost shows up in `Stats`.
- **Nothing shared that one plugin can spoil for another.** A worker processes items in order, so
  a shared one couples every plugin's IO latency and queue space; `Background.ForOwner` gives each
  plugin its own, keyed by the calling assembly so that two plugins choosing the same name do not
  end up sharing one after all. A shared public `HttpClient` let any plugin change headers or the
  base address for all; `SlHttp.CreateClient` gives each plugin its own settings over one
  connection pool, which adds no per-host connection limit of its own. The clock had static
  settings (failure threshold, default policy, backoff delays) and `MainThread.Budget` had a setter,
  so one plugin could change when every other plugin's jobs were disabled, or how much of each frame
  all callbacks got; the failure settings are now per job (`ClockJobOptions`), and the budget is the
  server owner's environment variable. `RateLimitedLog` had the same kind of static setters (burst,
  refill, summary interval) plus a `Reset` that wiped every plugin's counters, and
  `Background.Capacity` could resize the shared worker: the first are now a `RateLimitedLogger` each
  plugin creates for itself, the second is read-only. Two public methods acted on everybody too:
  `SlClock.OnRoundRestart` (cancels all plugins' Round jobs) is internal, and `SlClock.Reenable(name)`
  only touches the caller's own jobs. A server-wide setting such as the frame rate is not library
  code at all (see [`server_tickrate`](#server_tickrate-not-in-slkit)).
- **Bounded everywhere.** Worker queues have a capacity, HTTP has a concurrency limit, a waiting
  limit, a queue timeout, a request timeout and a body size cap, and the main-thread queue has
  `MaxPending`. Each limit, when it is hit, shows up as a return value, a result flag or a
  rate-limited warning, never as silent loss, and the flag says which limit it was.
- **`Server.Tps` hides hitches.** It is `round(1 / Time.smoothDeltaTime)`: smoothed, clamped and
  scaled. Mirror's `actualTickRate` is an integer per second and `fullUpdateDuration` is an
  average. Only a wall-clock measurement at the PlayerLoop boundaries shows each individual frame,
  which is why `FrameProfiler` exists and why `Snapshot()` shows the engine numbers only for
  comparison.
- **PlayerLoop edits go into the current loop.** Mirror registers `NetworkEarlyUpdate` and
  `NetworkLateUpdate` in `PlayerLoop.GetCurrentPlayerLoop()`. Starting from
  `GetDefaultPlayerLoop()` would drop all networking. SlKit copies the current loop, inserts marker
  types (an exact `Type` identity, so the edit is idempotent) and writes the loop back.
- **No cleanup on Disable.** `Plugin.Disable()` is never called, and the PlayerLoop survives scene
  loads. Round-bound work therefore needs `ClockScope.Round` (the default of `After`), which
  `Install` wires to `RoundRestarted` by itself: a default that silently did nothing until the plugin
  remembered a second call was not a safe default.
- **The main thread is checked before it is recorded.** `Install` is public, and the thread that
  first calls it used to be taken as the main thread unchecked, so a call from a worker recorded the
  wrong thread and edited the PlayerLoop from it. Unity knows its main thread from startup, and
  `Awaitable.MainThreadAsync().IsCompleted` exposes that answer without allocating, so SlKit asks it
  and refuses before touching anything.
- **Allocation-free steady state.** The server runs Boehm GC, which is non-generational, so every
  collection marks the whole live heap. The frame paths do not allocate: the clock tick, the
  drain, the profiler hooks and the once-per-second CSV capture into pooled buffers. Registering a
  job, `TryRun<T>` (one closure), every HTTP request (closures, the task, the body buffer) and
  `Snapshot()`/`Stats` (on demand) do allocate, by design. The checks on these paths (is the
  clock installed, is a disabled job due) are a field read or a comparison.
- **Verified engine API names.** These were checked against the installed
  `UnityEngine.CoreModule.dll`: `UnityEngine.LowLevel.PlayerLoop.GetCurrentPlayerLoop/SetPlayerLoop`,
  `PlayerLoopSystem { type, subSystemList, updateDelegate, ... }`,
  `UnityEngine.PlayerLoop.Update.ScriptRunBehaviourUpdate`, `Initialization` and `PostLateUpdate`.
