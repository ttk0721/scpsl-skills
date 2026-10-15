# Muting, flags, events and cheap routing

Verified against LabAPI 1.1.7 / SCP:SL 14.2.7 (2026-10). Cost figures are desktop-Mono microbenchmarks or estimates, never measured in a live server loop.

Compiled versions of the patterns below (mute ledger, spectator flags, gated hooks, delivery counter): `examples-walkthrough.md`.

## Choosing a mechanism (cheapest first)

| Goal | Mechanism | Per-packet cost | Notes |
|---|---|---|---|
| Silence a speaker for everybody | `Player.Mute(true)` (flag) or `Mute(false)` (file) | 0 | Checked before the Sending event. Wrappers raise NO Muting/Muted event (only the Remote Admin commands do). `Mute(false)` writes `mutes.txt` synchronously on the main thread. |
| Block only the intercom | `Player.IntercomMute()` or cancel `PlayerEvents.UsingIntercom` | 0 / per frame | Cancel is not instant for a running session and is bypassed by admin overrides; `IntercomMute` is the robust form. |
| Spectators should not hear the living | `ReceiveFlags \|= GroupMuteFlags.Alive` on the spectator's voice module | 0 (check exists) | All-or-nothing: removes Proximity, Radio, Intercom, 1576. Keeps RoundSummary. Client-overwritten (below). |
| Per-speaker policy (talk-time limit, allow-list, "who is talking") | `SendingVoiceMessage` | 1 per accepted packet, 100/s per speaker | Cancelling = nobody hears; `CurrentChannel`/`ServerIsSending` already updated. Channel edits ignored. |
| Per-pair routing (mute A for B, team chat) | `ReceivingVoiceMessage` (`IsAllowed=false` or Channel None) | 1 per receiver per packet | Most expensive hook. Last resort. |
| Count actual deliveries | `VoiceTransceiver.OnVoiceMessageReceiving` | 1 delegate call per delivery | Plain C# event. Integer increment only; unsubscribe on disable. |
| Know if someone talks now | `VoiceModule.ServerIsSending` (+ `CurrentChannel`), `Player.IsUsingRadio` | 0 | Combine with `IsMuted`. Not `IsSpeaking`/`VoiceChannel`. |
| Force a far listener to hear proximity | nothing works | | Client revalidates. Only Radio (non-spectator speakers, but plays only through a usable radio in range, or faintly via the speaker's proximity source) and, by code, Intercom/RoundSummary (accepted by human and SCP receivers; live client UNVERIFIED) reach a far human. Prefer the PA relay in plugin-audio.md. |
| Change who counts as proximity | Harmony on a module's `ValidateReceive` | per receiver per packet | Last resort; breaks client revalidation for any channel it widens. Never patch `ValidateVisibility`. |

Game API with no LabAPI wrapper: `Intercom.TrySetOverride(hub, bool)`, `Intercom.State`, `VoiceModuleBase.ReceiveFlags`, `OverwatchVoiceModule.DisabledChannels`/`UseSpatialAudio`, `VoiceChatMutes.GetFlags/SetFlags(hub, VcMuteFlags)` for the global flags (wrappers set Local* only).

## Event cost model (S talkers, H hubs ~ 101 at 100 players)

- `ReceivingVoiceMessage` raises S x 100 x H times per second. 10 talkers: ~101 000/s. The game allocates the args object (~64 B, with a `Player.Get` lookup) for every hub even with zero subscribers: ~6.5 MB/s of garbage at 10 talkers.
- A subscriber adds one `Delegate[]` per raise from `GetInvocationList()` (~4 MB/s more at S=10) plus the call. Desktop microbenchmark: ~0.5 us per handler call, so about 50 ms/s of main thread at S=10. Estimate, not an in-game measurement.
- Early return on `Channel == None` keeps your own handler near 20-50 ns (estimate).
- A throwing handler is caught and logged with a stack trace per call: up to ~100 000 log entries/s.
- `SendingVoiceMessage` raises S x 100 per second: one args allocation per raise, plus `Delegate[]` when subscribed. At S=10 that is ~1000/s, orders of magnitude below Receiving, but it is still catalogued HOT. Subscribe for per-speaker logic only, and write a cached timestamp, nothing else. If you only need "has anyone talked in the last second", polling `ServerIsSending` at 1 Hz misses short utterances and is cheaper; choose by whether misses matter.
- EXILED, when loaded, adds its own per-receiver args plus two `Player.Get` unconditionally.
- Distance-filtering spectators in a Receiving handler costs ~0.5 us on every receiver of every packet to save ~0.25-0.45 us per skipped send (estimates). It trades CPU for uplink; use only when the uplink is the bottleneck.

## ReceiveFlags and DisabledChannels are preferences, not policy

`GroupMuteFlags`: Spectators=1, Alive=2, Lobby=4, Summary=8. They arrive from the client, are stored per connection, are re-applied to the module on every role spawn, and are ALSO overwritten mid-life each time the client re-sends them (on connect and on any change of one of its four voice settings). The flags-received event fires BEFORE the overwrite, so no hook runs after it. Therefore:

- Set after the role spawned (`ChangedRole` fires after the new role is initialised).
- Re-apply on role change and on a short timer.
- Overwatch `DisabledChannels`/`UseSpatialAudio` follow the same rule and apply only to the Overwatch role.
- That `ChangedRole` follows the spawn setup that re-reads the stored flags is inferred (UNVERIFIED link).

```csharp
// Job body, registered once with SlClock.Every(TimeSpan.FromSeconds(2), ReapplyFlags, "spectator-flags").
// Cheap: one type test and one OR per spectator. Verify member names and namespaces with a compile.
static void ReapplyFlags()
{
    foreach (Player p in Player.List)
        if (p.IsReady && p.Role == RoleTypeId.Spectator && p.VoiceModule is SpectatorVoiceModule sv)
            sv.ReceiveFlags |= GroupMuteFlags.Alive;
}
```

## Mute lifetimes

- `VcMuteFlags`: LocalRegular=1, LocalIntercom=2, GlobalRegular=4, GlobalIntercom=8. Packets are dropped only for LocalRegular|GlobalRegular.
- State: an in-memory per-hub flag dictionary (dropped when the hub is removed) plus a set loaded from `mutes.txt` at start and on config reload (loading only adds, never removes; hand-edited removals need a restart or `Unmute(true)`).
- At authentication the flags are recomputed from scratch: file mute becomes LocalRegular, Northwood token mute flags become Global ones (gated by server config or verification), and tokens with ban bypass get None.
- `Joined` is raised after that recompute, so a temporary mute applied in `Joined` survives. A mute applied before authentication completes would be overwritten. There is no second authentication in one connection; reconnecting makes a new hub and loses `Mute(true)`. A round restart reloads the scene, destroys every hub and fires `Left` then `Joined` again, so `Mute(true)` is lost at each restart too; re-apply it in `Joined`.
- `Mute(true)`: flag only, until disconnect. `Mute(false)`: appends to `mutes.txt` (shared with Remote Admin) and sets the flag, but does nothing when the id is already in the in-memory set, including the flag. So `Mute(false)` after `Unmute(false)` on a file-muted player does not re-mute; use `Mute(true)`.
- `Unmute(true)` does nothing for a temporary mute (revoke only acts on ids in the in-memory set). Pair `Mute(true)` with `Unmute(false)`, `Mute(false)` with `Unmute(true)`. `Unmute(false)` on a file mute leaves the file entry, so the player is muted again after rejoin.
- GlobalRegular mutes come from the auth token and cannot be cleared with the wrappers.

## What a muted talker still does (UNVERIFIED at runtime, derived from code)

The rate counter is incremented before the mute test, and `CurrentChannel` keeps the last ACCEPTED channel until a role change. A muted player holding PTT therefore keeps `ServerIsSending` true:

- SCP-939 voice detection reads only `ServerIsSending`: muting never prevents it, whatever the timing.
- Radio position broadcast and role un-spoofing read `ServerIsSending && CurrentChannel == Radio`: they fire for a muted player whose stale channel is Radio. Muting before their first radio use this life avoids this part only.
- Intercom is NOT affected: muted hubs are rejected there.
- Gate your own consumers on `Player.IsMuted`, including `IsUsingRadio`.

## Unreliable status properties

`Player.IsSpeaking` reads a client-side playback buffer that the server fills only through its own host hub's client path: unreliable, "always false" is not established. `Player.VoiceChannel` reads the last accepted channel and is never reset.

## Re-verifying on a new build

- Order inside `ServerReceiveMessage` (rate counter before mute test; `CurrentChannel` assignment before the Sending event).
- `VoiceChatReceivePrefs` handler: still overwrites the current role's flags on every client message.
- `VoiceChatMutes` flag recompute at authentication and the position of the `Joined` raise relative to it.
- LabAPI `Player.Mute/Unmute/IntercomMute` bodies and whether wrappers now raise Muting events.
- Intercom `CheckPlayer`/`CheckPerms` and where `UsingIntercom` is raised.
- sl-probe or a counter on `OnVoiceMessageReceiving` to confirm event rates on the live build.
