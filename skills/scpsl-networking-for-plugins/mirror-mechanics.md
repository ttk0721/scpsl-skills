# Mirror mechanics that decide plugin cost

Verified against LabAPI 1.1.7 / SCP:SL 14.2.7 (2026-10). Code reading plus arithmetic on serializers. Byte counts are Mirror payload plus the stated framing, not captured datagrams; add ~28-48 B UDP/IP/LiteNetLib header per datagram.

## Server loop

- The dedicated server runs in host mode, so a loopback connection (id 0) observes everything and copies every message into a local queue. It is one extra "player" in every broadcast.
- `Broadcast()` runs once per send interval (the Mirror tick rate is a prefab value assumed 60: UNVERIFIED). Per ready connection it sends an empty unreliable `TimeSnapshotMessage` on channel 1 (~2.4 KB/s per client, computed), serializes dirty identities, then flushes the batcher.
- It walks every identity each connection observes, every tick, dirty or not. That is the O(connections x identities) term: vanilla estimate 1.4-3.5 ms/frame at ~800 identities and ~100 players; 10-90 ms with 25k map objects (estimates, not measured). `IsStatic` removes only the per-toy `LateUpdate`, not this walk.
- Serialization happens once per identity per frame and is shared by all non-owner observers. `syncMode = Owner` removes a component from the observers' writer; there is no per-observer variant.

## Dirty tracking

- A SyncVar setter marks dirty only when the value changes under `EqualityComparer<T>.Default`. For `Vector3` that is exact component equality (no epsilon). `Quaternion` was not re-checked: UNVERIFIED, assume analogous.
- A component is dirty only if bits are set **and** `syncInterval` has elapsed. While throttled, the bits are not cleared, so several writes become one message with the last value.
- `SyncList` ops are never coalesced: every recorded operation is sent.
- Fixed overhead per dirty component: 1 B size/safety byte, an 8 B sync-object bitmask even with no SyncObjects, and 8 B dirty-bits per class level.

## Messages and framing

| Message | Size |
|---|---|
| Framing | 2 B id (hash of the type's full name) + batch length VarUInt 1-2 B per message; 8 B timestamp per batch |
| `EntityStateMessage` | 2 + 4 netId + 4 length + payload |
| `RpcMessage` | 2 + 4 + 1 component + 2 hash + 4 length + payload (13 B header) |
| `SpawnMessage` | ~133 B for a primitive toy |
| `ObjectDestroyMessage` | 2 + 4 |
| Vector3 / Quaternion / ulong | 12 / 16 / 8 B, raw |

- `Send<T>` packs immediately in the caller's frame and appends to a per-channel batcher; the batcher is flushed at the next `Broadcast`. Anything queued reaches the wire at the next tick boundary (then LiteNetLib's ~15 ms logic tick).
- `SendToAll`/`SendToReady` pack once. **`ClientRpc` does not**: it repacks per observer, so a broadcast RPC is N serializations, the same order as N targeted sends. `TargetRpc` is one pack.
- A custom `NetworkMessage` id has no client handler. The vanilla client logs "failed to unpack and invoke message" and disconnects. The server likewise logs a warning and disconnects a sender that uses an unknown id.
- A transport send failure is swallowed silently.

## Channels and size limits

- Channel 0 = ReliableOrdered (from a scene-data scan, not re-read: UNVERIFIED) carries positions, voice, hints, RPCs, SyncVar deltas and spawns. One burst delays everything behind it (head-of-line blocking, inferred). Channel 1 (unreliable) is used for time snapshots and pings only.
- On channel 0 a connection's whole tick is one batch that LiteNetLib fragments; there is no practical Mirror size cap.
- Unreliable messages above one datagram are dropped with a LogError. MTU discovery is never enabled in the game's code, so the peer MTU is most likely 1024 and the channel-1 cap ~1013 B (channel 2 ~1010 B). UNVERIFIED at runtime: log `LiteNetLib4MirrorCore.Host.FirstPeer.Mtu`. A figure of 1432 and ~1421 B (assuming MTU discovery) is circulating; treat it as wrong until the log says otherwise.
- LiteNetLib statistics are off by default (`NetManager.EnableStatistics`); set it on `Host` or counters stay zero.

## Observers, AOI and who gets what

- `NetworkServer.aoi` is almost certainly null: the game's only interest-management class admits everyone, and nothing assigns it (a scene scan found no reference; UNVERIFIED at runtime, log it). Per-observer filtering exists only for player positions/roles, voice and gunshot audio.
- `NetworkIdentity.visible` is binary and global. `ShowForConnection`/`HideForConnection`/`SendSpawnMessage` are internal.
- Dummies are not in `NetworkServer.connections` (so not in the broadcast walk) but are receivers and targets in the position loop (CPU only). The host loopback is the reverse.

## Threading

Transport events are queued by a poll thread and drained on the main thread in `LateUpdate`. Writers, batchers and the spawned-object dictionary are plain collections. Marshal back before any `Connection.Send`, `Spawn` or `Destroy` (scpsl-main-thread-and-timing, SlKit `MainThread.Post`).

## Open questions (UNVERIFIED)

1. `NetworkServer.aoi` null at runtime. 2. Mirror tick rate prefab value. 3. Serialized channel list on the transport prefab. 4. Prefab `syncInterval` values. 5. Real Opus frame sizes. 6. Head-of-line numbers (derived from window size, not measured). 7. All CPU microsecond figures. 8. Hint client-queue semantics and the exact hint byte layout.

## Re-verifying on a new build

- Log at runtime: `NetworkServer.aoi`, `NetworkServer.tickRate`, `Host.FirstPeer.Mtu`, `Host.MtuDiscovery`, per-toy `SyncInterval`; enable LiteNetLib statistics.
- Grep game code for assignments to `MtuDiscovery`/`MtuOverride` and for any new `InterestManagement` subclass or `aoi` assignment.
- Compare `SendRPCInternal` (still repacks per observer?) and `BroadcastToConnection` (still walks `observing`?).
- Spawn one toy and count `SpawnMessage` bytes; re-derive the 134 B figure.
