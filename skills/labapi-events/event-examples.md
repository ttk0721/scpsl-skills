# LabAPI event examples (walkthrough)

Verified against LabAPI 1.1.7 / SCP:SL 14.2.7 (2026-10). Companion to `SKILL.md`. Every snippet below
is an excerpt of `examples/labapi-events-snippets/`, a real project that builds with 0 errors and lints
clean with `scpsl-lint`. Facts behind each pattern: `event-mechanics.md` (section numbers in brackets).

| File | Shows |
|---|---|
| `EventsSnippetsPlugin.cs` | lifecycle: fallible setup, subscribe last, idempotent `Enable`, release on `Shutdown` |
| `StaticEventWiring.cs` | style A: method groups, `-=` for every `+=`, per-round reset |
| `CustomHandlerWiring.cs` | style B: overrides only, register once, unconditional unregister |
| `VoiceActivityGate.cs` | hot event: subscribe only while needed, one-lookup handler |
| `CancellationRules.cs` | cancel with `false` only, mutate the same object, honoured setters, no re-entrancy |
| `PreAuthBridge.cs` | off-thread event: args plus concurrent state, queue to the main thread |

## 1. Both subscription styles, unsubscribed correctly [2]

Pick one style per handler group. The example wires both only to show both.

Style A. Method groups, so `-=` removes exactly what `+=` added (delegate equality is target plus
method). A lambda would be a new delegate each time and could never be removed.

```csharp
public void Subscribe()
{
    if (_subscribed) return;                       // idempotent: no second copy
    ServerEvents.WaitingForPlayers += OnWaitingForPlayers;
    PlayerEvents.Hurting += _rules.OnHurting;      // method group on an instance: still removable
    _subscribed = true;
}

public void Unsubscribe()                          // unconditional: removing an absent handler is a no-op,
{                                                  // so this is also the undo path of a failed Enable
    ServerEvents.WaitingForPlayers -= OnWaitingForPlayers;
    PlayerEvents.Hurting -= _rules.OnHurting;
    _subscribed = false;
}
```

Style B. Each `override` is one subscription. A same-named method with another signature, or marked
`new`, is silently never called. Registering costs one reflection pass per event, so it happens once
from the plugin, never per round or per player.

```csharp
internal sealed class RoundNotesHandler : CustomEventsHandler
{
    public override void OnServerWaitingForPlayers() { /* per-round reset */ }
    public override void OnPlayerJoined(PlayerJoinedEventArgs ev) { /* once per connection */ }
}

public void Register()
{
    if (_registered) return;       // the same instance twice throws partway and leaves a stale hook
    CustomHandlersManager.RegisterEventsHandler(_handler);
    _registered = true;
}
public void Unregister() => CustomHandlersManager.UnregisterEventsHandler(_handler); // unconditional
```

Lifecycle around both (`EventsSnippetsPlugin.Enable`): build everything that can throw first,
subscribe last, and on any exception call `Release()` and rethrow. LabAPI and the game never call
`Disable()`, so `Release()` also runs from `ServerEvents.Shutdown`.

## 2. Gated hot-event handler [3, 10]

`SendingVoiceMessage` is raised about 100 times per second per speaker and allocates args plus a
`Delegate[]` per raise. The gate keeps the subscription alive only while a player is watched:

```csharp
public void Watch(int playerId)
{
    if (!_watched.ContainsKey(playerId)) _watched[playerId] = new Stamp();   // allocate per watch
    Hook();
}
public void Unwatch(int playerId)
{
    if (_watched.Remove(playerId) && _watched.Count == 0) Unhook();          // idle: stop paying
}

private void OnSending(PlayerSendingVoiceMessageEventArgs ev)
{
    if (_watched.Count == 0) return;                                         // cheap early return
    try
    {
        Stamp stamp;
        if (ev.Player != null && _watched.TryGetValue(ev.Player.PlayerId, out stamp))
            stamp.Ticks = Stopwatch.GetTimestamp();                          // one lookup, one write
    }
    catch (Exception e)
    {
        RateLimitedLog.Error("snippets.voice", () => "SendingVoiceMessage handler failed: " + e);
    }
}
```

What it saves and what it does not: while idle there is no handler call and no `Delegate[]`. The game
still builds the args at its call site (zero subscribers included), and only a Harmony patch at that
call site removes that (scpsl-harmony-patching). While busy the cost is the args, the `Delegate[]`, one
lookup and one write. The handler allocates nothing itself; the `try` region is free until it throws, and
the closure in the `catch` is built only on a failure. `Clear()` runs at `WaitingForPlayers` because the
previous round's players are gone; `Detach()` removes only the subscription, which is safe from a
`Shutdown` raised on another thread.

`HotEventAudit` (SlKit) lists who is subscribed to the hot events, which is how you confirm the gate
really unsubscribed.

## 3. Cancellation and setters [5]

```csharp
// Hurting: cancel with false only, and mutate the object the game holds.
if (!ev.IsAllowed) return;                       // sees only EARLIER handlers' cancels
if (_shielded.Contains(victim)) { ev.IsAllowed = false; return; }
if (_softened.Contains(victim) && ev.DamageHandler is StandardDamageHandler standard)
    standard.Damage *= 0.5f;                     // same object. "ev.DamageHandler = new ..." is ignored

// InteractingDoor: CanOpen is re-read, and on a locked door it is the lock verdict.
if (!ev.CanOpen && _masters.Contains(id)) ev.CanOpen = true;

// ChangingRole: edit the args. ev.Player.SetRole(...) here re-enters ChangingRole.
ev.NewRole = RoleTypeId.FacilityGuard;
```

Why each line is the way it is:

- `ev.IsAllowed = !banned` writes `true` for everyone not banned, which un-cancels any earlier plugin
  (the game reads the flag once, after all handlers). `if (banned) ev.IsAllowed = false;` cannot.
- The early `if (!ev.IsAllowed) return;` is an optimisation and a courtesy, not a guarantee. A later
  handler can still cancel after you ran, so keep side effects reversible here and put irreversible ones in
  the `-ed` event where one exists.
- Reassigning `ev.DamageHandler` compiles, looks right and does nothing: the game applies its local
  handler. `scpsl-lint` SL003 flags it. Mutating the shared object (a cast, then a field write) works,
  and later `Hurt`/`Dying`/`Death` handlers see the change too.
- The locked-door verdict is `CanOpen`, not `IsAllowed`. Cancelling there still plays the denial feedback
  and fires `InteractedDoor(canOpen: false)`; toggling `Door.IsOpened` afterwards ignores locks and
  permissions.
- Do not call game APIs that re-raise the same event from inside its handler. If a handler must call one,
  use a re-entrancy guard and cancel the outer raise (`event-mechanics.md` section 9).

## 4. Events raised off the main thread [7]

`PreAuthenticating` runs on the single `ConnectionRequestThread`. The handler touches the args, a
`ConcurrentDictionary` and a bounded `ConcurrentQueue`; a clock job moves the result to the main thread.

```csharp
private void OnPreAuthenticating(PlayerPreAuthenticatingEventArgs ev)      // background thread
{
    try
    {
        string userId = ev.UserId;
        if (userId == null) return;
        if (_blocked.ContainsKey(userId)) { ev.RejectCustom("Blocked by server rules."); return; }
        if (!ev.IsAllowed || !ev.CanJoin) return;
        if (Interlocked.Increment(ref _queued) > MaxQueued) { Interlocked.Decrement(ref _queued); return; }
        _admitted.Enqueue(userId);                                         // no Player, no Unity, no MEC
    }
    catch (Exception e) { RateLimitedLog.Error("snippets.preauth", () => "PreAuthenticating handler failed: " + e); }
}

private void Drain()                                                       // SlClock job: main thread
{
    string userId;
    for (int i = 0; i < DrainPerRun && _admitted.TryDequeue(out userId); i++)
    {
        Interlocked.Decrement(ref _queued);
        if (_expectedJoins.Count < MaxExpected)                            // bounded: no-shows must not grow it
            _expectedJoins.Add(userId);                                    // plain HashSet: main thread only
    }
}
```

- `Player.Get` is off limits there because its lookup hashes through a native `gameObject` call and the
  wrapper dictionary is mutated on the main thread. The queue carries plain strings.
- The queue is bounded so a connection flood cannot grow memory while the main thread is busy.
- One thread serves every join, so a slow handler delays all of them. Keep it to tests and enqueues.
- `RateLimitedLog` and `Logger.*` are usable from that thread; the game logs from it itself.
- Shared state you add is yours to synchronise: event add/remove is atomic, handler state is not.
- `ServerEvents.Shutdown` and `RoundRestarted` can also arrive from the server-list thread on a legacy
  central-server action (how often is UNVERIFIED). The example's `Release()` therefore removes subscriptions
  and flags only, never per-round collections. `MainThread.Post(...)` (SlKit) is the alternative to a
  hand-rolled queue when the per-item cost of a closure does not matter.

## Re-verifying on a new build

- Rebuild `examples/labapi-events-snippets/` against the new `LabApi.dll` and game assemblies: a renamed
  `On{Category}{Event}` override, a changed args member or a removed event fails the build.
- Re-check against the event catalog that `SendingVoiceMessage` is still hot, `Hurting` still lists
  `DamageHandler` in `ignoredSetters`, and `PreAuthenticating` is still the off-thread pair.
- `InteractingDoor`: still `CanOpen` as the lock verdict on the locked path. `ChangingRole`: still re-reads
  `NewRole` when allowed.
- `EventManager.InvokeEvent`: still one `Delegate[]` per raise and a snapshot (changing subscriptions during a
  raise does not affect it)? Still no `IsAllowed` short-circuit?
- `CustomHandlersManager.RegisterEventsHandler`: does a second registration of the same instance still throw?
- Did anything start calling `Plugin.Disable()`? If so, the `Shutdown` release can become a second path.
