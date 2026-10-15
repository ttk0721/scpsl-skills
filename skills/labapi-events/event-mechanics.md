# LabAPI event mechanics (reference)

Verified against LabAPI 1.1.7 / SCP:SL 14.2.7 (2026-10). Companion to `SKILL.md`. Per-event facts
(frequency, cancel, ignored setters, raise sites) live in `events-catalog.md` and
`tools/catalog/labapi-events.json`. This file covers the machinery all events share.

## 1. Anatomy

- 377 events in the shipped `LabApi.dll`, spread over 15 static classes in `LabApi.Events.Handlers`:
  `PlayerEvents` (186), `ServerEvents` (60), `Scp079Events` (24), `Scp096Events`, `Scp173Events` (14 each),
  `ObjectiveEvents` (12), `Scp049Events` (11), `Scp106Events`, `Scp3114Events`, `Scp914Events` (10 each),
  `Scp939Events` (9), `Scp127Events`, `WarheadEvents` (6 each), `Scp0492Events` (4), `ScpEvents` (1).
- Each is a `public static event LabEventHandler<TArgs>?` (or the no-arg `LabEventHandler`).
- A source generator emits, per event: a public invoker `XxxEvents.OnYyy(args)`, a virtual no-op
  `CustomEventsHandler.On{Category}{Event}` (Category = class name minus `Events`, e.g.
  `Scp173Events.Snapping` → `OnScp173Snapping`, `ServerEvents.WaitingForPlayers` → `OnServerWaitingForPlayers`),
  and one registration line in `CustomHandlersManager`.
- No `HasSubscribers`, `IsSubscribed` or subscriber count exists. Game code cannot see whether
  anyone listens.
- Declared but never raised by the 14.2.7 game: `PlayerEvents.SearchedAmmo`, `SearchedArmor`,
  `ServerEvents.CassieAnnouncing`, `CassieAnnounced`. `PlayerEvents.AimingWeapon` is commented out
  in the LabAPI source and absent from the DLL. Use `AimedWeapon`.

## 2. Subscription styles

| | Style A: static `+=` | Style B: `CustomEventsHandler` |
|---|---|---|
| Wiring | `PlayerEvents.Joined += OnJoined;` | subclass, `override` the `On{Category}{Event}` methods, `CustomHandlersManager.RegisterEventsHandler(instance)` |
| What gets hooked | exactly what you add | only methods that **override** the base virtual. A same-named method with another signature, or one declared `new`, is silently ignored |
| Unwire | `-= OnJoined` (method group works: equality is target + method) | `UnregisterEventsHandler(instance)` |
| Registration cost | one CAS loop | one `GetMethods()` reflection pass **per event** (377 passes). Estimated ~10⁵ compares and ~1 MB transient garbage (UNVERIFIED estimate). Fine once at `Enable()`, wrong per round or per player |
| Double registration | adds a second copy; it runs twice | same instance twice: the first overridden event is added again, then a dictionary `Add` throws `ArgumentException` and the rest are skipped. One stale hook survives every later `Unregister`. Distinct instances do not collide |
| Lambdas | cannot be removed unless you kept the delegate instance | n/a |

Both styles end in the same multicast field. There is no separate dispatcher and no per-handler priority.

Leaks and wiring traps (both styles):

- **Short-lived owners.** Static events hold a strong reference to the handler's target. A per-round
  or per-player object that subscribes and never unsubscribes stays alive and keeps running after
  its round, and the handler list grows every round (more cost per raise). Subscribe once from a
  long-lived owner and route by id, or unsubscribe in `Left` / `RoundRestarted`.
- **Partial subscription.** If `Enable()` throws midway, the plugin is not marked enabled, but the
  subscriptions made before the throw stay live. Subscribe as the **last** step of `Enable()`.
- **`Disable()` is never called** by LabAPI 1.1.7 or the game, so `-=` in `Disable()` is harmless but
  does not run. If `Enable()` can ever run twice (a plugin-reload mechanism of your own, say), every
  static subscription doubles unless `Enable()` is idempotent. See designing-scpsl-plugins.
- **Variant delegates poison the event.** `Delegate.Combine` requires identical runtime delegate types
  when both sides are non-null. Subscribing a contravariant `LabEventHandler<EventArgs>` variable:
  on a non-empty event `+=` throws; on an **empty** event it succeeds silently, and then every later
  exact-typed `+=`/`-=` from **any** plugin throws. Subscribe with a method group or an exact
  `LabEventHandler<TArgs>`.

## 3. Invocation path and cost

What happens on every raise:

1. **Game call site** constructs `new XxxEventArgs(...)`. Constructors resolve wrappers
   (`Player.Get(hub)`, `Door.Get`, `RadioItem.Get`, ...): a Unity null check plus a dictionary probe
   that hashes through a native `gameObject` call. A miss **creates** a wrapper. This runs whether or
   not anyone is subscribed.
2. `OnX(args)` → `EventManager.InvokeEvent`: returns if the field is null.
3. `GetInvocationList()`: **one `Delegate[]` allocation per raise** once there is at least one
   subscriber (Mono: `new Delegate[1]` for one subscriber, an array clone for several). The array is a
   snapshot, so `+=`/`-=` inside a handler does not affect the current raise.
4. Each subscriber runs in its own `try/catch` (section 4). No short-circuit on `IsAllowed`.
5. The game reads back whatever fields it chooses (section 5).

| Component | When | Cost |
|---|---|---|
| Args object + wrapper lookups | every raise, **even with zero subscribers** | 1 object (≈24-80 B, estimate) + one probe per wrapper |
| `Delegate[]` | every raise with ≥1 subscriber | ≈40 B for one subscriber (estimate) + copy for N>1 |
| Per subscriber | every raise | type check + delegate call (try region is ~free when not throwing) |
| Caught exception | per throw | throw/unwind, stack-trace string, `Logger.Error` stack walk, two regex passes and a split in `ServerConsole.AddLog`, a closure per console output. Estimated 10-100+ µs (UNVERIFIED). At frame rate this is a TPS problem |

Consequences:

- Unsubscribing from a hot event saves the `Delegate[]` and your handler, **not** the args. Only a
  Harmony patch at the game call site removes args construction (see scpsl-harmony-patching).
- A Harmony prefix returning `false` on `XxxEvents.OnX` disables that event for every plugin and still
  does not save the args. Patch the call site, not the invoker. The invoker is tiny, so Mono may
  inline it and a prefix may not apply reliably (UNVERIFIED). Other mods already prefix some
  invokers (e.g. `OnValidatedVisibility`): check `Harmony.GetPatchInfo` first.
- Args objects are not pooled, but some **members** are pooled containers the game recycles right
  after the raise: `PlayersStayingInHazardEventArgs.AffectedPlayers` (`ListPool`) and
  `WaveRespawningEventArgs.Roles` (`DictionaryPool`). Copy them inside the handler. Never store `ev`.
- Scalar and wrapper members are a snapshot. Mutating them after the handler returns does nothing.

## 4. Exceptions

- Per-subscriber `try/catch`: a throwing handler does not stop the other handlers or the game.
- Mutations made before the throw **stay** (e.g. `IsAllowed = false` set, then NRE).
- The log header names the **last-registered** subscriber's method, not the thrower: LabAPI formats
  the message from the whole multicast delegate. The stack trace underneath is correct. Read the
  trace, not the header. The header is right only when the last-registered subscriber is the one that
  threw. Which declaring type the header prints for a combined delegate is UNVERIFIED.
- The log prefix reads `[LabApi]`, not your plugin name.
- Not protected by that `try/catch`: exceptions in the args constructor (game call site), exceptions
  the game throws later because you wrote an invalid value into the args (e.g. a nonsensical
  `NewRole`), and `StackOverflowException` from unbounded re-entrancy (uncatchable).
- Rule: wrap handler bodies in your own `try/catch` with your own (rate-limited) logging when
  attribution matters, and never let a hot handler throw.
- The `catch` is itself a hot path. Each caught-and-logged exception pays the full cost in the table
  in section 3. A bare `Logger.Error("..." + e)` there turns one persistent fault (a null field, a
  bad config value) into one log line plus stack trace per raise. For `Hurting`/`Hurt` that is every
  damage event, and per frame per victim under DoT effects. Log through a token bucket per key
  (`RateLimitedLog.Error(key, () => ...)` from SlKit, which also builds the string lazily), or keep a
  counter and a timestamp. See section 11 for the template.

## 5. Cancellation and setters

`ICancellableEvent` is only `bool IsAllowed { get; set; }`. Every args constructor sets it `true`.

- **No short-circuit.** All handlers run even after one wrote `false`.
- **Last writer wins.** The game reads `IsAllowed` once, after all handlers return.
- Writing `true` (or `IsAllowed = !cond`) can only **un-cancel** an earlier plugin. Write `false` only,
  and only when you mean to deny, unless overriding someone is the deliberate goal.
- `if (!ev.IsAllowed) return;` sees cancels by **earlier** handlers only. A later handler can still
  cancel after you ran, so irreversible side effects belong in the `-ed` event where one exists.
- A settable property is not a contract. It matters only if the game re-reads it after the raise.
  The catalog's `ignoredSetters` lists the known dead ones.

Representative consumers:

| Event | What the game does after the raise |
|---|---|
| `PlayerEvents.ChangingRole` | if allowed, re-reads `NewRole`, `ChangeReason`, `SpawnFlags`. Cancelling during death or escape is dangerous: the caller continues as if the role changed, so `Death` / `Escaped` still fire with the player unchanged |
| `PlayerEvents.Hurting` | if allowed, applies the game's **local** damage handler. Reassigning `ev.DamageHandler` is ignored. Mutating the same object (cast to `StandardDamageHandler`, change `Damage`) works. Not raised for GodMode/SpawnProtected victims |
| `PlayerEvents.InteractingDoor`, unlocked door | cancel → return, no `InteractedDoor`. Else `CanOpen` is re-read |
| `PlayerEvents.InteractingDoor`, locked door (`DoorVariant.TryResolveLock`) | raised with `CanOpen = false`. Lock resolution returns `ev.CanOpen`, so **`CanOpen = true` opens a locked door**. Cancelling falls into the lock-denied branch: denied cooldown and `LockBypassDenied` feedback still run and **`InteractedDoor(canOpen: false)` fires**. The event is not raised twice for the same interaction |
| `PlayerEvents.SendingVoiceMessage` | cancel → packet dropped for everyone. Else `Message` (a by-ref `VoiceMessage` struct) is what gets fanned out. `Message.Channel` set here is overwritten per receiver, so it is not honoured |
| `PlayerEvents.ReceivingVoiceMessage` | per listener: if allowed, sends that listener's `Message`. The message is a shared local, so non-`Channel` edits can leak to later listeners (UNVERIFIED). `Data` is a shared `byte[]`: never mutate in place |
| `ServerEvents.CommandExecuting` | re-reads `Arguments`, `Sender`, `Command`, but the "command exists" flag is computed **before** the event, so setting `Command` on an unknown command does not run it |
| `ServerEvents.CommandExecuted` | `-ed` but read back: `Response`, `ExecutedSuccessfully` (not on `CommandType.Client`) |
| `PlayerEvents.ValidatedVisibility` | not cancellable. `IsVisible` decides whether the target's position goes to that receiver |
| `PlayerEvents.UsingRadio` | if allowed, re-reads `Drain`. Cancel = no drain this frame |
| `ServerEvents.WaveRespawning` | cancelling still burns a respawn token and resets wave timers (see scpsl-game-lifecycle) |

## 6. `-ing` / `-ed` pairing in practice

The guideline says `-ing` is cancellable and `-ed` means "it happened". In practice:

| Pattern | Example |
|---|---|
| `-ed` skipped when `-ing` cancelled (usual) | `ChangedRole`, `Hurt`, `CommandExecuted`, `UsedRadio`. A cancelled `SendingVoiceMessage` suppresses all `ReceivingVoiceMessage` |
| `-ed` without an allowed `-ing` | locked door: `InteractedDoor(canOpen: false)` after a cancelled lock-path `InteractingDoor`. The catalog also notes a lock-denied path where it fires without `InteractingDoor` having been raised for that call |
| same cancel, two outcomes | `InteractingDoor`: unlocked path = silent return; lock path = denial feedback + `InteractedDoor` |
| `-ed` when nothing changed | `Hurt` fires even when damage output was `Nothing` |
| chained pre-event after partial commit | `Dying` is raised **after** damage was applied and `Hurt` fired. Cancelling it leaves HP where `ApplyDamage` put it (whether ≤0 HP is possible: UNVERIFIED) |
| N `-ing` per one `-ed` | `IdlingTesla` per gate per player in range each `FixedUpdate` until one is allowed; `IdledTesla` / `TriggeredTesla` once on transition |
| `-ed` is not "completed" | `ReportedCheater` fires right after the upload thread starts, before delivery |
| `-ed` on the failure path | `CommandExecuted` also fires when the command threw |
| order inside one action | death: `Hurting` → `Hurt` → `Dying` → ragdoll/drop → role set to Spectator → `Death` (player is already Spectator in `Death`). Role change: `ChangingRole` → `Spawning` → `Spawned` → `ReceivingLoadout` → `ReceivedLoadout` → `ChangedRole` (only `ChangedRole` sees the filled inventory; `Spawning`/`Spawned` occur only for roles with a spawnpoint handler) |

## 7. Thread affinity

| Event(s) | Thread |
|---|---|
| `PlayerEvents.PreAuthenticating`, `PreAuthenticated` | **`ConnectionRequestThread`**, a single background thread polling every 30 ms. Not raised at all while connections are delayed (boot, round restart) |
| `ServerEvents.Shutdown`, `RoundRestarted` | main thread normally. A legacy-format central-server `Restart`/`RoundRestart` action reaches them from the server-list thread. How often that happens is UNVERIFIED |
| everything else checked (voice, Mirror message handlers, console/RA commands, reports, Update/FixedUpdate sites) | main thread (not an exhaustive audit of every thread the game starts) |

In pre-auth handlers:

- Allowed: the args' own strings/bytes, your own thread-safe collections (`ConcurrentQueue`,
  `ConcurrentDictionary`, immutable snapshots swapped by reference), `Logger.*` (the game logs from
  that thread itself).
- Forbidden: any `UnityEngine.Object`, `Player.Get` (its hash calls `gameObject`), `Player.List` /
  `Player.Dictionary` (a plain `Dictionary` mutated on the main thread), game singletons, and MEC
  `Timing` (conservative: MEC thread-safety was not checked, UNVERIFIED). Hand work to the main thread through a concurrent queue that your main-thread
  clock drains.
- A slow handler delays **every** queued join, because one thread serves them all.

The event add/remove itself is atomic. Your handler state is not: synchronise anything a pre-auth
handler shares with main-thread handlers. See scpsl-main-thread-and-timing.

## 8. Ordering across plugins

- Invocation order = subscription order (`Delegate.Combine` appends).
- Plugins are enabled in ascending `Priority` (`LoadPriority : byte`): Highest = 64, High = 96,
  Medium = 128 (default), Low = 160, Lowest = 192. **Lower byte = enabled earlier = runs first.**
- So a `Lowest` plugin gets the **last word** on `IsAllowed` and other mutable args, which is the
  opposite of the intuitive reading.
- Ties follow plugin-file enumeration order, which is not guaranteed alphabetical.
- A subscription made later (lazily, in `WaitingForPlayers`, per round) goes to the end regardless
  of priority.
- LabAPI's own wrapper subscriptions come first (made before plugins load).
- On EXILED servers, EXILED's events are a separate layer, mostly raised by its own Harmony patches.
  Its transpilers can re-emit or remove LabAPI raises (the door patch re-emits `OnInteractingDoor`
  and truncates the lock path before its LabAPI raise), so LabAPI door semantics there are EXILED's.
  Test with EXILED loaded if you target mixed servers.

## 9. Re-entrancy

`InvokeEvent` has no guard, and calling a game API from a handler raises nested events synchronously.

- `ev.Player.SetRole(x)` inside `ChangingRole`: the inner call raises `ChangingRole`/`ChangedRole` and
  applies `x`. Then the outer call resumes with **its** role and overwrites `x`, and a second
  `ChangedRole` fires. Unconditional, it recurses until the stack overflows.
- Same for dealing damage inside `Hurting`.
- Fix, in order of preference: edit the args (`ev.NewRole = x`); defer to the next frame (one
  clock / `Timing.CallDelayed(0, ...)`); or a guard flag **plus** `ev.IsAllowed = false` on the outer
  raise.

## 10. Hot events at a glance

Rates are per server. "Estimate" marks derived numbers, not measurements.

| Event | Raised | Order of magnitude |
|---|---|---|
| `PlayerEvents.ValidatedVisibility` | per receiver × target per position tick | 60 players at 60 Hz ≈ 212 000/s (estimate) |
| `PlayerEvents.ReceivingVoiceMessage` | per voice packet × every hub with a voice role (dummies included), **args built even with no subscriber** | 10 speakers × 100 × 60 listeners ≈ 60 000/s (estimate) |
| `PlayerEvents.SendingVoiceMessage` | per voice packet, once | **100 packets/s per speaker** (480-sample frames at 48 kHz = 10 ms). Hard cap 256/s per speaker from the game's rate limit |
| `PlayerEvents.UsingRadio` / `UsedRadio` | every frame per switched-on radio with battery | frame rate × radios |
| `PlayerEvents.StayingInHazard` | every frame per hazard with occupants | frame rate × hazards |
| `PlayerEvents.IdlingTesla`, `TriggeringTesla` | `FixedUpdate` per gate × players in range | |
| `Scp173Events.AddingObserver` / `RemovingObserver` | every frame per SCP-173 × every hub | |
| `PlayerEvents.Escaping` | every frame per player standing in an escape zone | |
| `Scp106Events.ChangingVigor` / `ChangedVigor` | every frame per SCP-106 while stalking or recovering | |
| `PlayerEvents.UsingIntercom` | every frame while someone transmits in intercom range | |
| `PlayerEvents.Hurting` / `Hurt` | per action, but per frame per victim under DoT effects, SCP-3114 strangle, Micro-HID fire. Treat as hot for logging: no logging in the body, rate-limited logging in the `catch` | bursts of tens to hundreds/s |

The verified rate is 100/s (see the `SendingVoiceMessage` entry).

## 11. Gate patterns

A **gate** is whatever keeps the per-raise work near zero: not subscribing, subscribing only while
needed, or an early return that costs one field test. The compiled, linted versions of these
patterns (plus both subscription styles) are in `examples/labapi-events-snippets/`, walked through in
`event-examples.md`.

```csharp
// Attach a hot subscription only while some feature needs it. Main thread only.
internal static class VoiceTap
{
    private static int _users;
    private static readonly Dictionary<int, long> LastSpoke = new Dictionary<int, long>(64);

    public static void Acquire()
    {
        if (_users++ == 0) PlayerEvents.SendingVoiceMessage += OnSending;
    }

    public static void Release()
    {
        if (_users == 0) return;
        if (--_users == 0) PlayerEvents.SendingVoiceMessage -= OnSending;
    }

    public static void ResetRound() => LastSpoke.Clear(); // call from WaitingForPlayers

    // ~100 raises/s per speaker. One probe, one write. No logging, LINQ, closures or strings.
    // Still not allocation-free: the game allocates the args and LabAPI the Delegate[] per raise.
    private static void OnSending(PlayerSendingVoiceMessageEventArgs ev)
    {
        if (ev.Player != null) LastSpoke[ev.Player.PlayerId] = Stopwatch.GetTimestamp();
    }

    public static bool SpokeWithin(int playerId, long ticks) =>
        LastSpoke.TryGetValue(playerId, out long t) && Stopwatch.GetTimestamp() - t <= ticks;
}
```

```csharp
// Pre-auth: background thread. Args and thread-safe state only; hand the rest to the main thread.
private static readonly ConcurrentDictionary<string, byte> Blocked = new ConcurrentDictionary<string, byte>();
private static readonly ConcurrentQueue<string> Admitted = new ConcurrentQueue<string>();

private static void OnPreAuthenticating(PlayerPreAuthenticatingEventArgs ev)
{
    if (Blocked.ContainsKey(ev.UserId)) { ev.IsAllowed = false; return; } // or a Reject* helper on the args
    Admitted.Enqueue(ev.UserId); // drained by a main-thread clock job
}
```

```csharp
// Own try/catch on a frequent event (Hurting here): attribution without a log flood.
private static void OnHurting(PlayerHurtingEventArgs ev)
{
    try
    {
        if (!ev.IsAllowed) return;
        // ... one test, one write ...
    }
    catch (Exception e)
    {
        // Not Logger.Error("..." + e): a persistent fault would log on every damage event.
        // SlKit: token bucket per key, message built only when the bucket allows, suppressed count reported later.
        RateLimitedLog.Error("MyPlugin.Hurting", () => "Hurting handler failed: " + e);
    }
}
```

```csharp
// Re-entrancy-safe role override: edit the args, never call SetRole here.
private static void OnChangingRole(PlayerChangingRoleEventArgs ev)
{
    if (!ev.IsAllowed || ev.NewRole != RoleTypeId.ClassD) return;
    if (GuardQuota.TryTake(ev.Player.UserId)) ev.NewRole = RoleTypeId.FacilityGuard;
}
```

## Re-verifying on a new build

- Re-generate the catalog and diff `tools/catalog/labapi-events.json`: new/removed events, changed
  `frequency`, `hot`, `ignoredSetters`, `pair`.
- LabAPI `EventManager.InvokeEvent`: still `GetInvocationList` per raise? still a per-subscriber
  `try/catch`? still formats the error from the multicast delegate (wrong blame)? does it now
  short-circuit on `IsAllowed`? any `HasSubscribers`-style API?
- LabAPI `CustomHandlersManager`: still one reflection pass per event, still `Dictionary.Add` after
  `AddEventHandler` (double-register leak)?
- LabAPI `PluginLoader`: still `OrderBy(Priority)` ascending; does anything call `Disable()` now?
- Count `public static event` in `LabApi.Events.Handlers` (377 in 1.1.7).
- Game: grep each never-fired invoker (`OnSearchedAmmo`, `OnSearchedArmor`, `OnCassieAnnouncing`,
  `OnCassieAnnounced`) for new callers; check `DoorVariant.TryResolveLock` still returns `ev.CanOpen`
  and still raises `InteractedDoor` on the denied path; `PlayerStats.DealDamage` still applies the
  local handler; `VoiceTransceiver` packet size (480) and rate limit (128 per 0.5 s); which thread
  calls `ProcessConnectionRequest`.
- Pooled args members: grep LabAPI args constructors for `ListPool`/`DictionaryPool`.
- EXILED: list its patches that re-emit LabAPI raises.

## 12. Common Mistakes, detail

- **Voice handler cost.** `SendingVoiceMessage` fires 100 times/s per speaker (480 samples at
  48 kHz), not "~50 packets/s". Each raise allocates the args and a `Delegate[]`: 10 speakers is
  ~1000 raises/s of garbage. Subscribing can still be justified (1 Hz polling is coarse). Keep the
  handler to a lookup plus a write and state the cost. `ReceivingVoiceMessage` fires per packet
  **per listener**: avoid it.
- **Locked doors.** On a locked door the game raises `InteractingDoor` with `CanOpen = false` and
  takes `ev.CanOpen` as the lock verdict, so `ev.CanOpen = true` opens it. Cancelling there still
  plays the denial feedback and fires `InteractedDoor(canOpen: false)`. `Door.IsOpened` ignores
  locks and permissions, so cancel-then-toggle is wrong.
- **`ev.IsAllowed = !banned`** un-cancels other plugins. Write `if (banned) ev.IsAllowed = false;`.
- **`ev.DamageHandler` in `Hurting`.** Reassigning is ignored by the game. Mutate the existing
  handler object.
- **Unbounded `Log.Error("..." + e)` in a frequent handler's `catch`.** A persistent fault logs
  once per raise, and each line costs a stack walk, regexes and a stack-trace string.
  `Hurting`/`Hurt` count: per action normally, per frame per victim under DoT, SCP-3114 strangle
  or Micro-HID fire. Log through a keyed token bucket (`RateLimitedLog.Error(key, () => ...)`,
  SlKit) or a counter plus timestamp (template in section 11).
- **Subscribing before fallible setup.** If `Enable()` throws, earlier subscriptions stay live and
  `Disable()` never runs (designing-scpsl-plugins), so subscribe last.
- **Quick Reference facts, expanded.** The game builds args (`Player.Get`...) before checking for
  subscribers, so zero subscribers still pay that. With at least one subscriber every raise
  allocates a `Delegate[]`. Each handler has its own try/catch: edits made before a throw stay,
  and the log header names the last-registered handler, so read the stack trace. `Priority` is a
  byte, lower enables first, so `Lowest` gets the last word. `-ing`/`-ed` are not 1:1: `Hurt`
  fires even when the damage output was `Nothing`. `CustomEventsHandler` registration costs one
  reflection pass per event.
