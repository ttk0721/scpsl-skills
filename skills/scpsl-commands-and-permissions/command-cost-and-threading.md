# Command cost, RA events and threading

Verified against LabAPI 1.1.7 / SCP:SL 14.2.7 (2026-10). Part of scpsl-commands-and-permissions. Figures marked (est.) are estimates, not measurements.

## Cost model

| Item | Cost | Notes |
|---|---|---|
| Dispatch of one command | cheap: one `Split` (string[] + N strings), two dictionary lookups, two LabAPI events (`GetInvocationList` + try/catch per subscriber), two event-args objects, a rich-text tag closer | once per command, at the rate the network delivers; capped near 50/s per connection by the shared bucket |
| Registration | reflection over plugin types once + one `Activator.CreateInstance` per attribute | zero per-tick cost: commands are never polled |
| Each RA reply | 2 byte[] serialisations + a fresh AES-GCM cipher object per call | send ONE short reply, not many `Respond` lines |
| RA command table | name, usage, description (<= ~83 chars), one entry per alias, pushed once per RA hub | every command and alias adds an entry |
| String permission check | `Player.Get` + LINQ/closure + group `Contains` + inheritance recursion | fine per command, not per frame x player |
| Game permission check | one AND on a ulong | free |
| `AddPermissions/RemovePermissions` | YAML serialise + file write on the main thread | per call |

The shared rate bucket is 150 uses per 3 s per connection by default (`ratelimit_commands_threshold`, `ratelimit_commands_window` in the gameplay config). RA, dot commands and admin chat draw from the SAME bucket. Overflow silently drops the command and, with `ratelimit_kick` true (default), KICKS the player. A keybind or macro that fires dot commands will kick its owner. Debounce client-side, prefer SSS events (see scpsl-server-specific-settings), and add a per-player cooldown to commands that allocate, scan `Player.List`, spawn objects or write files.

## RA player list and info events

The RA panel asks for the player list and per-player info through its own channel (not commands). The list is rebuilt from scratch on every request:

| Event | Frequency | What it costs you |
|---|---|---|
| `RequestingRaPlayerList` | per request, cancellable | once per list request |
| `RaPlayerListAddingPlayer` (cancellable) and `RaPlayerListAddedPlayer` | per LISTED player per request (`AddedPlayer` only for players the Adding handler left allowed) | up to N calls per event per request per admin |
| `RequestedRaPlayerList` | per request | once |
| `RequestingRaPlayerInfo` / `RequestedRaPlayerInfo`, `...PlayersInfo` (multi-select) | once per info click | builds large strings; keep light |
| `RequestedCustomRaInfo` | once, when the selection matched no player | |

- Vanilla list cost: about 0.3-0.5 ms CPU and 100-150 KB garbage per request at 100 players (estimate; the alphabetical sort is the slow case). Info clicks emit about 5 separately encrypted messages.
- A subscriber to `RaPlayerListAddingPlayer` runs plugin code for every player on every refresh of every open RA panel. Keep it O(1) and allocation-free: no `Player.List` scans, no I/O, no logging. If you only decorate names, prefer `Player.CustomInfo` or badges.
- How often the RA client refreshes the list is client-side and UNVERIFIED. Worst case is bounded by the rate limit (est. ~50 req/s per admin).
- See labapi-events and its catalog for the frequency classes before subscribing.

## Threading

- RA and dot commands are executed on the Unity main thread. Packets are queued by a background poll thread and the transport's `LateUpdate` drains the queue and invokes the handler synchronously. Console input is queued from reader threads and executed in the main thread's `FixedUpdate`. So `Execute` may touch Unity objects, `Player`, `NetworkServer`.
- There is NO async command API. A slow `Execute` stalls the whole server tick. Do not use `.Result`, `.Wait()`, `Thread.Sleep`, synchronous HTTP, or large file IO. (scpsl-lint SL007 flags these in event handlers and `Update`-like methods; its README does not list `ICommand.Execute` as a handler, so review commands by hand.)
- Shared static state (cooldown tables, caches) needs no lock when touched only from commands and events: all main thread. The handler dictionaries are plain, non-concurrent: never register commands from a background thread.
- `Server.RunCommand` from a non-main thread would run `Execute` off-thread, which is unsupported (inference).
- Slow work pattern: (1) validate and snapshot plain data on the main thread, (2) start the work off-thread (`Task.Run` + `ConfigureAwait(false)`, or `TryRun` on the plugin's own SlKit worker from `Background.ForOwner`), (3) answer later on the main thread (SlKit `MainThread.Post`, or the `onMainThread` callback of that worker's `TryRun` or of the plugin's own `SlHttp.CreateClient` client), (4) re-resolve the player there (they may have left) and reply with `Player.SendConsoleMessage`, a hint, or `Server.SendAdminChatMessage`. `sender.Respond` after the command returned only works for an RA sender whose hub still exists. Return a "working..." response immediately.
- The off-thread part touches only strings, numbers and POCOs. **REQUIRED SUB-SKILL:** scpsl-main-thread-and-timing.

## Re-verifying on a new build

- Transport and dispatch: read how received messages reach the encrypted-channel handler (queue drained in the transport's `LateUpdate`?), and `ServerConsole` input handling.
- Rate limit: defaults of the commands bucket and `ratelimit_kick` in the config file, and which paths consume the bucket.
- RA list: `RaPlayerList.ReceiveData` event raising per listed hub; measure with a 60-bot server and a stopwatch around your own handler before claiming a cost.
- `tools/catalog/labapi-events.json`: frequency class of the `RaPlayerList*` events.
