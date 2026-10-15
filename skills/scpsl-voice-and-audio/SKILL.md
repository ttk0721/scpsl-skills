---
name: scpsl-voice-and-audio
description: "Use when an SCP:SL LabAPI plugin mutes players, routes or filters voice (proximity, radio, intercom, spectators), subscribes to SendingVoiceMessage/ReceivingVoiceMessage, plays sounds or music through SpeakerToy/AudioTransmitter/AudioPlayer, or when voice bandwidth, TPS drops with many talkers, spectator uplink, audio that never ends, garbled or skipping audio appear."
---

# SCP:SL voice and plugin audio

Verified against LabAPI 1.1.7 / SCP:SL 14.2.7 (2026-10).

## Overview

The server never decodes voice: it validates a client-encoded Opus packet, loops over every hub and sends it to each accepting receiver. Cost is talkers x 100 packets/s x hubs. Prefer flags the pipeline already checks over per-packet hooks. A round restart rebuilds every hub: `Mute(true)` and `ReceiveFlags` are lost with them.

## When to Use

- Mute/unmute, spectator or team voice rules, intercom/radio limits, "who talks".
- Voice-event subscribers; `Player.IsSpeaking`/`VoiceChannel` reads.
- Playing a clip, or relaying player voice to a speaker.
- NOT for hiding players (scpsl-networking-for-plugins), generic event cost (labapi-events), threading (scpsl-main-thread-and-timing).
- Small addition (one `Mute` call, one clip): use the facts below, skip the process. Live tests only if the owner wants them (running-a-test-server: ask, never touch their ports).

## Quick Reference

| Goal | Use | Per-packet cost |
|---|---|---|
| Silence a player | `Mute(true)` (until disconnect) / `Mute(false)` (mutes.txt) | 0 |
| Block intercom | `IntercomMute()`; cancelling `UsingIntercom` lags, admin overrides bypass | 0 |
| Spectators skip the living | `ReceiveFlags` Alive on spectator module, re-applied | 0 |
| Per-speaker policy | `SendingVoiceMessage` | 100/s per speaker |
| Per-pair routing | `ReceivingVoiceMessage`, last resort | per receiver per packet |
| Is someone talking | `ServerIsSending`, `IsMuted` | 0 |
| Play a clip | `SpeakerToy` + `AudioTransmitter` + `ValidPlayers` | encode + R sends per 10 ms |

Detail: voice-pipeline.md, muting-and-routing.md, plugin-audio.md, bandwidth-and-cost.md.

## Compiled examples

`examples/voice-audio-snippets/`; key parts in `examples-walkthrough.md`.

- Mute: `MuteLedger.cs`.
- Routing: `SpectatorFlagKeeper.cs` (free), `ReceivingGate.cs`, `SendingBudget.cs` (gated hooks).
- Audio: `AudioClips.cs`, `AnnouncementSpeakers.cs`.

## Rules

1. **100 packets/s per speaker.** Both events are HOT; Receiving fires per receiver even for channel None, args built with zero subscribers (SL001).
2. A Receiving handler returns on `Channel == None`, does one set lookup, never allocates or throws.
3. `Message.Channel` edits in Sending are ignored. Never mutate Data/Speaker in Receiving; the buffer is shared.
4. The client re-validates every delivery: proximity cannot be widened by rewriting channels.
5. Radio has no server range check. Denying it to non-radio receivers silences radio talkers for nearby bystanders.
6. Spectators receive all proximity/radio/intercom map-wide: the biggest bandwidth multiplier.
7. `ReceiveFlags` is a client preference, overwritten at spawn and whenever the client re-sends voice settings. Set after `ChangedRole`; re-apply on a timer.
8. Audio PCM: mono 48 kHz float, **length a multiple of 480**, peaks <= ~0.7 (conservative). Set `ValidPlayers` (default: every hub) and `MaxDistance` (default 15, even when non-spatial). Pool speakers.

```csharp
var toy = SpeakerToy.Create(pos, networkSpawn: false);
toy.ControllerId = 201;                          // unique per stream across plugins
toy.IsSpatial = true; toy.MaxDistance = 25f;
toy.ValidPlayers = p => _audience.Contains(p);   // O(1); set rebuilt <= 10 Hz
toy.Spawn();
toy.Play(PadTo480(samples), queue: false);       // PadTo480: plugin-audio.md
```

## Common Mistakes

| Wrong | Right |
|---|---|
| Voice event rate "about 50/s", cost "cheap". | 100/s per speaker; each raise builds args (plus a `Delegate[]` when subscribed). Sending: ~1000/s at 10 speakers; Receiving: times hubs. Sending only for per-speaker policy. |
| `Player.IsSpeaking`/`VoiceChannel` on the server. | Use `ServerIsSending` (0.1 s hold) + `CurrentChannel` + `IsMuted`; a muted talker keeps both alive. |
| Muting to stop SCP-939 hearing. | 939 reads only `ServerIsSending`, which muting never clears. Gate your consumers on `IsMuted`. |
| `Mute(false)` after `Unmute(false)` on a file-muted player; `Unmute(true)` on a `Mute(true)`. | Both no-ops. Pair `Mute(true)`/`Unmute(false)`, `Mute(false)`/`Unmute(true)`. No Muting event is raised. |
| Spectator flags set once. | Re-apply on role change and a timer. |
| Arbitrary clip length. | Pad to 480 or it may never end (unverified in game); `Stop()` after the duration. |
| `AudioPlayer.Create` (community AudioPlayerApi). | The inspected build adds a radio stream and may download ffmpeg. Verify the binary or use `SpeakerToy`. |
| One speaker per effect. | Each costs a buffer, decoder and per-frame update on every client. Pool. |

## Red Flags

- Receiving handler touching `ev.Sender`, LINQ or logging (`ev.Player` is free: the args constructor resolved it).
- Audio without `ValidPlayers`, or allocating predicates.
- Bandwidth claims without talkers x receivers x 159 B.
