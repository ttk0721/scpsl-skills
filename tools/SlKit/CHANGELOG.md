# Changelog

SlKit follows [semantic versioning](https://semver.org). While the major version is 0, a minor
release may break the API; every break is listed here with a migration note. The assembly version
equals the release version.

## 0.2.0

The first version in this repository.

Binary compatibility: the public members of the 0.2.0 binaries that plugins may already be built
against stay in this DLL. Three are obsolete shims that exist only so such a plugin still loads, and
each has a migration: `SlClock.AutoWireLabApi()` (calls `Install`; delete the call),
`SlClock.MaxConsecutiveFailures` (read-only, the default 5; pass
`new ClockJobOptions { MaxConsecutiveFailures = n }` at registration) and `SlHttp.DefaultTimeout`
(read-only, `Default.Timeout`; use `SlHttpOptions.Timeout` on your own client from
`SlHttp.CreateClient`). `AdmissionLimit.Limit`, `BoundedWorkQueue<T>.Capacity` and
`Percentiles.Mean(double[], int)` also stay. A member is removed only in a release that bumps the
version and lists the removal here.

- `SlClock`: one PlayerLoop entry that drives every periodic, delayed and staggered job with
  absolute `Stopwatch` deadlines. Failure handling is per job (`ClockJobOptions`, `ClockJobHandle`,
  policies `Backoff` and `DisablePermanently`); `ClockScope.Round` jobs end at round restart without
  extra wiring; `Install` refuses other threads before touching anything; the first install logs the
  SlKit version and the main-thread budget and warns about other SlKit copies in the process.
- `MainThread`: a queue drained under a per-frame budget that belongs to the server owner (the
  environment variable `SLKIT_MAIN_THREAD_BUDGET_MS`), a backlog limit with a drop warning, and a
  thread check that is right before the clock is installed.
- `Background`, `BackgroundWorker`: one bounded worker thread per plugin (`ForOwner`, keyed by name
  and calling assembly) beside a shared default worker.
- `SlHttp`, `SlHttpClient`, `HttpResult`: HTTP that never blocks the main thread, with a client per
  plugin (timeouts, headers, concurrency and waiting limits, response size cap), results that say
  which phase ended a request and whether it was sent, and one shared transport.
- `RateLimitedLog` (one shared table with fixed limits) and `RateLimitedLogger` (a plugin's own
  limits, keys and counters).
- `FrameProfiler`, `FrameSnapshot`, `HotEventAudit`: frame timing that shows every hitch, with an
  optional CSV sink on its own worker, and a list of the subscribers of high-rate LabAPI events.
- `SlKit.Core`: the pure logic behind all of it, without Unity or LabAPI references and unit-tested on
  plain .NET (deadline timer, stagger planner, percentiles, ring buffer, token bucket, bounded work
  queue, interval gate, failure backoff, admission limit, bounded body read, copy report, owner
  registry, connection-limit rule, job options, host budget).
- `SlKit.Polyfills`: `MinBy`, `MaxBy`, `DistinctBy`, `Chunk` and `IsExternalInit` for the server's BCL.
- Tests: `SlKit.Tests` (Core), `SlKit.Clock.Tests` (the real `SlClock` and `MainThread` against
  stand-ins for Unity and LabAPI), `SlKit.Runtime.Tests` (`SlHttpClient` against a loopback server,
  `Background.ForOwner` from a second assembly, `RateLimitedLog`), `SlKit.Polyfills.Tests`.
- `Background.ForOwner` logs "the new capacity is ignored" once per worker, not on every call that asks
  for a different size after the thread has started.
