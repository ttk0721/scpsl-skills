# AFK and activity signals, roles, spectators

Verified against LabAPI 1.1.7 / SCP:SL 14.2.7 (2026-10). Part of adding-per-player-tracking.

"Is this player here" is a guess built from signals the server can see. Pick signals by cost, state the guess's weakness, and let the owner choose what counts.

## Signals, cheapest first

| Signal | Cost | Catches | Misses |
|---|---|---|---|
| `Player.Position` delta between samples | A cached vector read. Setters send messages, getters do not | Walking, sprinting, being carried | Standing still while fighting or reading |
| `Player.LookRotation` delta (pitch, yaw) | Two cached floats. Use this, not `Rotation`: the quaternion's pitch convention differs from the game's | Aiming, looking around | Mouse jiggle (counts as active) |
| `VoiceModule.ServerIsSending`, with `!IsMuted` | One property read in the sample you already run | Anyone talking at the sample instant | A short sentence between two samples |
| `PlayerEvents.SendingVoiceMessage` | HOT: 100 raises/s per speaker, args and `Delegate[]` allocated per raise even with no work | Every utterance | nothing, at the cost shown |
| Item, door, shot, damage events | Per action, usually cheap, each one a subscription | Players who stand and interact | Everything else |

Default: position, look and polled voice. Add an event only when a missed signal is a real defect for this feature, and say which cost you accepted.

### The gated voice alternative

Use it only when sub-sample utterances matter. Subscribe in `Enable` only if a config flag asks for it, unsubscribe in `Shutdown`, and keep the handler to one lookup and one write:

```csharp
private void OnVoice(PlayerSendingVoiceMessageEventArgs ev)   // 100/s per speaker
{
    if (_hubs.TryGetValue(ev.Player.UserId, out HubState h))
        h.LastVoiceTicks = StopwatchTime.Now;                  // no log, no allocation, no throw
}
// JudgeAfk then compares LastVoiceTicks with the previous sample time.
```

Catalog entry and the gate rule: labapi-events. Packet rates and the `ServerIsSending` hold (0.1 s after the last packet, alive for a muted talker who holds push-to-talk): scpsl-voice-and-audio.

## The sample in code

```csharp
_sampler = SlClock.EveryStaggered<Player>(period, _tracker.Refill, _tracker.Sample, "playtime.sample");
// ... in Sample, for an alive FPC role:
bool moved = h.HasBaseline                           // a new baseline is NOT movement
    && ((pos - h.Position).sqrMagnitude >= move * move || turn >= look);
if (!moved && cfg.VoiceCountsAsActivity)             // polled, no voice event
    moved = voice != null && voice.ServerIsSending && !p.IsMuted;
if (moved) account.LastMovedTicks = now;             // account: keyed by UserId, outlives rounds
bool afk = (now - account.LastMovedTicks) >= afkAfter;
```

Hub baselines die with the round. The idle clock does not: a restart or respawn never grants a fresh grace period (see "The idle clock survives rounds" below). The late-joiner and `ChangedRole` behaviour of `Sample`, `Joined` and `Left` is under "Late joiners and rejoin".

## Thresholds

- `AfkAfterSeconds` 120 is a starting value, a config key. A respawn does not reset the timer (next section). The first reading after a role change only sets a new position baseline; it is not movement.
- Movement and look thresholds are per sample (5 s), so they are small: 0.25 m and 2 degrees. A sample every second would need smaller ones.
- Time before the threshold is credited as active. The interval ending at the sample that crosses it, and every later one, goes to AFK. Write that rule into the owner's design; nobody guesses it.

## The idle clock survives rounds

Resetting the AFK state at `WaitingForPlayers` and at every role change gives an AFK player a fresh 2-minute grace period each round, credited as about 5 minutes of active time per 15-20 minute round.

| State | Lives | Reset when |
|---|---|---|
| Position, look, "has baseline", last sample time, last bucket | Per hub, in the round owner | Round restart (`WaitingForPlayers` replaces the owner), `Joined`, role change (baseline only). They point into the old scene |
| Idle clock (last time movement was seen), last-seen time | Per `UserId`, outside the round owner | Only after a long absence (`IdleCarryOverSeconds`, example default 300), or never seen before |
| Accumulated totals | Per `UserId`, journaled | Never |

Rules:

- A restart destroys every hub and wrapper, raises `Left` for each non-host hub and raises `Joined` again after re-authentication; statics survive. Per-hub state must go, `UserId`-keyed state must stay.
- Idle time keeps counting through a restart, the lobby (role `None`), spectating and a respawn. Do not "start fresh" in any of them.
- The first reading in a new world (after a scene reload, a spawn or a rejoin) has no previous position to compare with. It becomes the new baseline and is not movement, so it must not touch the idle clock. The second reading decides.
- Cost to honest players: someone idle for a while across a restart is classed AFK from their first reading after spawn until their first move, normally one or two periods. A farmer pays that every round. State the trade-off to the owner.
- Voice still counts as activity on any reading, baseline or not.
- The carry-over window is a design choice, not a game constant. How long a restart plus reconnect takes is unmeasured, so the example default is generous. Too short a window resets the clock every round and brings the bug back.
- Idle clocks live in memory only. They are not journaled, so a process restart starts everyone as present.

## Roles

| Role state | Handling | Why |
|---|---|---|
| Alive with a first-person movement module | Judge for AFK | Has position and look |
| Alive without one (SCP-079) | Active, never AFK | No first-person state; LabAPI returns the zero vector |
| Spectator | Own bucket | Waiting is neither playing nor AFK, and there is no first-person state to judge |
| Overwatch, Filmmaker | Skipped | Staff observing; count only if the owner says so |
| `None` (lobby) | Skipped | No role yet, nothing to judge |
| Host, dummies | Host is not in `ReadyList`; dummies are, but share `ID_Dummy` and fail the account-id filter | Not accounts |

Elevators, SCP-106 pocket teleports and noclip move a player the player did not move. They can mask AFK for one interval; do not special-case them unless the owner cares.

Roles table for exemptions in other mechanics: scpsl-roles-damage-effects.

## Late joiners and rejoin

For a late joiner the role events come before `Joined`, with no nickname. The tracker needs no role event to start: `Sample` adopts an account it has not seen, and `Joined` only installs a clean entry. `OnChangedRole` on an unknown account is a no-op.

A rejoin inside the carry-over window keeps the old idle clock, so disconnecting and reconnecting does not buy a fresh grace period for free. A rejoin after a longer absence starts present.

Staff, testers and dummy accounts: decide in the design, not in the code after the fact. Ask the owner.

## Re-verifying on a new build

- LabAPI `Player`: do `Position`, `LookRotation`, `Role`, `IsMuted`, `VoiceModule` still exist with the same meaning? Is `Position` still zero for non-FPC roles?
- Catalog: is `SendingVoiceMessage` still `hot`, and is its rate still 100/s per speaker?
- On a live server with one client: stand still for 2x `AfkAfterSeconds` and check the AFK bucket grows; speak and check it stops.
- Same test across a round restart and a respawn: stand still through both and check the AFK bucket grows from the first reading after spawn, not 2 minutes later. Log how long restart plus reconnect really takes on that server before tuning the carry-over window.
- `Joined` re-firing for every player at a restart is derived from the code, so confirm it in a log before relying on it.
