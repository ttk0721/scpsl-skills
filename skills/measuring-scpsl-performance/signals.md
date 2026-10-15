# Frame-rate signals: what each one really measures

Verified against LabAPI 1.1.7 / SCP:SL 14.2.7 (2026-10). Read this before trusting any "TPS" number.

## What paces the server

- One Unity main thread on the standard PlayerLoop. `Application.targetFrameRate` paces it. Physics is a separate fixed step (0.02 s per project settings, not re-read from this build).
- Writers of `targetFrameRate`, in order at boot: headless init, the config value `server_tickrate` (via a game setter), then Mirror's host start, which sets it to `NetworkManager.sendRate` (default 60). **The configured value is therefore overwritten** and only returns after an idle-mode exit, a config reload, or a plugin assigning `Server.MaxTps` again (a no-op if `sendRate` already equals the config value).
- Consequence for measurement: record `Application.targetFrameRate` and `NetworkManager.singleton.sendRate` next to every sample. A server configured at 20 that actually targets 60 burns more CPU than you think, and a fixed "60" threshold would alert permanently on a server that really targets 20.
- Fix for the override (apply while not idle, replace `-1` and `0` with a cap, never lower the owner's value): re-assign `Server.MaxTps` in `ServerEvents.WaitingForPlayers`. The standalone `tools/tickrate-fix` plugin (the server owner installs and enables it; it is off by default) does this and skips while idle. Each assignment prints a console line; do not call it per frame. Whether the override happens on your build is visible as a differing `targetFrameRate` before and after.
- With `server_tickrate: 20` the game prints "Server tickrate set to: 20", yet `targetFrameRate` is 60 at `WaitingForPlayers` and the server runs at 59.9 fps (measured, 14.2.7, no players); after the plugin re-assigns it, 20.0 fps and about half the main-thread load. The numbers are in `tools/tickrate-fix/README.md`.
- Network input arrives in batches: the transport poll thread sleeps about 30 ms between polls, so input latency has a floor independent of tickrate.

## Signal table

| Signal | Formula / source | Lies about | Honest use |
|---|---|---|---|
| `Server.Tps` (LabAPI) | `Math.Round(1f / Time.smoothDeltaTime)` | Low-pass filtered, so isolated 100-300 ms hitches barely move it. `deltaTime` is clamped by `maximumDeltaTime` (0.333 s in project settings, unverified for this build), so a 2 s freeze reads as 0.333 s and the value never goes below about 3. Probably scaled by `timeScale` (UNVERIFIED for `smoothDeltaTime`), in which case it is meaningless in idle; whether it reads about 1 or about 100 there is UNVERIFIED too. | Compare column in a report |
| `tps` command | Same formula, printed as `TPS: x/y` where y is the config value | The denominator is the config value, not what the loop targets | Nothing |
| EXILED `Server.Tps` | Capped `1/Time.deltaTime` plus a smoothed variant | Same smoothing and clamping, plus a cap at the configured rate | Nothing |
| `NetworkServer.actualTickRate` | Frames counted per window of at least 1 s on `Time.unscaledTimeAsDouble` | Integer, one value per second, hides everything inside the window. Whether unscaled time escapes the `maximumDeltaTime` clamp is UNVERIFIED, so it may overstate after a long freeze. Reads 0 while `NetworkServer.active` is false | Cheap cross-check |
| `NetworkServer.fullUpdateDuration.average` (also `earlyUpdateDuration`, `lateUpdateDuration`) | Stopwatch EMA in seconds over `sendRate` samples. Spans from the Mirror early-update hook (end of EarlyUpdate) to the end of the Mirror late-update hook | Excludes Initialization, most of EarlyUpdate, PostLateUpdate and the frame-limiter wait. An average, so spikes vanish | Share of the frame spent in FixedUpdate, Update, LateUpdate and Broadcast |
| PlayerLoop `Stopwatch` markers | `Stopwatch.GetTimestamp()` at frame start and end | Nothing except one assumption: the limiter wait sits before the first marker (UNVERIFIED). If it does not, the wait shows up in busy time while period stays correct | Ground truth |

## Reading the markers

- **Real TPS** = 1000 / mean(period ms). **Load** = mean(busy) / mean(period).
- Period near the target with low busy means idle headroom. Period above the target means the frame is late. Busy close to period means no headroom, so the next spike turns into a hitch.
- Rubber-banding with clean frame time is not a CPU problem. Check uplink (voice from several speakers is the big consumer; an ESTIMATE for 10 speakers at 100 players is 76-126 Mbit/s) and per-peer bytes sent, for example by enabling LiteNetLib statistics.

## Idle mode

- Idle (when `idle_mode_enabled` is on) sets `timeScale` to 0.01 and the frame rate to `idle_mode_tickrate` (default 1). It engages only when the hub count is 1 (the host). `Time.*`-based numbers, including `Server.Tps`, are not TPS there. Skip such windows: check `Server.IdleModeActive` (and drop frames whose period is far above the target).
- Idle can begin while a round is still in progress if the last player leaves, because the round-end rule keeps an empty round alive. Do not alert on that.
- A permanent dummy or NPC hub keeps the server out of idle (hub count stays above 1), which also changes what your baseline means.

## Physics steps and GC

- At 60 FPS there are 0 or 1 physics steps per frame, 2-3 at 20 FPS, and about 50 per frame while idle (derived, UNVERIFIED). To see it, insert a counter system into the FixedUpdate phase and count calls per frame.
- GC is Boehm: non-moving, non-generational, 3 ms incremental slices. Mark work scales with the live heap, and the allocation rate decides how often a cycle starts. Correlate long frames with `GC.CollectionCount(0)` deltas per second. The game itself forces a full collection at scene load and before a round restart, so those frames are expected to be long.

## Re-verifying on a new build

- Dump the top-level `PlayerLoop.GetCurrentPlayerLoop().subSystemList` type names once at startup and confirm the phase names and that the limiter wait is before Initialization.
- Read `NetworkManager.singleton.sendRate` and `Application.targetFrameRate` at `WaitingForPlayers` with `server_tickrate` set below 60. Confirm the override still happens.
- Check LabAPI `Server.Tps` is still the same formula (LabAPI is public: `LabApi/Features/Wrappers/Server/Server.cs`).
- Confirm `NetworkServer.actualTickRate` and `fullUpdateDuration` still exist and are public in the shipped Mirror.
- Measure whether `maximumDeltaTime` still clamps: freeze the main thread for 1 s in a test plugin and compare `Server.Tps`, `actualTickRate` and your markers.
