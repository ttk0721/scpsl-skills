# Cost table for plugin actions

Verified against LabAPI 1.1.7 / SCP:SL 14.2.7 (2026-10). N = ready connections (players plus the host loopback), O = spawned identities. Wire figures are per receiver, computed from serializers, not captured. CPU figures are estimates unless stated.

| Action | Main thread | Wire (per receiver) | Scaling |
|---|---|---|---|
| `Player.SendHint` to 1 player | one pack, a `TextHint` and a parameter array | text + ~15-25 B (bracket, UNVERIFIED) | N hints = N independent packs, nothing shared |
| `Server.SendBroadcast` (all) | payload built once, N repacks of the RPC | 13 B RPC header + text + ~5 B | O(N) |
| `Player.SendBroadcast` | one pack | same | O(1) |
| `SendConsoleMessage` | AES-GCM per message when the player has a key: new cipher, 32 B random nonce, several arrays | ~60 B + text | N independent encryptions; per-message cost UNVERIFIED |
| Spawn 1 primitive toy | per-connection serialization + pack, synchronous | ~133 B once | x N; 1744 toys ~ 228 KiB per player |
| Hold 1 static toy | share of the per-tick observer walk | 0 B/s | CPU only |
| Hold 1 non-static toy | + LateUpdate (~0.3-0.5 us, estimate) | 0 B/s if unchanged | O(toys) per frame |
| Move 1 primitive | dirty each frame | pos 48 B, +rot 64 B, +scale 76 B per delivered update | rate = min(frames, 1/SyncInterval) |
| Move a static toy | ignored | 0 B | silent no-op |
| Any SyncVar change | equality check, dirty bit | floor ~28 B + value | once per `syncInterval`; same value is free |
| `TextToy` text/arguments | string alloc, SyncList op per change | string + overhead | interval 0 = every change goes out |
| Spawn 1 pickup | spawn + physics body | ~95 B (unverified sum) | x N once |
| Pickup in motion | physics tick | ~50 B every 0.25 s | 4 Hz x N while awake |
| Despawn | destroy to observers | 6 B | x N |
| `Player.Scale` set | one pack, N sends | ~19 B | not coalesced, no equality check |
| `Player.Position` set | one override message | 14 B to the owner only | avoid per frame |
| `RoleSyncEvent` | N x (N-1) calls per tick + a pooled writer per pair | role message only on change | CPU always |
| `ValidatedVisibility` subscriber | args object + 2 `Player.Get` per pair even with ZERO subscribers; subscriber adds `GetInvocationList` + handler | omitted target saves 2-13 B/tick | N x (N-1) per tick; ~40 B x N(N-1) x rate of garbage (~5.9 MB/s at 50 players, estimate) |
| Proximity voice, speaker -> receiver | loop over all hubs per frame, args alloc per receiver per frame | 12-150 B per 10 ms frame; ~1.2-15 KB/s per pair (bracket, real Opus sizes UNVERIFIED) | 100 frames/s per speaker; receivers within 36 m (70 m when the receiver is on the surface); radio/intercom to everyone |
| Plugin audio (`AudioTransmitter`) | one Opus encode per 10 ms frame | up to ~15 KB/s per listener | sent to ALL authenticated players unless `ValidPlayers` filters |
| Hand-built EntityState/Rpc to one player | one pack, one send | 2 + 4 + 4 + payload (EntityState) or 13 + payload (Rpc) | per call |
| Spawn to a single observer | `SendSpawnMessage` via reflection | ~133 B per toy | O(1) per player, you own cleanup |

## Baseline traffic every player already pays

- ~2.4 KB/s of time snapshots (computed).
- Positions: 2 B (standing) to 13 B (moving and rotating) per visible target per tick, 0 B for omitted targets. Vanilla model ~0.5-1.5 KB/s per player; spectators get all targets (~38 KB/s with 90 targets, estimate).
- Radio transmitters: one transmitter-position message per frame per transmitting radio, to everyone. SCP-173 observers tracker: an RPC to all every frame. Last human tracker: position to all.

## Voice bandwidth

- 100 frames/s per speaker (480 samples at 48 kHz), encoder target 120 kbps (~150 B per frame on average; VBR frames can exceed it, so ~15 KB/s per listener is a planning figure, not a cap).
- Proximity is sent to receivers inside the vanilla range flag; radio and intercom to everyone (the client decides whether to play). The prefab plays proximity to about 22 m while the server routes to 36 m: the 22-36 m band is wasted bandwidth only if the rolloff curve reaches zero (UNVERIFIED).
- Plugin audio: `ValidPlayers` with an O(1) predicate (it runs per hub per 10 ms frame), for example a `HashSet<Player>` refreshed at <= 10 Hz with hysteresis. "In range" means `SpeakerToy.MaxDistance` (default 15 m) only when `IsSpatial` is true; a non-spatial speaker is heard everywhere. See scpsl-voice-and-audio.

## Reliable-stream consequences (inference)

Positions, voice, hints and spawns share one ordered stream. A spawn burst of S bytes per player queues S bytes in front of the next position message. Size bursts, not just counts (toys-and-spawning.md).

## Re-verifying on a new build

- Recompute the per-message sizes from the generated serializers after each game update; the 134 B spawn figure and the 48-76 B move updates depend on the toy's SyncVar layout.
- Re-read `HintDisplay.Show`, `Broadcast`, `GameConsoleTransmission` for changes in packing or encryption.
- Re-measure CPU with SlKit `FrameProfiler` and Mirror's `fullUpdateDuration`; treat every estimate here as a hypothesis until measured.
