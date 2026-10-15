# Delivery policy: status classes, retries, idempotency, state names

Verified against LabAPI 1.1.7 / SCP:SL 14.2.7 (2026-10). Companion to SKILL.md. The code is
`DeliveryPolicy.cs` and `WebhookSender.cs` in `examples/adding-external-integrations/`; this file
explains the choices. Discord API facts below come from Discord's public documentation, not from
this repository: see "Re-verifying on a new build".

## Status classes

| Outcome | Class | Action | The entries are |
|---|---|---|---|
| 2xx (Discord answers 204) | delivered | remove the batch | gone, counted `Delivered` |
| 0: timeout, transport error, or never sent | transient, **ambiguous** (not for `Sent == false`) | back off, re-send the same batch id | deferred |
| 429 | transient | wait the `retry_after` of the body (0.5 to 120 s), else back off | deferred |
| 408, 5xx | transient | back off | deferred |
| 401, 403, 404, leftover 3xx | sender is wrong or revoked | stop sending, keep the data, log once, re-arm on `labapi reload configs` | deferred (kept) |
| other 4xx (400, 413, 422) | poison payload | drop this batch, count items, log the status | dropped |
| anything else (1xx, negative) | unforeseen | stop like 401; never guess toward discarding | deferred (kept) |

`StatusCode == 0` means no response headers arrived: `TimedOut`, a transport `Error`, or a request that was
never sent (`Rejected`, `QueueTimedOut`; both have `Sent == false`, so a resend cannot duplicate).
A failed result can also carry a non-zero status (headers arrived, then the body failed or was `TooLarge`),
so test `Success` for "complete 2xx" and classify by `StatusCode`, never "failed means status 0". `HttpResult`
does not expose response headers, so the 429 wait is read from the JSON body.

## Names mean what the code does

- **Deferred**: still in the outbox, will be tried again.
- **Dropped**: removed and never tried again. Only a poison batch (other 4xx) is dropped on purpose.
  Queue overflow and expiry also remove entries and have their own counters (`Overflowed`, `Expired`).
- Count items, not attempts: `Attempts` and `FailedAttempts` count requests; `Delivered`, `Dropped`,
  `Overflowed` and `Expired` count entries. One stuck batch must not inflate "dropped" per retry.
- Be able to answer "after the last failure, will this entry be sent again?" with yes or no. The
  per-round log line (`Describe()`) prints all of the counters, so state is visible without a command.

## Bounded retry

- Back off 2, 4, 8, 16, 32, 60 s with plus or minus 20 % jitter, per batch.
- After 6 failed attempts, cool down 5 minutes, then start the count again. The batch is deferred, not
  dropped, so the worst case against a dead receiver is 6 requests per 5 minutes.
- Entries older than `MaxAgeHours` are discarded instead of being sent late (`PruneOlderThan`, only
  while no batch is pinned). The queue is capped by `MaxQueued`; a full queue refuses the new entry.
- Never retry in a loop inside one tick. One request in flight, one decision per answer.

## Idempotency: the receiver decides

Every entry gets a key (`run-seq`, the run id is random per process start), and a batch is the first
key plus a count. The batch is pinned, so every retry within one run carries the same id. After a
restart the batch is re-formed from the reloaded file: the first key is the same, but the count can
differ (entries queued after the batch was pinned are in the file too), so the id can differ. A receiver
that must not duplicate should therefore also key on the entry key (`run-seq`), not only on the batch id.

- Discord has no idempotency key. A timeout can mean "processed, answer lost", so a duplicate is
  possible. The batch id in the last line (`-# batch id`) makes a duplicate recognisable. That is
  at-least-once, and it is the honest guarantee.
- A panel or API you control: put the batch id and the entry keys in the body and give the table a unique
  key on `(server_id, entry_key)`. On a conflict the receiver skips that entry and answers 2xx, so the
  retry succeeds. Without this the id is decoration.
- Sums are sent as deltas under a batch id. Records (best streak, maximum) are sent as absolute values
  and merged with max, because adding "5" twice is wrong and `max(5, 5)` is not.

## Why the batch is pinned

The head of the outbox is the batch in flight. If anything else removed entries from the head (drop
oldest on overflow, age pruning), the removal after a 2xx would delete the wrong entries. So: new
entries only append; removal happens only through `RemoveFirst(n)` for the pinned batch; pruning and
re-batching happen only when nothing is pinned.

## Silent-failure checks

- A lost callback must not stop delivery forever: the in-flight flag has a watchdog (timeout plus
  10 s), and a send id makes a late callback of the abandoned send a no-op.
- Do not flush on every `Left`: with 60 players a restart sends `Left` for everyone. Queue it and
  let the tick send it.
- The plugin worker's `TryRun` returns `false` when its queue is full: leave the outbox dirty and retry next tick.
- Log through `RateLimitedLog`, log the status and a redacted message, never the webhook URL. A transport
  exception message can contain the request URL, so run anything derived from a response through a redactor.

## Re-verifying on a new build

- `SlHttpClient.PostJsonAsync` signature and `HttpResult` members (`Success`, `StatusCode`, `Body`, `Error`,
  `Rejected`, `QueueTimedOut`, `TimedOut`, `Sent`) in the SlKit README; `HttpResult` still has no headers.
- `ServerEvents.Shutdown` still raised synchronously before the process exits; the wait after it is
  short (well under two seconds), so no network work belongs there.
- Discord (check the current documentation): `content` limit 2000 characters, `allowed_mentions.parse`,
  `retry_after` in the 429 body, `<t:unix:T>` timestamps, `-#` subtext, webhook limit about 5 requests per
  2 seconds (this recipe sends at most one per tick, default 5 s).
- After editing `DeliveryPolicy`, `DiscordPayload` or `Outbox`, re-check them on plain .NET: they
  reference no game types, so a scratch console project that includes the three files runs them. Useful
  assertions: every status maps as in the table, the escaped output parses with a real JSON parser for
  all 32 control characters and a lone surrogate, and an older write generation never overwrites a newer
  file. The sender itself needs the game assemblies and is verified on a test server.
