# Plugin audio: SpeakerToy, AudioTransmitter, relays, AudioPlayerApi

Verified against LabAPI 1.1.7 / SCP:SL 14.2.7 (2026-10). Derived from code reading; in-game behaviour is marked UNVERIFIED where it is not confirmed.

Compiled pool, audience set, PCM helpers and WAV decoder: `examples-walkthrough.md`.

## How it works

- Audio does not travel inside the toy. A `SpeakerToy` is a networked toy with synced `ControllerId` (byte), `IsSpatial`, `Volume`, `MinDistance` (default 1), `MaxDistance` (default 15). Audio is a separate Mirror `AudioMessage` carrying the controller id plus an Opus frame. Every client plays it on speakers whose `ControllerId` matches.
- `LabApi.Features.Audio.AudioTransmitter` (one per controller id, created on first use and never disposed; max 256 ids):
  - MEC coroutine on the Update segment. It keeps a target time against `NetworkTime.time` (unscaled on the server) and, each game frame, loops "while behind" encoding one 480-sample frame per 10 ms. At 60 fps that is 1-2 frames per game frame, at 20 fps about 5, after a 1 s hitch about 100.
  - Per frame: copy 480 samples, multiply by sqrt(2) (compensates a client-side -3 dB loss), encode with the game's `OpusEncoder` (Audio application, 120 kbit/s), send if the encoded length exceeds 2 bytes. Digital silence encodes to 3 B, so silent frames are STILL sent.
  - Send: `ValidPlayers == null` goes to every authenticated hub; otherwise a predicate runs for every hub every 10 ms frame (including catch-up iterations and the host and dummies) and a closure is allocated per frame. Both pack ONCE and reuse the segment per receiver, unlike the voice path.
  - Channel 0, reliable ordered (script default; serialized value UNVERIFIED).
- `SpeakerToy.Play/Pause/Resume/Skip/Stop` (static by id and instance helpers) route to that transmitter. `new AudioTransmitter(id)` gives an own instance (e.g. per-player audio). `ValidPlayers` is documented as called hundreds of times per frame.

## PCM contract

Mono `float[]`, 48 000 Hz, range -1..1. No resampling or downmixing anywhere. Pre-convert at load, cache the array (192 KB per second of mono 48 kHz; a 3-minute track is about 35 MB). Decoding files may run off the main thread (pure managed work); hand the result back with `MainThread.Post` from SlKit or your own queue, not through `Timing.*` from the worker (MEC's instance getter touches Unity objects).

- Headroom: peaks about 0.7 or lower is a conservative default because of the sqrt(2) gain. Whether louder input actually clips is UNVERIFIED (the client loss may cancel it). Listen-test with a full-scale sine before normalising a library.
- Pad every clip to a multiple of 480 samples. A tail that is not a multiple switches the transmitter to an internal zero buffer that never finishes: coroutine stays alive, `IsPlaying` stays true, one encode of silence per 10 ms, and a silent stream may be broadcast forever if libopus returns more than 2 bytes for silence. Derived; in-game UNVERIFIED. For a clip padded to 480, `IsPlaying` going false is a valid end signal (the coroutine returns); it also goes false when an exception aborts the loop, which is not "finished". Still call `Stop()` after the clip duration (`SlClock.After`) as a guard, and do not use `CurrentSampleCount` for end-of-clip (it keeps the last clip's length).

```csharp
// Zero-pad to a whole number of 10 ms frames (480 samples) before Play.
static float[] PadTo480(float[] s)
{
    int n = (s.Length + 479) / 480 * 480;
    var o = new float[n];
    Array.Copy(s, o, s.Length);
    return o;
}
```

## Rules

1. One transmitter per distinct stream. A second `new AudioTransmitter(sameId)` is legal only with disjoint `ValidPlayers`, or two streams garble in one client buffer.
2. Unique `ControllerId` per logical channel across all plugins (default 0 is shared by everything left at default). `Play(..., queue: false)` interrupts; the default appends. `Looping` is overwritten on every `Play`.
3. Always set `ValidPlayers` for anything not meant for everybody. Back it with a set rebuilt at most 10 Hz; the predicate is O(1), exception-free, allocation-free. Test `IsHost`/`IsNpc` or accept that they pass the predicate.
4. Set `MaxDistance` deliberately. Clients cull by it even when `IsSpatial` is false, so a "global" speaker with the default 15 is inaudible to far players.
5. Create with `networkSpawn: false`, configure, then `Spawn()` (one spawn message instead of spawn plus a combined state update).
6. Budget speakers. Each costs a ~94 KB buffer, a native decoder, an AudioClip and a per-frame update on the server and on every client; destroyed ones leave ~94 KB garbage. Pool, never one per sound effect. Every same-id speaker that is not culled and within its max distance of a listener decodes each message separately: client cost scales with same-id speakers in hearing range, not total.
7. Avoid stalls above ~0.5 s while audio plays: the client buffer holds 24 000 samples (0.5 s) and is cleared, not trimmed, on overflow, so audio is skipped. At merely low TPS (20-30) the buffer cannot overflow; roughness there is jitter sensitivity because the transmitter never runs ahead of real time (audibility UNVERIFIED).
8. Guard `Skip(n)`: clamp to `QueuedClipsCount + 1`, else it throws. After any exception inside the transmit loop the coroutine ends with the queue intact and old clips replay later: keep `ValidPlayers` exception-free and `Stop()` before replaying.
9. Main thread only for `Play`, `Spawn`, transmitters and anything touching Mirror.

## Alternatives

| Need | Option | Cost / caveat |
|---|---|---|
| Sound or music to a group | `SpeakerToy` + transmitter + `ValidPlayers` | 1 encode (~30 us per frame, measured below) + 1 pack + R sends per 10 ms |
| Announce a player's voice on a loudspeaker (PA) | In `SendingVoiceMessage`, build an `AudioMessage` from the raw Opus bytes and send to a precomputed audience | 0 encode, 0 decode, 1 pack + R sends. Code-compatible (both paths decode 48 kHz mono) but UNVERIFIED with a live client; one source per controller id at a time (stateful decoder; switching speakers may click). `AudioMessage` serialization copies synchronously, so copy only if you defer. |
| Several simultaneous effects | Mix once into one `float[]` per stream, keep few streams | 50 concurrent streams would be ~150 ms/s of encode alone on a fast desktop CPU |
| Community AudioPlayerApi (`AudioPlayer`, `AudioClipStorage`) | Only after inspecting the deployed binary | See below |

## AudioPlayerApi (a community dependency; findings are for the one build inspected)

- `AudioPlayer` is a MonoBehaviour with its own encoder and a controller id allocated from 0..254; `Update` catches up at 100 sends/s (idle early-out when no clips). Per send it copies the clip list, allocates a 480-float array per clip, mixes with clipping, encodes, then sends via a condition predicate, or to everyone, or to its owners with one pack each.
- `AudioClipStorage.LoadClip` accepts OGG only and keeps the whole clip as floats. Sample rate and channel count are stored but never used: a non-48 kHz or stereo clip plays at the wrong speed or garbled. Convert first.
- `AudioPlayer.Create` ended with an UNCONDITIONAL call adding an internet radio stream to every new player. The stream playback starts ffmpeg: if no `ffmpeg` folder is next to the server it DOWNLOADS an unpinned archive from GitHub (no checksum) and extracts it, then spawns the process. UNVERIFIED whether that line is upstream or a local modification, and UNVERIFIED what runs in production. Before deploying anything that calls it: look for an `ffmpeg` folder, grep the log for the downloader line, hash the deployed DLL against the one inspected, and either control outbound access and the folder or switch to LabAPI `SpeakerToy`.
- Allocating a player per effect exhausts ids (it logs "No available controller IDs" instead of throwing). Reuse players.

## Host and dummies

`ReferenceHub.AllHubs` includes the host and dummies. Dummy connections drop sends; the host's local connection delivers to the host's own client handler, which decodes audio if a speaker is within range of the server camera. The dedicated server mutes its listener, so that decode is wasted (estimate 2-6 ms/s per stream, UNVERIFIED). One more reason to use `ValidPlayers`.

## Measured Opus cost (one desktop CPU)

About 30 us per 10 ms frame on a Ryzen 9 9950X3D via native libopus (28-31 us speech-like and noise, ~10 us digital silence), i.e. about 3 ms/s per active stream. A production CPU 2-3x slower puts it at 6-9 ms/s per stream (estimate). Managed marshalling overhead inside Mono is UNVERIFIED. Below ~200 receivers the encode is the larger CPU term; above that the per-receiver sends dominate, and bandwidth is always dominated by the fan-out. Measure on your own server with a Stopwatch or sl-probe.

## Re-verifying on a new build

- Decompile or reflect `LabApi.Features.Audio.AudioTransmitter`: still sqrt(2) gain, `length > 2` filter, `ValidPlayers` handling, catch-up loop, 480-multiple termination. Re-run the non-multiple tail test in game (count `AudioMessage`s after a 1000-sample clip).
- `SpeakerToy` defaults (1/15), `PlaybackBuffer` capacity and overflow behaviour.
- Check which transport channel table the shipped prefab uses for channel 0.
- Hash any deployed AudioPlayerApi and re-inspect `Create`, the stream playback and the ffmpeg downloader.
- Measure Opus encode on the production CPU.
