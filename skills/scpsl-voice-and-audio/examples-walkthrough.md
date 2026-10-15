# Voice and audio examples: key parts

Verified against LabAPI 1.1.7 / SCP:SL 14.2.7 (2026-10). Everything here is compiled in `examples/voice-audio-snippets/` (namespace `Examples.VoiceAudioSnippets`, builds with 0 errors; the linter reports 0 errors and 0 warnings, plus 3 SL003 info notes: the game ignores `IsAllowed` on these voice events when the channel is None). It proves the code compiles against the 1.1.7 API, not that it was run on a live server: runtime claims are marked UNVERIFIED in the sibling notes and stay so.

| File | Shows |
|---|---|
| `MuteLedger.cs` | Right mute pairs, only-undo-what-you-did, rejoin, "audibly talking" |
| `SpectatorFlagKeeper.cs` | Spectators skip the living at zero per-packet cost |
| `ReceivingGate.cs` | Per-listener routing as a last resort; delivery counter |
| `SendingBudget.cs` | A per-speaker policy that needs `SendingVoiceMessage` |
| `AudioClips.cs` | PCM contract: padding, headroom, tone, WAV decode |
| `AnnouncementSpeakers.cs` | Pooled `SpeakerToy` + `AudioTransmitter` with an O(1) audience |
| `VoiceAudioWiring.cs` | Lifecycle: idempotent subscribe, round reset, shutdown |

## Muting: pairs, ownership, rejoin

```csharp
public bool MuteTemporarily(Player player)
{
    if (player == null || player.IsHost || player.IsMuted)
        return false;                 // somebody else's mute: not ours to lift later
    player.Mute(true);                // flag only, gone at disconnect
    _temporary.Add(player.UserId);    // UserId, not PlayerId: a rejoin is a new hub
    return true;
}
// Release: _temporary -> Unmute(false); _persistent -> Unmute(true). A crossed pair does nothing.
private void OnJoined(PlayerJoinedEventArgs ev)   // Joined runs AFTER the auth-time flag recompute
{
    if (ev.Player != null && _temporary.Contains(ev.Player.UserId)) ev.Player.Mute(true);
}
```

Why: `Mute(true)` pairs with `Unmute(false)`, `Mute(false)` with `Unmute(true)`; both lifetimes share one flag bit, so unmuting an admin's mute lifts it for them too. The ledger refuses already-muted players and releases only ids it recorded. `Mute(false)` writes `mutes.txt` synchronously on the main thread: human-speed actions only.

"Is it audible" is `module.ServerIsSending && !player.IsMuted && module.CurrentChannel != None` (`MuteLedger.IsAudiblyTalking`). A muted talker keeps `ServerIsSending` alive, so the mute test is part of the answer.

## Spectators: flag, not hook

```csharp
if ((module.ReceiveFlags & GroupMuteFlags.Alive) == 0)
    module.ReceiveFlags |= GroupMuteFlags.Alive;   // SpectatorVoiceModule only
```

Applied in `ChangedRole` and by `SlClock.Every(2 s)`. The client overwrites the flags at spawn and whenever it re-sends its voice settings, and no hook runs after that, so "usually on" is the honest contract. Cost per packet: nothing, the pipeline tests the flag already.

## The two hooks, built to be cheap

```csharp
// ReceivingGate: subscribed only while a rule exists; two set lookups at most.
private void OnReceiving(PlayerReceivingVoiceMessageEventArgs ev)
{
    if (ev.Message.Channel == VoiceChatChannel.None) return;     // most calls end here
    Player listener = ev.Player;                                 // already resolved by the args ctor
    if (listener == null) return;
    if (_deaf.Contains(listener.PlayerId)) { ev.IsAllowed = false; return; }
    if (_blockedPairs.Count != 0 && ev.Message.Speaker != null
        && _blockedPairs.Contains(Pair(listener.PlayerId, ev.Message.Speaker.PlayerId)))
        ev.IsAllowed = false;
}
```

- Keys are ints and a packed `long`: no strings, no boxing, no closure per raise. `ev.Sender` is avoided (an extra `Player.Get`).
- The `+=` carries `// scpsl-lint: ignore SL001 -- <reason>`; the hook is removed when the last rule goes (`UnhookIfIdle`).
- Never edit `ev.Message` (shared buffer) and never deny a whole channel: radio talkers are heard by bystanders from the Radio-labelled packet.
- `DeliveryCounter` subscribes the plain `VoiceTransceiver.OnVoiceMessageReceiving` (`(VoiceMessage, ReferenceHub)`) and does `_deliveries++`; a 10 s `SlClock` job logs the delta. Use it to compare a measured rate with talkers x receivers, then unsubscribe.

`SendingBudget` is the legitimate Sending use: a per-speaker radio allowance. The handler reads `module.CurrentChannel` (the validated channel; `ev.Message.Channel` is only what the client asked for), does one `TryGetValue` and `++used.Packets`; a clock job zeroes the counters each window. Cancelling here silences the packet but not `ServerIsSending`. A plain "may not talk" rule belongs in `Mute(true)`.

## Audio: PCM, pool, audience

```csharp
// AudioClips: whole 480-sample frames or the transmitter may never finish the clip.
int padded = (samples.Length + FrameSize - 1) / FrameSize * FrameSize;
if (padded == samples.Length) return samples;            // no copy when already aligned
```

`FrameSize` and `SampleRate` come from `AudioTransmitter` constants, so the code follows the build. `Normalize` scales to a 0.7 peak (the transmitter adds sqrt(2) gain). `TryDecodeWav` accepts only 16-bit mono 48 kHz and returns a reason otherwise, because nothing downstream resamples or downmixes; it uses no game types, so it may run on the plugin's own `Background.ForOwner` worker with the result returned through `MainThread.Post`.

```csharp
// AnnouncementSpeakers.CreateSlot: configure first, spawn once, predicate built once per slot.
SpeakerToy toy = SpeakerToy.Create(Vector3.zero, networkSpawn: false);
toy.ControllerId = controllerId;  toy.IsSpatial = true;  toy.MaxDistance = 25f;
slot.Valid = p => p != null && slot.Audience.Contains(p.PlayerId);   // called every hub every 10 ms
toy.ValidPlayers = slot.Valid;
toy.Spawn();

// Play: audience set first, then play, then a 10 Hz refresh and a duration guard.
slot.Toy.Play(AudioClips.PadToFrames(clip), queue: false);
slot.Refresh = SlClock.Every(100 ms, () => OnRefresh(slot), "pa.audience", ClockScope.Round);
slot.Guard   = SlClock.After(duration + 0.25 s, () => Finish(slot), "pa.release");
```

- Four slots, each its own `ControllerId` (201-204, an example range; pick and document yours). A fifth request returns false: dropped, never a new speaker per sound.
- The predicate is O(1) and exception-free; the set is rebuilt at 10 Hz from `Player.List` using a caller-supplied `Include` rule (here "within R metres"). The host and dummies are never in the set.
- End of clip: the refresh job calls `Finish` when `IsPlaying` goes false; the guard calls it regardless. `Finish` does `Toy.Stop()`, clears the audience, frees the slot.
- `ResetRound` (called from `WaitingForPlayers`) stops the static per-id transmitters, which survive scene reloads, and empties the pool. `Rent` also drops slots whose toy `IsDestroyed`.
- The transmitter itself allocates a closure per frame when `ValidPlayers` is set: a library cost, not removable from plugin code (see `plugin-audio.md`).

## Re-verifying on a new build

- Rebuild the project against the new LabAPI and game assemblies: `VoiceTransceiver.OnVoiceMessageReceiving` signature, `VoiceModuleBase.ServerIsSending/CurrentChannel`, `SpectatorVoiceModule.ReceiveFlags` setter, `SpeakerToy` members (`ValidPlayers`, `IsDestroyed`, static `Stop`) and `AudioTransmitter.FrameSize/SampleRate`.
- Re-check the facts the comments rely on, in `muting-and-routing.md` and `plugin-audio.md`: `Joined` after the auth-time flag recompute, `CurrentChannel` assigned before the Sending event, flags overwritten on client re-send, the 480-multiple termination.
- Only if the owner wants live tests (ask first; see running-a-test-server, use a separate instance and never their existing ports), run the examples on a server with a client: count `AudioMessage`s and deliveries per second (`DeliveryCounter`) and listen to the chime from inside and outside the radius.
