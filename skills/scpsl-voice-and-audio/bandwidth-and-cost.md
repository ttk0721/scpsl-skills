# Voice and audio: bandwidth and CPU arithmetic

Verified against LabAPI 1.1.7 / SCP:SL 14.2.7 (2026-10). All figures are payload arithmetic (no UDP/IP/LiteNetLib headers, no packet captures) unless stated. Encoder measurements are from one desktop CPU.

## Definitions

S = simultaneous talkers, H = hubs (host and dummies included, ~101 at 100 players), R = receivers that accept the packet. p = 100 packets/s per talker. Voice work is constant per second and independent of TPS; per tick it grows as TPS drops (at 20 TPS each talker delivers ~5 packets per frame).

Per talker-receiver pair: 100 x ~159 B x 8 = ~127 kbit/s (voice); ~158 B for plugin audio.

## Bandwidth table

| Scenario | Sends/s | Mbit/s |
|---|---|---|
| 1 speaker heard by 10 humans in range | 1 000 | 1.3 |
| 1 speaker heard by everyone (radio, intercom, round summary) | 9 900 | 12.6 |
| 10 speakers, everyone | 99 000 | 126 |
| 15 proximity speakers x 40 spectators | 60 000 | 76 |
| 10 speakers, each 10 humans + 40 spectators | 50 000 | 64 |
| 1 global plugin stream, 100 receivers | 10 000 | 12.6 |
| 6 simultaneous global plugin streams | 60 000 | 76 |

Rule of thumb: 10 speakers on pure proximity with 10 listeners each is about 13 Mbit/s. A figure of 76 Mbit/s for 10 speakers corresponds to 60 000 sends/s (spectator case or ~6 fully heard speakers); 126 Mbit/s is exact for 10 speakers each heard by 99 receivers. Spectator count, radio use and round summary decide the real number. Measure deliveries per second with `OnVoiceMessageReceiving`.

Client cost: every received voice packet is Opus-decoded BEFORE its channel is inspected. A spectator decodes every routed speaker (15 speakers is 1 500 decodes/s) and SCPs decode human radio they never play.

## Main-thread work (estimates from desktop-Mono microbenchmarks, not in-game)

| Item | Cost |
|---|---|
| Validation + event args, S x 100 x H iterations/s | 10 talkers: ~101 000 iterations/s, ~6.5 MB/s args garbage with zero subscribers |
| Subscribed Receiving handler | +~4 MB/s `Delegate[]` garbage and ~0.5 us per call (~50 ms/s at S=10) |
| Per-receiver `Send<VoiceMessage>` (full pack + batcher copy) | ~0.25-0.45 us each; 99 000 sends/s is ~25-45 ms/s (2.5-4.5% of a second of main thread) |
| Plugin audio send (pack once, per-receiver batcher add) | ~0.1-0.2 us per receiver; 100 receivers ~1.5 ms/s per stream |
| `ValidPlayers` predicate | H calls + H `Player.Get` per 10 ms frame (100 x H lookups/s per stream) and one closure per frame |
| Opus encode (plugin audio) | ~30 us per 10 ms frame measured, ~3 ms/s per stream; 2-3x that on slower hardware (estimate) |
| Radio position broadcast | per frame per radio talker; about 0.35-0.65% CPU at 60 TPS (estimate) |

Allocations per voice packet: ~H x 64 B args (always) + ~64 B for Sending; + H x (32 + 8k) B when subscribed (k = subscribers).

## Cutting the multiplier

1. Spectators first: Alive flag if they need not hear the living (re-applied; see muting-and-routing.md).
2. Per-pair filtering only when the uplink, not the main thread, is the bottleneck.
3. Plugin streams: `ValidPlayers` limited to who can hear, few streams, mix once.
4. Do not widen 36 m; the audible limit is about 22 m (UNVERIFIED curve), so the band between is already waste.
5. Expect round-summary spikes; keep hooks trivial then.

## Estimating your own server

`bandwidth_bps = S x 100 x R x 159 x 8`. Count S from `SendingVoiceMessage` per speaker (exact; `ServerIsSending` over-counts because of the 0.1 s hold and muted talkers) and R from `OnVoiceMessageReceiving` deliveries divided by packets. Per-event and per-send costs above are benchmarks: confirm with SlKit `FrameProfiler` or sl-probe before optimising (measuring-scpsl-performance, scpsl-engine-cost-model).

## Re-verifying on a new build

- Constants: 48 kHz, 480-sample frame, 120 kbit/s encoder, batch/message framing sizes; recompute 159 B.
- Sample 10 s of real-microphone speech to replace the synthetic-speech 151 B figure (real speech size is UNVERIFIED, expected near 150 B).
- Capture on the wire for LiteNetLib overhead.
- Re-measure Opus encode per frame on the production CPU.
- Re-measure per-send and per-event main-thread costs inside a live server loop.
