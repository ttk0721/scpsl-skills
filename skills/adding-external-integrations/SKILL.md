---
name: adding-external-integrations
description: "Use when an SCP:SL plugin must send data off the server or fetch it: a Discord webhook, a web panel or REST API, a stats upload, a ban or audit feed, a database; or when asked for retries, batching, an outbox, \"must survive restarts\", or when a webhook spams, drops messages, or pings @everyone."
---

# Adding external integrations

Verified against LabAPI 1.1.7 / SCP:SL 14.2.7 (2026-10).

## Overview

Whatever leaves the server is slow, fails, and answers late, and the game has one main thread. The main thread only snapshots and enqueues. One state machine sends through `SlHttp`, forgets an entry only after a 2xx, and persists the rest. Compiled reference: `examples/adding-external-integrations/` (`WebhookReporterPlugin.cs`, `WebhookSender.cs`, `DeliveryPolicy.cs`, `DiscordPayload.cs`, `Outbox.cs`).

## When to Use

- A webhook, panel or REST call, stats upload, audit feed, database write.
- Symptoms: freezes when the network is down, messages lost across a restart or outage, duplicates after retries, `@everyone` from a nickname, HTTP 400 from a tab in a name, retries against a deleted webhook.
- **Small addition** (one ping on one event, loss acceptable): snapshot, `PostJsonAsync` on your own `SlHttp.CreateClient` client, rate-limited failure log. Stop there; no outbox.
- Thread rules in general: scpsl-main-thread-and-timing.

## Decisions

| Decision | Default | Otherwise |
|---|---|---|
| May a report be lost? | Yes: fire-and-forget, failure counter | Bans, rewards, stats: outbox |
| Delivery | At-least-once, same batch id on every retry | Exactly-once needs a receiver-side unique key |
| Batching | One request per tick, at most 10 entries | One request in flight even for single items |
| Persistence | File, written each tick if changed, synchronously at `Shutdown` | None when loss is fine |
| Queue full | Refuse the new entry, count it | Drop-oldest only if nothing is pinned |
| Failures | By status class: [delivery-policy.md](delivery-policy.md) | Never one rule for all non-2xx |
| Secrets | Config only, never logged; an auth header goes in your own client's `DefaultHeaders`, never on a client other plugins use | [targets.md](targets.md) |
| Database | An HTTP API in front of it | Own thread: [targets.md](targets.md) |

## The pattern

1. **Snapshot on the main thread.** Handlers copy names, ids and numbers into one sanitised string and enqueue. No `Player` outlives the handler; no I/O in it.
2. **One 1 Hz `SlClock` job** decides if a flush is due, reading `Config` at use time.
3. **Pin the batch** with an id that stays the same on retry. Only `RemoveFirst(n)` touches the head until it is delivered or dropped.
4. **Send with your own client.** In `Enable`, once: `_http = SlHttp.CreateClient(Name, options)` and `_worker = Background.ForOwner(Name, capacity)`. Then `_http.PostJsonAsync(url, json, onMainThread)`; the callback runs on the main thread. Size `MaxConcurrency` and `MaxQueued` for one request in flight. The static `SlHttp.PostJsonAsync` and `Background.TryRun` use one default client and one worker for every plugin together, so another plugin's backlog becomes your `Rejected`. A watchdog clears a stuck in-flight flag; a send id discards a late callback.
5. **Classify** (`DeliveryPolicy.Classify`, by `StatusCode`; `Success` is only 2xx with a complete body, and a failed result can still carry the real status). `Rejected` and `QueueTimedOut` mean not sent (`Sent == false`), safe to retry as is; `TimedOut` means sent, the receiver may have processed it. Delivered removes the batch, transient backs off and keeps it, a wrong or revoked URL stops the sender and keeps the data, a rejected payload drops that batch (counted).
6. **Persist** through your own worker's `TryRun` with a generation number against stale writes. At `ServerEvents.Shutdown` write synchronously; never wait for the network.
7. **`WaitingForPlayers` resets per-round counters only.** The outbox outlives rounds.

```csharp
bool queued = _worker.TryRun<bool>(   // _worker = Background.ForOwner(Name, capacity), made once in Enable
    () => { _store.Write(generation, OutboxStore.Serialize(snapshot)); return true; }, // worker: snapshot only
    ok => { },
    error => _outbox.Dirty = true);   // main thread: try again next tick
if (queued) _outbox.Dirty = false;
```

Discord: `"allowed_mentions":{"parse":[]}`, every control character escaped (`DiscordPayload.AppendEscaped`), untrusted names in code spans.

## Common Mistakes

| Wrong | Right |
|---|---|
| Cooldown or "sent" set before the answer | State changes on the result; a failed send must not look like success |
| JSON escaping only `"`, `\`, `\n` | All control chars below U+0020 and lone surrogates, or Discord answers 400 |
| No `allowed_mentions` | Parse nothing, sanitise names |
| `HttpClient.Timeout` set after first use | `SlHttp`'s per-request timeout |
| Flush and dispose the client in `Disable()` | It never runs: flush at `Shutdown`; an `SlHttpClient` holds no thread or socket, so there is nothing to dispose |
| Retry creates a new batch id | Stable id kept with the entries, or "deduplicated" is only a claim |
| `ContinueWith` mutating state on a pool thread | `MainThread.Post`, one owner |
| Empty `catch { }` around the send | Count it, log the status (never the URL) |
| Record (best streak) sent as an increment | Deltas for sums, absolute max for records |

## Domain skills

scpsl-main-thread-and-timing, scpsl-game-lifecycle, labapi-events, designing-scpsl-plugins, scpsl-engine-cost-model, deploying-scpsl-plugins, reviewing-scpsl-plugins.
