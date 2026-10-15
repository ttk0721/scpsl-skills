# Voice pipeline: receive, validate, per-receiver send

Verified against LabAPI 1.1.7 / SCP:SL 14.2.7 (2026-10). Derived from reading the game assembly; items marked UNVERIFIED were not confirmed at runtime. Mechanics are described in words; type names are the public ones.

## Wire and cadence

| Fact | Value |
|---|---|
| Client capture | PTT state, 480-sample frames at 48 kHz mono = 10 ms, Opus-encoded by the client |
| Rate | 100 packets/s per talking client (rate limiter ceiling 256/s) |
| Encoder bitrate | fixed at 120 kbit/s; real frames ~151 B (measured with the game's native libopus on synthetic input: 149-153 B for non-silence, 3 B for digital silence; VBR, speech-like frames ranged about 106-188 B; real microphone speech UNVERIFIED) |
| Voice packet on wire | ~157-159 B in a batch (ids, channel, length, payload, batch prefix; UDP/IP/LiteNetLib headers not counted) |
| Delivery channel | Mirror channel 0, reliable ordered, the same stream as game state (head-of-line blocking effect on voice latency not measured) |
| Server latency added | up to one send interval (1/tickrate): the batcher flushes once per interval |

Handlers run on the main thread: the transport receives on a poll thread, queues, and the Mirror handler drains it in the Unity loop.

## Server receive path, in order

1. Drop if the speaker is null, the speaker's net id does not match the connection, the role is not a voice role, the rate limit fails (128 packets per 0.5 s window, window reset in Update), or the speaker is muted. The rate counter is incremented BEFORE the mute test, so a muted talker who holds PTT still keeps `ServerIsSending` alive.
2. The speaker's module `ValidateSend(channel)` maps the requested channel; None drops.
3. `CurrentChannel` is set to the validated channel. This happens before the Sending event, so a packet cancelled by a plugin still updates `CurrentChannel` and `ServerIsSending`.
4. `SendingVoiceMessage` (cancellable). Changes to `Message.Data`, `DataLength`, `Speaker` propagate; changes to `Message.Channel` are ignored (the receive loop uses the already validated channel).
5. Loop over EVERY hub (host and dummies included; hubs in the None role count as voice roles). Per hub: `ValidateReceive(speaker, channel)`, then `ReceivingVoiceMessage` args are allocated (always, even with no subscriber and even when the channel came out None). If not cancelled, the possibly modified message is written back into the loop variable shared with later receivers. If the channel is not None: the static `VoiceTransceiver.OnVoiceMessageReceiving` event fires, then one `Send` to that hub.
6. Each `Send<VoiceMessage>` packs a fresh writer per receiver. This is dearer than plugin audio, which packs once.

`Message.Data` is one static 512-byte array reused by the next deserialization. Never keep it past the handler.

LabAPI events are inline call sites in the game assembly, not Harmony hooks. A Harmony prefix that returns false on `VoiceTransceiver.ServerReceiveMessage` bypasses them. EXILED, if loaded, patches the same method with transpilers anchored on the LabAPI args constructors and the generic `Send` call: run last and leave those instructions in place. See scpsl-harmony-patching.

## Channels

`VoiceChatChannel`: None, Proximity, Radio, ScpChat, Spectator, RoundSummary, Intercom, Mimicry, Scp1576, PreGameLobby, Scp1507.

Who may SEND what (`ValidateSend`):

| Role module | Accepted |
|---|---|
| Human | Proximity; Radio only with a usable radio (radio item enabled, battery above 0); Scp1576 only if the sender is in that item's receiver set, otherwise downgraded to Proximity |
| Global modules (most SCPs, spectators, lobby) | only their primary channel: ScpChat, Scp1507, Spectator, PreGameLobby |
| SCP-079 | Proximity when its speaker ability can transmit, plus ScpChat |
| SCP-3114 | ScpChat; Proximity only while disguised |
| SCP-939 | ScpChat, Mimicry (mimicry is sent by 939's own client from recorded buffers) |

## Who hears whom (`ValidateReceive`, per receiver per packet)

Common prefix for non-global receivers: speaker equals owner gives None; Mimicry passes; during a round summary, everyone hears everyone (RoundSummary) unless the receiver set the Summary mute flag; if the speaker currently holds intercom rights (and the channel is not Scp1576) the packet is relabelled Intercom for every receiver; otherwise unchanged.

- **Human receiver:** Intercom and RoundSummary pass. Proximity passes only if the receiver's visibility controller accepts the speaker (a flag test, no raycast, no walls). Radio and Mimicry pass for any non-spectator speaker. Scp1576 becomes Proximity if the speaker passes the proximity check. Spectator channel is heard only by a Scp1576 receiver in range. ScpChat, Scp1507, PreGameLobby give None.
- **SCP receiver:** own primary channel passes (SCPs hear each other; the 1507 swarm hears only 1507). Radio and Intercom pass unless the speaker is a spectator. Proximity needs a distance check: SCP-079 uses distance from its camera (110 m), roles without a visibility controller use 110 m from the body, others use the visibility controller. So SCPs receive human radio on the wire; the client decodes it before looking at the channel and discards it: pure waste.
- **Spectator receiver:** Proximity, Radio, Intercom, RoundSummary, Mimicry all pass with NO distance check. The receiver's `ReceiveFlags` Alive removes Proximity/Radio/Intercom/Scp1576; Spectators removes the Spectator channel. Lobby spectators have PreGameLobby remapped to Spectator before the round starts.
- **Overwatch:** `UseSpatialAudio=false` rewrites Proximity/Mimicry/Radio into RoundSummary (global). `DisabledChannels` can mute channels. Both come from the client's own overwatch settings and are overwritten on each client re-send; they exist only on the Overwatch role.

## Proximity: 36 m routing, 22 m audible

- Routing is the visibility flag test: out of range beyond about 36 m (squared test 1300), or about 70 m (4900) when the RECEIVER is on the Surface. Invisible (SCP-268) hides the speaker. A receiver wearing SCP-1344 ignores range and 268, so it is routed ALL proximity voice map-wide.
- The client runs the same validation against client-known positions. A hidden player is parked at (0, 6000, 0) client-side, so their voice fails and is dropped.
- Audible rolloff of the prefab is about 22 m max distance (read from the prefab asset once and not re-checked, so treat as UNVERIFIED; whether the curve reaches 0 at 22 m is UNVERIFIED). The 22-36 m band therefore costs bandwidth for little or nothing, 2.7x area (10x on the Surface). Read `HumanVoiceModule.FirstProxPlayback.Source.maxDistance` at runtime instead of hardcoding 22.
- Never patch `ValidateVisibility` to change voice range: it has six consumers (voice, SCP voice, 049 Sense, 939 ripple, firearm audio, position distributor). The client runs the same method, so a server-only change breaks revalidation.

## Radio

- Server sender gate: usable radio only. Server receiver gate: none. Client receiver gate: a usable radio, and range from the radio prefab (values UNVERIFIED) checked against the speaker's last known waypoint location, with a vertical penetration limit.
- Last known location comes from a transmitter-position message the server sends to everyone EVERY FRAME per transmitting player while `ServerIsSending` and `CurrentChannel == Radio`.
- Radio audio is also fed into the speaker's proximity playbacks, so a bystander next to a radio talker hears them from the Radio-labelled packet (volume 1.0; the 0.35 attenuation applies only for listeners holding a usable radio). Denying Radio deliveries to receivers without a radio silences radio talkers for nearby bystanders.
- `UsingRadio`/`UsedRadio` fire every frame per enabled radio. Cancelling `UsingRadio` skips battery drain but the radio stays usable. Battery exhaustion has no discrete event.

## Intercom

- Server state machine: Ready, first valid hub becomes the speaker, Starting, InUse (default max 20 s), Cooldown (default 120 s; bypass-mode speakers get 0).
- The speaker check requires range of the desk, human, `ServerIsSending`, not muted, and `UsingIntercom` not cancelled. It does NOT look at the channel: anyone transmitting anything near the desk takes the intercom, and while they hold it EVERY packet (Proximity or Radio, not Scp1576) is relabelled Intercom for every receiver.
- `UsingIntercom` limits: fires only in Ready (per candidate) and InUse (current speaker), never in Starting; cancelling for the current speaker does not end a running session at once because InUse continues for a sustain time; a speaker with an admin override (`Intercom.TrySetOverride`) is relabelled without the check, so only `IntercomMute` stops that case.
- Any mute flag counts as an intercom mute.

## Other facts

- `ServerIsSending` is true while packets arrive and for 0.1 s after the last. Consumers: radio position broadcast, intercom, 079 speaker ability, 939 detection range, role un-spoofing for hidden players.
- `OnVoiceMessageReceiving` (plain C# event on `VoiceTransceiver`) fires per ACTUAL delivery, no EventArgs, no `GetInvocationList` allocation. It is still ~100 000 calls/s at 10 talkers x ~100 hubs, so keep the handler to an integer increment. Vanilla subscribes it for an achievement.
- SCP-1576 rebuilds its receiver set every Update (about 10.5 m around any active user).
- Round-end summary: everyone talking is routed to everyone. Expect a bandwidth spike; do not run per-packet hooks heavier than a set lookup then.
- Freeze over ~1.3 s: packets queued during a long frame run in one frame against a 128-per-0.5 s budget, so voice can drop (derived, UNVERIFIED).
- A newly revealed (un-hidden) player's first words can clip: voice is forwarded earlier in the frame than the position update, and the client snaps the player from 6000 m on its next Update (derived from code, not observed live).

## Re-verifying on a new build

- Rate and size constants: sample rate, 480-sample frame, 128 packets per 0.5 s, 36/70 m squared values, 110 m SCP range. Inspect the game types `VoiceChatSettings`, `VoiceModuleBase`, `FpcVisibilityController`.
- `VoiceTransceiver.ServerReceiveMessage`: order of mute test, rate limiter, `CurrentChannel` assignment, Sending event, receiver loop, write-back. Confirm the args are still built for every hub.
- Per-role `ValidateSend`/`ValidateReceive` tables (human, standard SCP, spectator, overwatch, 079/3114/939 modules).
- Run a live check: join with a client, count `OnVoiceMessageReceiving` per second against talkers x receivers.
- Re-read the events catalog (labapi-events) for any change in voice event frequency class.
