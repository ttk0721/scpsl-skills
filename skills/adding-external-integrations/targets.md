# Targets: Discord, panel API, database, reads

Verified against LabAPI 1.1.7 / SCP:SL 14.2.7 (2026-10). Companion to SKILL.md. What changes when the
receiver is not the Discord webhook of the example. Code fragments here are sketches, not compiled.

## Discord webhook

- The URL is the secret. Keep it in `config.yml`, keep that file out of version control, never log it.
- Body: `content` (at most 2000 characters), `allowed_mentions` with an empty `parse`. Without it a
  nickname such as `@everyone` pings the channel.
- Escape every control character, not only quote, backslash and newline. A tab in a nickname turns hand
  built JSON into a 400 and the whole batch is dropped.
- Put untrusted names in code spans and replace backticks; strip format characters (bidi overrides,
  zero width) so a name cannot reorder the line.
- Show the event time with `<t:unix:T>` (the reader's own time zone), not the send time: a batch that waited
  out an outage stays truthful.

## Panel or REST API you control

- Add the batch id and the per-entry keys to the body and a unique key on `(server_id, entry_key)` on the
  receiver (a batch id alone can change across a restart, see delivery-policy.md); skip a seen
  entry and still answer 2xx (see delivery-policy.md).
- **Authentication.** Since SlKit 0.2 there is no shared public `HttpClient`: each plugin makes its own
  `SlHttpClient`, and a header put in its `DefaultHeaders` is sent by that client only. Make it once in
  `Enable`; the callback comes back on the main thread and the task never faults:

```csharp
// Sketch. Enable: one client for this plugin, the token lives only in its options.
SlHttpOptions options = new SlHttpOptions { Timeout = TimeSpan.FromSeconds(10), MaxConcurrency = 1, MaxQueued = 4 };
options.DefaultHeaders["Authorization"] = "Bearer " + Config.ApiToken;   // never logged
_http = SlHttp.CreateClient(Name, options);

// Any thread. Snapshot data only; OnResult runs on the main thread.
_http.PostJsonAsync(url, json, result => OnResult(result));

// Per-request header or a method other than GET/POST: the factory builds a fresh message per call.
_http.SendAsync(() => new HttpRequestMessage(HttpMethod.Put, url) { Content = new StringContent(json, Encoding.UTF8, "application/json") }, OnResult);
```

  In `OnResult` classify `result.StatusCode` with the same policy and read the phase before you retry:
  `Rejected` and `QueueTimedOut` were never sent (this client's own backlog, safe to resend), `TimedOut`
  was sent and may have been processed. Do not test `StatusCode != 0` for failure: use `Success`, because a
  failed result (body cut off, too large) can still carry the real status. A response body above
  `MaxResponseBytes` (1 MiB by default) ends as `TooLarge` with no `Body`.
- Reads (a player's totals at join): never make joining wait. Show a default, fetch asynchronously,
  and when the answer lands re-resolve the player by `UserId` on the main thread; they may have left, and
  after a round restart every old wrapper is dead. Cache with a time limit and fall back to the cache on
  transient failures.
- Rate: batch per tick, one in flight. Do not turn a per-kill event into a per-kill request.

## Database

- A database driver is an extra dependency compiled for the server's runtime. Put it under `dependencies/`
  (deploying-scpsl-plugins) and prove it loads before designing around it. The simplest robust design is a
  small HTTP API in front of the database: you get the status classes, the batch id and the outbox for free.
- The static `Background.TryRun` is ONE worker for every plugin that shares `SlKit.dll`. A slow query on it
  delays every other plugin's background job, including their file writes. Give the plugin its own worker
  (`Background.ForOwner(Name, capacity)`, once in `Enable`) and keep each job short: the worker runs one item
  at a time, so a query that blocks for seconds still holds up the plugin's own writes behind it. For direct
  queries that can block for long, use a dedicated thread with its own bounded queue, the same snapshot
  rules, and the same outbox.
- Make writes idempotent: key each row by the entry key and use insert-or-ignore, one transaction per batch.
- Never query from a handler, a command handler or a `SlClock` job.

## Shutdown and crashes

- `Disable()` is never called. At `ServerEvents.Shutdown` write the outbox file synchronously and return:
  the process exits shortly after, so there is no time to send. A request in flight is not awaited;
  its batch is still in the outbox and goes out again at the next start.
- A crash or SIGKILL fires nothing. The loss window is `FlushSeconds` of entries not yet persisted. State
  that number in the design note instead of promising zero.
- The outbox file is per-port (the plugin config directory), so two servers on one machine do not share it.

## Re-verifying on a new build

- `LabApi.Loader` still exposes the `GetConfigDirectory()` extension used for the outbox path.
- `SlHttp.CreateClient` still gives each plugin its own client with its own `DefaultHeaders`, and no public shared `HttpClient` exists (SlKit README).
- A candidate database driver: load it on a test server before relying on it (sl-probe can log what loaded).
