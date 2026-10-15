# Accounting, persistence and flushing

Verified against LabAPI 1.1.7 / SCP:SL 14.2.7 (2026-10). Part of adding-per-player-tracking.

## What is the unit

| Meaning | Key | Survives restart | Needs |
|---|---|---|---|
| Per hub (lives at most one round) | `Player`/hub, or `UserId` reset at `Left` | No | Nothing. Say "per round" in the output |
| Per account, cumulative | `UserId` | Yes (statics survive) | Totals dictionary outside the round owner; journal for the process |
| Per visit (one stay of a human) | `UserId` plus parking across the restart-time `Left`/`Joined` | Yes | The parking pattern in scpsl-game-lifecycle (players.md) |

The example is the second row, which is what "playtime" almost always means. A restart sends `Left` for every non-host hub and then `Joined` again after re-authentication, so a session opened on `Joined` and closed on `Left` silently becomes per-round. Accumulating into `UserId`-keyed totals needs no parking: there is no session object to split.

`UserId` is an identity only for authenticated online-mode players. Offline mode gives a non-empty per-connection id (`ID_Offline_...`), dummies share `ID_Dummy`, the host has its own. The example rejects the `ID_` prefix and logs a rate-limited warning. On an offline server there is no per-account tracking; say so and fall back to per-hub, or key by something you control.

## Time accounting

- Credit the interval since the player's own last sample, from the same `Stopwatch` the clock uses (`StopwatchTime.Now`). Do not add a constant period per tick: staggered jobs and skipped periods make the real interval vary.
- Clamp each interval to about two periods and count the excess (`DroppedSeconds`). A freeze, or one 1 fps idle second, then does not become played time, and the loss is visible. Clamp on purpose, never silently.
- Do not use `Time.deltaTime` or MEC waits for this: idle mode (`timeScale` 0.01, 1 fps) scales them, and hitches longer than the engine's maximum delta are lost.
- Credit the open interval in `Left` and in the shutdown flush, or up to one period per player is lost at every leave and at every stop.
- The first interval after a role change is not credited (conservative: Overwatch or lobby time must not count). Error: at most one period per spawn.

## State owner and reset

One `RoundState` object holds everything that belongs to the current world (here: per-hub baselines). `WaitingForPlayers` replaces the reference. Never edit entries in place and never keep `Player` or `ReferenceHub` in it: only `UserId` and plain numbers. `Joined`, `Left` and `WaitingForPlayers` all re-fire every round, so each handler replaces or removes, never appends blindly.

Reset or keep, by what the state points at:

| State | Points at | At a round restart |
|---|---|---|
| Position, look, baselines, open interval, last bucket | The old scene's hubs | Drop (replace the round owner) |
| Totals, pending deltas | Only `UserId` | Keep |
| AFK clock (last movement time, last-seen time) | Only `UserId` | Keep. Resetting it gives an AFK player free credit every round (afk-and-activity.md) |

Resetting something that is account-level at a round event is the same bug as caching a `Player` across rounds, in the other direction.

`ClockScope.Server` jobs survive restarts, which is right when the job holds nothing from a round. A job that does belongs on `ClockScope.Round`. `SlClock.Install` hooks `RoundRestarted` for you, which a `softrestart` does not raise (scpsl-game-lifecycle, restart-and-state.md).

## Journal, not a rewritten file

| Option | Per flush | Crash loses | Problem |
|---|---|---|---|
| Rewrite a totals file | O(all accounts ever seen) | Everything if torn | Grows with history; rename dance every time |
| Append deltas (example) | O(accounts that changed) | One flush interval; damaged or cut lines are dropped | Needs summing at boot; compacted there |
| Database or panel | One request per batch | Whatever is not acknowledged | Idempotency and retry policy (below) |

At boot the journal is summed and rewritten compacted (tmp file, then replace), once, before any player exists. The journal grows only between boots.

### Line format: a cut line must not look valid

One line per account per batch: `J1`, batch id, `UserId`, three values, CRC-32 of everything before it, then a newline (tab separated). The reader keeps a line only if it has the version and exactly seven fields, the checksum matches, the values are finite and not negative, and the line ended with `\n`. Anything else is dropped and counted. Each part earns its place:

- Without a checksum, a line cut in the middle of a number (`...12` instead of `...123.0`) still parses, as a smaller value. Splitting on tabs and parsing the fields cannot see that.
- Without a required newline, a cut that lands exactly after the checksum verifies. The missing terminator is the only sign the write stopped, and `File.ReadAllLines` hides it, so read the text and split it yourself.
- The batch id makes a repeated write harmless: the reader counts each (batch id, account) once and reports the rest as duplicates.
- A failed append can leave a half line at the end. The next append starts with a newline when the file does not end with one, or the retry glues itself to the fragment and fails its own checksum.
- An id containing a tab or a newline would split across fields, so it is not accepted as an account id.

## Flush protocol

The rule: every re-send of a batch uses the batch's own id and content, and the journal tolerates seeing a line twice. "Either in memory or in flight, never both" is false. A write can stop halfway (disk full) and leave part of the batch in the file, and a worker can finish just before `Shutdown` while its confirmation never reaches the main thread. Merging a failed batch back into the pending seconds is the bug: the retry would get a new id and the lines already written would count twice.

1. Main thread: copy dirty accounts into a named `Batch` (id, strings and doubles), zero their pending counters, clear the dirty set. The batch stays in a book (in flight) until the worker confirms it.
2. The plugin's own worker (`Background.ForOwner(Name, capacity)`, created once in `Enable`): `TryRun(work, onMain, onError)`. `work` appends the batches and touches nothing else. It returns `false` when the queue is full.
3. In `onMain`: remove the batch from the book. On `false` or in `onError` (main thread): move it to a retry list unchanged. The next flush sends the retry list again, with the new seconds in a batch of their own. Bound the retry list and count what it drops.
4. Shutdown: credit open intervals, then write on the calling thread everything not confirmed: the retry list, the batches still in flight (the worker may or may not have written them, and it dies with the process; its callbacks are never drained again) and the new seconds. Wait for the file lock with a timeout so a stuck worker cannot hold up the exit. A fire-and-forget write usually never lands, and disposing the writer right after starting it kills the write in flight.

Lock file IO: the worker and the `Shutdown` handler can write at the same moment.

The example keeps this bookkeeping in `FlushBook.cs` and the format in `JournalStore.cs`, both free of game references, so `tests/PerPlayerTracking.Tests` runs on plain .NET: truncated line, corrupted checksum, duplicate batch, missing newline, partial write then retry, in-flight batch at shutdown.

Never `.Result`/`.Wait()` anything on the main thread; never write the file from an event handler. `Shutdown` also does not run on a crash or SIGKILL, so the periodic flush is the only protection against those: pick `FlushSeconds` from how much you can lose.

## Sending totals to a panel or database

Same snapshot rule, plus a failure policy you write down before coding: which statuses retry (transient, bounded), which stop (other 4xx), what the terminal state is, and whether the receiver is idempotent (a batch id and upsert, not "add these seconds" twice). The plugin's own `SlHttp.CreateClient` client does the request without blocking; classify by `StatusCode` and `Sent`, not by "failed means status 0". A max-style value (best streak) is sent as a value, not a delta. Two servers sharing an account: each journals under its own port folder; sum at the receiver, tagged by server id. The full design of that hop is adding-external-integrations; the IO checklist item is in designing-scpsl-plugins.

## Cost numbers (estimates, not measurements)

At 60 players and 5 s: 12 samples per second. Each is a dictionary lookup, a role read, two cached reads, a few float operations. Refill is one `ReadyList` pass per period (a lazy LINQ enumerator, so one small allocation per period). The flush allocates one `Batch` (with its `Delta[]`) plus a closure per interval. Measure with `SlClock.Stats` (average and max µs per job) before claiming anything.

## Re-verifying on a new build

- `Player.ReadyList` still excludes the host and still filters on `IsReady`?
- `Joined`/`Left` still re-fire for every player at a restart (log them on a live server through a restart)?
- `ServerEvents.Shutdown` still fires on `restart_after_rounds` and on a console `exit`?
- SlKit: `EveryStaggered` source called once per period; `Background.ForOwner` and `BackgroundWorker.TryRun<T>`: signature, callback thread, and that the worker thread is a background thread (it dies with the process).
